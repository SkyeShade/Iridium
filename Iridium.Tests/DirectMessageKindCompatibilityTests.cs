using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class DirectMessageKindCompatibilityTests
{
    [Fact]
    public async Task LegacyDirectMessagesGainUserKindWithoutDataLoss()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = """
                CREATE TABLE DirectMessages (
                    Id TEXT NOT NULL PRIMARY KEY,
                    ConversationId TEXT NOT NULL,
                    AuthorAccountId TEXT NOT NULL,
                    ClientMessageId TEXT NULL,
                    Content TEXT NOT NULL,
                    CreatedAt INTEGER NOT NULL,
                    EditedAt INTEGER NULL,
                    IsDeleted INTEGER NOT NULL DEFAULT 0,
                    DeletedAt INTEGER NULL,
                    ReplyToMessageId TEXT NULL
                );
                """;
            await create.ExecuteNonQueryAsync();
        }

        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);
        await DatabaseCompatibility.EnsureDirectMessageTablesAsync(db);

        var messageId = Guid.NewGuid();
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = """
                INSERT INTO DirectMessages
                    (Id, ConversationId, AuthorAccountId, Content, CreatedAt, IsDeleted)
                VALUES ($id, $conversation, $author, 'legacy message', 1, 0);
                """;
            insert.Parameters.AddWithValue("$id", messageId);
            insert.Parameters.AddWithValue("$conversation", Guid.NewGuid());
            insert.Parameters.AddWithValue("$author", Guid.NewGuid());
            await insert.ExecuteNonQueryAsync();
        }

        var legacy = await db.DirectMessages.AsNoTracking().SingleAsync(value => value.Id == messageId);
        Assert.Equal(MessageKind.User, legacy.Kind);
        Assert.Null(legacy.RelatedCallId);
        Assert.Equal("legacy message", legacy.Content);
    }

    [Fact]
    public void NewDirectMessagesDefaultToUserKind()
    {
        var message = new DirectMessage
        {
            Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), AuthorAccountId = Guid.NewGuid(),
            Conversation = null!, AuthorAccount = null!, Content = "normal", CreatedAt = DateTimeOffset.UtcNow
        };

        Assert.Equal(MessageKind.User, message.Kind);
    }
}
