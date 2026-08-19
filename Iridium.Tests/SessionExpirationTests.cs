using Iridium.Server.Configuration;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Iridium.Tests;

public sealed class SessionExpirationTests
{
    [Fact]
    public async Task IdleAndAbsoluteExpirationInvalidateSessions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var service = Service(idleDays: 60, absoluteDays: 365, updateMinutes: 15);
        var account = Account();
        db.Accounts.Add(account);
        await db.SaveChangesAsync();

        var (idleToken, idleSession) = await service.CreateAsync(account, db);
        idleSession.LastUsedAt = DateTimeOffset.UtcNow.AddDays(-61);
        await db.SaveChangesAsync();
        Assert.Null(await service.GetByTokenAsync(idleToken, db));

        var (absoluteToken, absoluteSession) = await service.CreateAsync(account, db);
        absoluteSession.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        Assert.Null(await service.GetByTokenAsync(absoluteToken, db));
    }

    [Fact]
    public async Task SessionActivityUpdatesAreThrottled()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = Context(connection);
        await db.Database.EnsureCreatedAsync();
        var service = Service(idleDays: 60, absoluteDays: 365, updateMinutes: 15);
        var account = Account();
        db.Accounts.Add(account);
        await db.SaveChangesAsync();
        var (token, session) = await service.CreateAsync(account, db);
        session.LastUsedAt = DateTimeOffset.UtcNow.AddMinutes(-20);
        await db.SaveChangesAsync();

        Assert.NotNull(await service.GetByTokenAsync(token, db));
        var firstUpdate = session.LastUsedAt;
        Assert.True(firstUpdate > DateTimeOffset.UtcNow.AddMinutes(-1));

        Assert.NotNull(await service.GetByTokenAsync(token, db));
        Assert.Equal(firstUpdate, session.LastUsedAt);
    }

    private static IridiumDbContext Context(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options);

    private static SessionService Service(int idleDays, int absoluteDays, int updateMinutes) => new(
        Options.Create(new NodeOptions
        {
            SessionIdleDays = idleDays,
            SessionAbsoluteDays = absoluteDays,
            SessionActivityUpdateMinutes = updateMinutes
        }));

    private static NodeAccount Account() => new()
    {
        Id = Guid.NewGuid(),
        Username = $"account-{Guid.NewGuid():N}"[..32],
        DisplayName = "Session test",
        PasswordHash = "unused",
        CreatedAt = DateTimeOffset.UtcNow
    };
}
