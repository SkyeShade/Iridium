using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class CommunityUnreadStateTests
{
    [Fact]
    public async Task MessageHistoryQueriesUseCompositePagingIndexes()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);
        await db.Database.EnsureCreatedAsync();

        Assert.Contains("IX_ChannelMessages_CommunityId_ChannelId_CreatedAt_Id",
            await QueryPlanAsync(connection,
                "EXPLAIN QUERY PLAN SELECT * FROM ChannelMessages WHERE CommunityId = $community AND ChannelId = $channel ORDER BY CreatedAt DESC, Id DESC LIMIT 50"));
        Assert.Contains("IX_DirectMessages_ConversationId_CreatedAt_Id",
            await QueryPlanAsync(connection,
                "EXPLAIN QUERY PLAN SELECT * FROM DirectMessages WHERE ConversationId = $conversation ORDER BY CreatedAt DESC, Id DESC LIMIT 50"));
    }

    [Fact]
    public async Task ReadStateIsPerAccountAndUsesSortableUtcTicks()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var owner = Account("owner");
        var reader = Account("reader");
        var community = new Community { Id = Guid.NewGuid(), Name = "Test", OwnerAccountId = owner.Id,
            OwnerAccount = owner, CreatedAt = DateTimeOffset.UtcNow };
        var category = new CommunityCategory { Id = Guid.NewGuid(), CommunityId = community.Id, Community = community,
            Name = "TEXT CHANNELS", Position = 0 };
        var channel = new CommunityChannel { Id = Guid.NewGuid(), CommunityId = community.Id, Community = community,
            CategoryId = category.Id, Category = category, Name = "general", Position = 0, CreatedAt = DateTimeOffset.UtcNow };
        var createdAt = DateTimeOffset.UtcNow;
        var message = new ChannelMessage { Id = Guid.NewGuid(), CommunityId = community.Id, ChannelId = channel.Id,
            Channel = channel, AuthorAccountId = owner.Id, AuthorAccount = owner, Content = "hello", CreatedAt = createdAt };
        db.AddRange(owner, reader, community, category, channel, message);
        await db.SaveChangesAsync();

        Assert.True(await HasUnreadAsync(db, reader.Id, community.Id));
        db.CommunityChannelReadStates.Add(new CommunityChannelReadState { CommunityId = community.Id,
            ChannelId = channel.Id, AccountId = reader.Id, LastReadAt = createdAt, Channel = channel, Account = reader });
        await db.SaveChangesAsync();
        Assert.False(await HasUnreadAsync(db, reader.Id, community.Id));
        Assert.False(await HasUnreadAsync(db, owner.Id, community.Id));
    }

    private static Task<bool> HasUnreadAsync(IridiumDbContext db, Guid accountId, Guid communityId) =>
        db.ChannelMessages.AnyAsync(message => message.CommunityId == communityId && message.AuthorAccountId != accountId &&
            !db.CommunityChannelReadStates.Any(state => state.CommunityId == message.CommunityId &&
                state.ChannelId == message.ChannelId && state.AccountId == accountId && state.LastReadAt >= message.CreatedAt));

    private static NodeAccount Account(string username) => new()
    {
        Id = Guid.NewGuid(), Username = username, DisplayName = username, PasswordHash = "test", CreatedAt = DateTimeOffset.UtcNow
    };

    private static async Task<string> QueryPlanAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$community", Guid.Empty);
        command.Parameters.AddWithValue("$channel", Guid.Empty);
        command.Parameters.AddWithValue("$conversation", Guid.Empty);
        await using var reader = await command.ExecuteReaderAsync();
        var details = new List<string>();
        while (await reader.ReadAsync()) details.Add(reader.GetString(3));
        return string.Join(Environment.NewLine, details);
    }
}
