using Application.Common.DTOs.Books;
using Application.Common.DTOs.Tags;
using Application.Common.Exceptions;
using Application.Interfaces.Managers;
using Application.Interfaces.Repositories;
using Application.Interfaces.Services;
using AutoMapper;
using Domain.Entities;
using Microsoft.AspNetCore.WebUtilities;

namespace Application.Services;

public class BookService : IBookService
{
    private readonly IMapper _mapper;
    private readonly IBookRepository _bookRepository;
    private readonly IUserRepository _userRepository;
    private readonly IBookBlobStorageManager _bookBlobStorageManager;

    public BookService(IMapper mapper, IBookRepository bookRepository,
                       IUserRepository userRepository, 
                       IBookBlobStorageManager bookBlobStorageManager)
    {
        _mapper = mapper;
        _bookRepository = bookRepository;
        _userRepository = userRepository;
        _bookBlobStorageManager = bookBlobStorageManager;
    }


    public async Task CreateBookAsync(string email, BookInDto bookInDto)
    {
        var user = await _userRepository.GetAsync(email, trackChanges: true);
        if (await _bookRepository.ExistsAsync(user.Id, bookInDto.Guid))
        {
            const string message = "A book with this id already exists";
            throw new CommonErrorException(400, message, 0);
        }

        if (!await UserHasEnoughStorageSpaceAvailable(user))
        {
            const string message = "Book storage space is insufficient";
            throw new CommonErrorException(426, message, 5);
        }

        var book = _mapper.Map<Book>(bookInDto);
        book.BookId = bookInDto.Guid;

        foreach (var tag in bookInDto.Tags)
        {
            AddTagDtoToBook(book, tag, user);
        }

        user.Books.Add(book);
        await _bookRepository.SaveChangesAsync();
    }

    private async Task<bool> UserHasEnoughStorageSpaceAvailable(User user)
    {
        var usedStorage = await _bookRepository.GetUsedBookStorage(user.Id);
        return usedStorage <= user.BookStorageLimit;
    }

    public async Task<IList<BookOutDto>> GetBooksAsync(string email)
    {
        var user = await _userRepository.GetAsync(email, trackChanges: false);

        var books = _bookRepository.GetAllAsync(user.Id).ToList();
        await _bookRepository.LoadRelationShipsAsync(books);

        return books.Select(book => _mapper.Map<BookOutDto>(book)).ToList();
    }

    public async Task DeleteBooksAsync(string email, IEnumerable<Guid> bookGuids)
    {
        var user = await _userRepository.GetAsync(email, trackChanges: true);

        foreach (var bookGuid in bookGuids)
        {
            var book = user.Books.SingleOrDefault(book => book.BookId == bookGuid);
            if (book == null)
            {
                const string message = "No book with this id exists";
                throw new CommonErrorException(404, message, 4);
            }

            await _bookRepository.LoadRelationShipsAsync(book);
            _bookRepository.DeleteBook(book);
            await _bookBlobStorageManager.DeleteBookBlob(book.BookId);
            
            if(book.HasCover)
                await _bookBlobStorageManager.DeleteBookCover(book.BookId);
        }

        await _bookRepository.SaveChangesAsync();
    }

    public async Task UpdateBookAsync(string email, BookForUpdateDto bookUpdateDto)
    {
        var user = await _userRepository.GetAsync(email, trackChanges: true);
        var book = user.Books.SingleOrDefault(book => book.BookId == bookUpdateDto.Guid);
        if (book == null)
        {
            const string message = "No book with this id exists";
            throw new CommonErrorException(404, message, 4);
        }
        await _bookRepository.LoadRelationShipsAsync(book);
        

        var dtoProperties = bookUpdateDto.GetType().GetProperties();
        foreach (var dtoProperty in dtoProperties)
        {
            // Manually handle certain properties
            switch (dtoProperty.Name)
            {
                case "Guid":
                    continue;     // Can't modify the GUID
                case "Tags":
                    MergeTags(bookUpdateDto.Tags, book, user);
                    continue;
            }
            
            // Update any other property via reflection
            var value = dtoProperty.GetValue(bookUpdateDto);
            SetPropertyOnBook(book, dtoProperty.Name, value);
        }

        await _bookRepository.SaveChangesAsync();
    }

    public async Task AddBookBinaryData(string email, Guid guid, MultipartReader reader)
    {
        var user = await _userRepository.GetAsync(email, trackChanges: true);
        var book = user.Books.SingleOrDefault(book => book.BookId == guid);
        if (book == null)
        {
            const string message = "No book with this id exists";
            throw new CommonErrorException(404, message, 4);
        }

        try
        {
            await _bookBlobStorageManager.UploadBookBlob(guid, reader);
        }
        catch (Exception _)
        {
            // If uploading the book's data fails, make sure to remove the book
            // from the SQL Database, so that no invalid book exist
            await _bookRepository.LoadRelationShipsAsync(book);
            _bookRepository.DeleteBook(book);
            await _bookRepository.SaveChangesAsync();

            throw;
        }
    }

