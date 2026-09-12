using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Baihua.Data;
using Baihua.Data.Entities;
using Baihua.Modules.Family.Services;
using Xunit;

namespace Baihua.Family.Tests.Services;

public class LearnerServiceTests
{
    private static DbContextOptions<FamilyDbContext> CreateInMemoryOptions(string databaseName)
    {
        return new DbContextOptionsBuilder<FamilyDbContext>()
            .UseInMemoryDatabase(databaseName)
            .Options;
    }

    private static IDbContextFactory<FamilyDbContext> CreateFactory(DbContextOptions<FamilyDbContext> options)
    {
        return new InMemoryDbContextFactory<FamilyDbContext>(options);
    }

    private class InMemoryDbContextFactory<TContext> : IDbContextFactory<TContext> where TContext : DbContext, new()
    {
        private readonly DbContextOptions<TContext> _options;
        public InMemoryDbContextFactory(DbContextOptions<TContext> options) => _options = options;
        public TContext CreateDbContext() => (TContext)Activator.CreateInstance(typeof(TContext), _options)!;
        public Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
    }

    [Fact]
    public async Task GetAllAsync_EmptyDatabase_ReturnsEmpty()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        await using var db = new FamilyDbContext(options);
        var mockLogger = new Mock<ILogger<LearnerService>>();
        var factory = CreateFactory(options);
        var service = new LearnerService(factory, mockLogger.Object);

        var result = await service.GetAllAsync();

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAllAsync_MultipleLearners_ReturnsOrderedList()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        await using var db = new FamilyDbContext(options);
        db.LearnerProfiles.AddRange(
            new LearnerProfile { Id = 2, Name = "Alice", AvatarEmoji = "👧", Color = "#ff6b6b", IsDefault = false },
            new LearnerProfile { Id = 1, Name = "Bob", AvatarEmoji = "👦", Color = "#4ecdc4", IsDefault = true }
        );
        await db.SaveChangesAsync();

        var mockLogger = new Mock<ILogger<LearnerService>>();
        var factory = CreateFactory(options);
        var service = new LearnerService(factory, mockLogger.Object);

        var result = await service.GetAllAsync();

