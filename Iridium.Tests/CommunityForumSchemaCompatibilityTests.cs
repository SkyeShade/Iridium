using Iridium.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class CommunityForumSchemaCompatibilityTests
{
    [Fact]
    public async Task PreForumTagAndEmbedDatabaseUpgradesBeforeEntityMigrationAndIsIdempotent()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                PRAGMA foreign_keys = ON;
                CREATE TABLE Communities (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE CommunityCategories (
                    CommunityId TEXT NOT NULL,
                    Id TEXT NOT NULL,
                    Name TEXT NOT NULL,
                    Position INTEGER NOT NULL,
                    ParentCategoryId TEXT NULL,
                    PRIMARY KEY (CommunityId, Id));
                CREATE TABLE CommunityChannels (
                    CommunityId TEXT NOT NULL,
                    Id TEXT NOT NULL,
                    CategoryId TEXT NULL,
                    ParentForumChannelId TEXT NULL,
                    Name TEXT NOT NULL,
                    Kind INTEGER NOT NULL DEFAULT 0,
                    PermissionsSyncedToCategory INTEGER NOT NULL DEFAULT 0,
                    Position INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL,
                    PRIMARY KEY (CommunityId, Id));
                CREATE TABLE CommunityForumPosts (
                    Id TEXT NOT NULL PRIMARY KEY,
                    CommunityId TEXT NOT NULL,
                    ForumChannelId TEXT NOT NULL,
                    DiscussionChannelId TEXT NOT NULL,
                    RootMessageId TEXT NOT NULL,
                    AuthorAccountId TEXT NOT NULL,
                    Title TEXT NOT NULL,
                    CreatedAt INTEGER NOT NULL,
                    UpdatedAt INTEGER NOT NULL,
                    LastActivityAt INTEGER NOT NULL,
                    ReplyCount INTEGER NOT NULL DEFAULT 0,
                    IsLocked INTEGER NOT NULL DEFAULT 0,
                    IsPinned INTEGER NOT NULL DEFAULT 0);
                INSERT INTO Communities (Id) VALUES ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA');
                INSERT INTO CommunityChannels
                    (CommunityId, Id, CategoryId, ParentForumChannelId, Name, Kind,
                     PermissionsSyncedToCategory, Position, CreatedAt)
                VALUES
                    ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                     'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB', NULL, NULL,
                     'existing-forum', 2, 0, 9, '2026-01-01 00:00:00+00:00');
                INSERT INTO CommunityForumPosts
                    (Id, CommunityId, ForumChannelId, DiscussionChannelId, RootMessageId, AuthorAccountId,
                     Title, CreatedAt, UpdatedAt, LastActivityAt)
                VALUES
                    ('CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',
                     'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                     'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',
                     'DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD',
                     'EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE',
                     'FFFFFFFF-FFFF-FFFF-FFFF-FFFFFFFFFFFF',
                     'existing-post', 1, 1, 1);
                """;
            await setup.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);

        for (var pass = 0; pass < 2; pass++)
        {
            await DatabaseCompatibility.EnsureEarlyCommunitySchemaAsync(db);
            await DatabaseCompatibility.EnsureUnifiedCommunitySidebarOrderingAsync(db);
        }

        var channel = await db.CommunityChannels.SingleAsync();
        Assert.False(channel.RequireTag);
        Assert.False(channel.AllowDocumentEmbeds);
        Assert.Null(channel.EmbedProvider);
        Assert.Null(channel.EmbedUrl);
        Assert.Equal(0, channel.Position);
        var post = await db.CommunityForumPosts.SingleAsync();
        Assert.Null(post.EmbedProvider);
        Assert.Null(post.EmbedUrl);

        var channelColumns = await ReadNamesAsync(connection, "PRAGMA table_info('CommunityChannels');", 1);
        Assert.Contains("RequireTag", channelColumns);
        Assert.Contains("EmbedProvider", channelColumns);
        Assert.Contains("EmbedUrl", channelColumns);
        Assert.Contains("AllowDocumentEmbeds", channelColumns);
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT [notnull] FROM pragma_table_info('CommunityChannels') WHERE name = 'RequireTag';"));
        Assert.Equal("0", await ScalarTextAsync(connection,
            "SELECT dflt_value FROM pragma_table_info('CommunityChannels') WHERE name = 'RequireTag';"));

        Assert.Equal(1L, await TableExistsAsync(connection, "CommunityForumTags"));
        Assert.Equal(1L, await TableExistsAsync(connection, "CommunityForumPostTags"));
        var postColumns = await ReadNamesAsync(connection, "PRAGMA table_info('CommunityForumPosts');", 1);
        AssertContains(postColumns, "EmbedProvider", "EmbedUrl");

        var tagColumns = await ReadNamesAsync(connection, "PRAGMA table_info('CommunityForumTags');", 1);
        AssertContains(tagColumns, "Id", "CommunityId", "ChannelId", "Name", "EmojiKind", "StandardEmoji",
            "CustomEmojiId", "Moderated", "SortOrder", "CreatedAt");
        var assignmentColumns = await ReadNamesAsync(connection, "PRAGMA table_info('CommunityForumPostTags');", 1);
        AssertContains(assignmentColumns, "PostId", "TagId");

        var tagIndexes = await ReadNamesAsync(connection, "PRAGMA index_list('CommunityForumTags');", 1);
        AssertContains(tagIndexes, "IX_CommunityForumTags_ChannelId_Name", "IX_CommunityForumTags_ChannelId",
            "IX_CommunityForumTags_ChannelId_SortOrder");
        var assignmentIndexes = await ReadNamesAsync(connection, "PRAGMA index_list('CommunityForumPostTags');", 1);
        AssertContains(assignmentIndexes, "IX_CommunityForumPostTags_PostId", "IX_CommunityForumPostTags_TagId");

        Assert.Equal(2, (await ReadNamesAsync(connection,
            "PRAGMA foreign_key_list('CommunityForumPostTags');", 2)).Count);
    }

    [Fact]
    public async Task FreshDatabaseIncludesForumTagAndEmbedSchemaWithoutCompatibilityAlter()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);

        await db.Database.EnsureCreatedAsync();

        var columns = await ReadNamesAsync(connection, "PRAGMA table_info('CommunityChannels');", 1);
        Assert.Contains("RequireTag", columns);
        Assert.Contains("EmbedProvider", columns);
        Assert.Contains("EmbedUrl", columns);
        Assert.Contains("AllowDocumentEmbeds", columns);
        var postColumns = await ReadNamesAsync(connection, "PRAGMA table_info('CommunityForumPosts');", 1);
        AssertContains(postColumns, "EmbedProvider", "EmbedUrl");
        Assert.Equal(1L, await TableExistsAsync(connection, "CommunityForumTags"));
        Assert.Equal(1L, await TableExistsAsync(connection, "CommunityForumPostTags"));
    }

    private static async Task<HashSet<string>> ReadNamesAsync(SqliteConnection connection, string sql, int ordinal)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) names.Add(reader.GetString(ordinal));
        return names;
    }

    private static void AssertContains(IReadOnlySet<string> actual, params string[] expected)
    {
        foreach (var value in expected) Assert.Contains(value, actual);
    }

    private static async Task<long> TableExistsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", table);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ScalarTextAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
