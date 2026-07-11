using System;
using System.Threading.Tasks;
using KnotGarden.Api.Services.Auth;
using KnotGarden.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace KnotGarden.Tests.Auth;

public class UserServiceTests : IAsyncLifetime
{
    // A fresh in-memory SQLite DB per test (keep-alive connection). EnsureCreated builds the Users
    // table from the EF model, so no startup schema step is needed here.
    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        await using var db = new AppDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private AppDbContext Db() => new(_options);
    private UserService Service(AppDbContext db) => new(db, TimeProvider.System);

    [Fact]
    public async Task Create_normalizes_username_hashes_password_and_validates_case_insensitively()
    {
        await using var db = Db();
        var service = Service(db);
        Assert.Equal(0, await service.CountAsync());

        var user = await service.CreateAsync("Admin", "password123", "admin");

        Assert.Equal("admin", user.Username);                 // normalized to lower-case
        Assert.NotEqual("password123", user.PasswordHash);    // stored hashed, never plaintext
        Assert.Equal(1, await service.CountAsync());
        Assert.NotNull(await service.ValidateCredentialsAsync("ADMIN", "password123"));  // case-insensitive
        Assert.Null(await service.ValidateCredentialsAsync("admin", "wrong-password"));
        Assert.Null(await service.ValidateCredentialsAsync("nobody", "password123"));
    }

    [Fact]
    public async Task Duplicate_username_is_rejected()
    {
        await using var db = Db();
        var service = Service(db);
        await service.CreateAsync("bob", "password123");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CreateAsync("BOB", "another123"));
    }

    [Fact]
    public async Task Short_password_is_rejected()
    {
        await using var db = Db();
        await Assert.ThrowsAsync<ArgumentException>(() => Service(db).CreateAsync("bob", "short"));
    }

    [Fact]
    public async Task Cannot_delete_the_last_user_but_can_delete_others()
    {
        await using var db = Db();
        var service = Service(db);
        var first = await service.CreateAsync("admin", "password123");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(first.Id));

        var second = await service.CreateAsync("bob", "password123");
        Assert.True(await service.DeleteAsync(second.Id));
        Assert.Equal(1, await service.CountAsync());
    }

    [Fact]
    public async Task Change_password_updates_the_verifiable_hash()
    {
        await using var db = Db();
        var service = Service(db);
        var user = await service.CreateAsync("admin", "password123");

        Assert.True(await service.ChangePasswordAsync(user.Id, "newpassword456"));
        Assert.Null(await service.ValidateCredentialsAsync("admin", "password123"));
        Assert.NotNull(await service.ValidateCredentialsAsync("admin", "newpassword456"));
    }
}