        Assert.Equal(2, result.Count);
        Assert.Equal("Bob", result[0].Name);
        Assert.Equal("Alice", result[1].Name);
    }

    [Fact]
    public async Task GetDefaultAsync_EmptyDatabase_ReturnsNull()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        await using var db = new FamilyDbContext(options);
        var mockLogger = new Mock<ILogger<LearnerService>>();
        var factory = CreateFactory(options);
        var service = new LearnerService(factory, mockLogger.Object);

        var result = await service.GetDefaultAsync();

        Assert.Null(result);
    }

    [Fact]
    public async Task GetDefaultAsync_HasDefault_ReturnsDefault()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        await using var db = new FamilyDbContext(options);
        db.LearnerProfiles.AddRange(
            new LearnerProfile { Id = 1, Name = "Bob", AvatarEmoji = "👦", Color = "#4ecdc4", IsDefault = false },
            new LearnerProfile { Id = 2, Name = "Alice", AvatarEmoji = "👧", Color = "#ff6b6b", IsDefault = true }
        );
        await db.SaveChangesAsync();

        var mockLogger = new Mock<ILogger<LearnerService>>();
        var factory = CreateFactory(options);
        var service = new LearnerService(factory, mockLogger.Object);

        var result = await service.GetDefaultAsync();

        Assert.NotNull(result);
        Assert.Equal("Alice", result.Name);
    }

    [Fact]
    public async Task GetDefaultAsync_NoDefaultSet_ReturnsFirst()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        await using var db = new FamilyDbContext(options);
        db.LearnerProfiles.AddRange(
            new LearnerProfile { Id = 1, Name = "Bob", AvatarEmoji = "👦", Color = "#4ecdc4", IsDefault = false },
            new LearnerProfile { Id = 2, Name = "Alice", AvatarEmoji = "👧", Color = "#ff6b6b", IsDefault = false }
        );
        await db.SaveChangesAsync();

        var mockLogger = new Mock<ILogger<LearnerService>>();
        var factory = CreateFactory(options);
        var service = new LearnerService(factory, mockLogger.Object);

        var result = await service.GetDefaultAsync();

        Assert.NotNull(result);
        Assert.Equal("Bob", result.Name);
    }

    [Fact]
    public async Task GetByIdAsync_ExistingId_ReturnsLearner()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        await using var db = new FamilyDbContext(options);
        db.LearnerProfiles.Add(new LearnerProfile { Id = 1, Name = "Bob", AvatarEmoji = "👦", Color = "#4ecdc4", IsDefault = true });
        await db.SaveChangesAsync();

        var mockLogger = new Mock<ILogger<LearnerService>>();
        var factory = CreateFactory(options);
        var service = new LearnerService(factory, mockLogger.Object);

        var result = await service.GetByIdAsync(1);

        Assert.NotNull(result);
        Assert.Equal("Bob", result.Name);
    }

    [Fact]
    public async Task GetByIdAsync_NonExistingId_ReturnsNull()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        await using var db = new FamilyDbContext(options);
        var mockLogger = new Mock<ILogger<LearnerService>>();
        var factory = CreateFactory(options);
        var service = new LearnerService(factory, mockLogger.Object);

        var result = await service.GetByIdAsync(999);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateAsync_FirstLearner_SetsAsDefault()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        await using var db = new FamilyDbContext(options);
        var mockLogger = new Mock<ILogger<LearnerService>>();
        var factory = CreateFactory(options);
        var service = new LearnerService(factory, mockLogger.Object);

        var result = await service.CreateAsync("Charlie", "👨", "#95e1d3");

        Assert.NotNull(result);
        Assert.Equal("Charlie", result.Name);
        Assert.Equal("👨", result.AvatarEmoji);
        Assert.Equal("#95e1d3", result.Color);
        Assert.True(result.IsDefault);
        Assert.Equal(1, result.Id);
    }

    [Fact]
    public async Task CreateAsync_SubsequentLearner_NotDefault()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        await using var db = new FamilyDbContext(options);
        db.LearnerProfiles.Add(new LearnerProfile { Id = 1, Name = "Alice", AvatarEmoji = "👧", Color = "#ff6b6b", IsDefault = true });
        await db.SaveChangesAsync();

        var mockLogger = new Mock<ILogger<LearnerService>>();
        var factory = CreateFactory(options);
        var service = new LearnerService(factory, mockLogger.Object);

        var result = await service.CreateAsync("Bob", "👦", "#4ecdc4");

        Assert.NotNull(result);
        Assert.Equal("Bob", result.Name);
        Assert.False(result.IsDefault);
        Assert.Equal(2, result.Id);
    }

    [Fact]
    public async Task CreateAsync_TrimsName()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        await using var db = new FamilyDbContext(options);
        var mockLogger = new Mock<ILogger<LearnerService>>();
        var factory = CreateFactory(options);
        var service = new LearnerService(factory, mockLogger.Object);

        var result = await service.CreateAsync("  David  ", "👨", "#f38181");

        Assert.Equal("David", result.Name);
    }

    [Fact]
    public async Task SetDefaultAsync_ExistingId_SetsDefault()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        var factory = CreateFactory(options);
        
        await using (var db = factory.CreateDbContext())
        {
            db.LearnerProfiles.AddRange(
                new LearnerProfile { Id = 1, Name = "Alice", AvatarEmoji = "👧", Color = "#ff6b6b", IsDefault = true },
                new LearnerProfile { Id = 2, Name = "Bob", AvatarEmoji = "👦", Color = "#4ecdc4", IsDefault = false }
            );
            await db.SaveChangesAsync();
        }

        var mockLogger = new Mock<ILogger<LearnerService>>();
        var service = new LearnerService(factory, mockLogger.Object);

        var result = await service.SetDefaultAsync(2);

        Assert.True(result);
        
        await using (var db = factory.CreateDbContext())
        {
            var alice = await db.LearnerProfiles.FindAsync(1);
            var bob = await db.LearnerProfiles.FindAsync(2);
            Assert.False(alice?.IsDefault);
            Assert.True(bob?.IsDefault);
        }
    }

    [Fact]
    public async Task DeleteAsync_ExistingId_DeletesLearner()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        await using var db = new FamilyDbContext(options);
        db.LearnerProfiles.Add(new LearnerProfile { Id = 1, Name = "Alice", AvatarEmoji = "👧", Color = "#ff6b6b", IsDefault = true });
        await db.SaveChangesAsync();

        var mockLogger = new Mock<ILogger<LearnerService>>();
        var factory = CreateFactory(options);
        var service = new LearnerService(factory, mockLogger.Object);

        var result = await service.DeleteAsync(1);

        Assert.True(result);
        Assert.Empty(db.LearnerProfiles);
    }

    [Fact]
    public async Task DeleteAsync_NonExistingId_ReturnsFalse()
    {
        var options = CreateInMemoryOptions(Guid.NewGuid().ToString());
        await using var db = new FamilyDbContext(options);
        var mockLogger = new Mock<ILogger<LearnerService>>();
        var factory = CreateFactory(options);
        var service = new LearnerService(factory, mockLogger.Object);

        var result = await service.DeleteAsync(999);

        Assert.False(result);
    }
}