    public async Task<Stream> GetBookBinaryData(string email, Guid guid)
    {
        var user = await _userRepository.GetAsync(email, trackChanges: true);
        var book = user.Books.SingleOrDefault(book => book.BookId == guid);
        if (book == null)
        {
            const string message = "No book with this id exists";
            throw new CommonErrorException(404, message, 4);
        }

        return await _bookBlobStorageManager.DownloadBookBlob(guid);
    }

    public async Task<Stream> GetBookCover(string email, Guid guid)
    {
        var user = await _userRepository.GetAsync(email, trackChanges: true);
        var book = user.Books.SingleOrDefault(book => book.BookId == guid);
        if (book == null)
        {
            const string message = "No book with this id exists";
            throw new CommonErrorException(404, message, 4);
        }

        return await _bookBlobStorageManager.DownloadBookCover(guid);
    }

    public async Task DeleteBookCover(string email, Guid guid)
    {
        var user = await _userRepository.GetAsync(email, trackChanges: true);
        var book = user.Books.SingleOrDefault(book => book.BookId == guid);
        if (book == null)
        {
            const string message = "No book with this id exists";
            throw new CommonErrorException(404, message, 4);
        }

        await _bookBlobStorageManager.DeleteBookCover(guid);
    }

    public async Task<string> GetFormatForBook(string email, Guid guid)
    {
        var user = await _userRepository.GetAsync(email, trackChanges: true);
        var book = user.Books.SingleOrDefault(book => book.BookId == guid);
        if (book == null)
        {
            const string message = "No book with this id exists";
            throw new CommonErrorException(404, message, 4);
        }

        return book.Format;
    }

    public async Task ChangeBookCover(string email, Guid guid, MultipartReader reader)
    {
        var user = await _userRepository.GetAsync(email, trackChanges: true);
        var book = user.Books.SingleOrDefault(book => book.BookId == guid);
        if (book == null)
        {
            const string message = "No book with this id exists";
            throw new CommonErrorException(404, message, 4);
        }

        var coverSize = await _bookBlobStorageManager.ChangeBookCover(guid, reader);
        book.CoverSize = coverSize;

        await _bookRepository.SaveChangesAsync();
    }
    
    private void SetPropertyOnBook(Book book, string property, object value)
    {
        var bookProperty = book.GetType().GetProperty(property);
        if (bookProperty == null)
        {
            var message = "Book contains no property called: " + property;
            throw new CommonErrorException(400, message, 0);
        }
        
        bookProperty.SetValue(book, value);
    }

    private void MergeTags(ICollection<TagInDto> newTags, Book book, User user)
    {
        RemoveBookTagsWhichDontExistInNewTags(book, newTags);
        
        foreach (var tag in newTags)
        {
            // If book already has the tag, update it
            var existingTag = book.Tags.SingleOrDefault(t => t.TagId == tag.Guid);
            if (existingTag != null)
            {
                existingTag.Name = tag.Name;
                continue;
            }
            
            AddTagDtoToBook(book, tag, user);
        }
    }
    
    /// When a book is updated, a list of tags is sent with it. This list of tags
    /// is the "source of truth" and contains all tags that the book owns.
    /// If the database book contains tags that the updated list of tags does not
    /// contain, those old tags shall be deleted
    private void RemoveBookTagsWhichDontExistInNewTags(Book book, 
                                                       ICollection<TagInDto> newTags)
    {
        var tagsToRemove = new List<Tag>();
        foreach (var tag in book.Tags)
        {
            if (newTags.All(t => t.Guid != tag.TagId))
                tagsToRemove.Add(tag);
        }

        foreach (var tag in tagsToRemove) {  book.Tags.Remove(tag); }
    }

    private void AddTagDtoToBook(Book book, TagInDto tag, User user)
    {
        // Return if the book already owns the tag
        if (book.Tags.SingleOrDefault(t => t.TagId == tag.Guid) != null)
            return;
        
        // If the tag already exists, just add it to the book
        var existingTag = user.Tags.SingleOrDefault(t => t.TagId == tag.Guid);
        if (existingTag != null)
        {
            book.Tags.Add(existingTag);
            return;
        }
        
        // If the book already has a tag with the same name, throw
        if (book.Tags.Any(t => t.Name == tag.Name))
        {
            var message = "A tag with this name already exists";
            throw new CommonErrorException(400, message, 6);
        }
        
        // Create the tag from scratch
        var newTag = _mapper.Map<Tag>(tag);
        newTag.UserId = user.Id;
        book.Tags.Add(newTag);
    }
}