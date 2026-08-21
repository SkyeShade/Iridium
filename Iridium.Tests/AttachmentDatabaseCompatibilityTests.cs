using Iridium.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class AttachmentDatabaseCompatibilityTests
{
    [Fact]
    public async Task EmptyDatabaseGetsCurrentAttachmentSchemaAndCanRunTwice()
    {
        await using var fixture = await CompatibilityFixture.CreateAsync();

        await DatabaseCompatibility.EnsureAttachmentsTableAsync(fixture.Db);
        await DatabaseCompatibility.EnsureAttachmentsTableAsync(fixture.Db);

        await AssertCurrentSchemaAsync(fixture.Connection);
    }

    [Fact]
    public async Task LegacyAttachmentTableIsUpgradedWithoutChangingExistingRows()
    {
        await using var fixture = await CompatibilityFixture.CreateAsync();
        const string attachmentId = "11111111-1111-1111-1111-111111111111";
        const string objectKey = "0123456789abcdef0123456789abcdef";
        await ExecuteAsync(fixture.Connection, """
            CREATE TABLE Attachments (
                Id TEXT NOT NULL CONSTRAINT PK_Attachments PRIMARY KEY,
                UploaderAccountId TEXT NOT NULL,
                ChannelMessageId TEXT NULL,
                DirectMessageId TEXT NULL,
                OriginalFileName TEXT NOT NULL,
                StoredObjectKey TEXT NOT NULL,
                ContentType TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                CreatedAt INTEGER NOT NULL
            );
            INSERT INTO Attachments
                (Id, UploaderAccountId, OriginalFileName, StoredObjectKey, ContentType, SizeBytes, CreatedAt)
            VALUES
                ('11111111-1111-1111-1111-111111111111',
                 '22222222-2222-2222-2222-222222222222',
                 'legacy.png', '0123456789abcdef0123456789abcdef', 'image/png', 1234, 42);
            """);

        await DatabaseCompatibility.EnsureAttachmentsTableAsync(fixture.Db);
        await DatabaseCompatibility.EnsureAttachmentsTableAsync(fixture.Db);

        await AssertCurrentSchemaAsync(fixture.Connection);
        await using var command = fixture.Connection.CreateCommand();
        command.CommandText = """
            SELECT Id, StoredObjectKey, ContentType, SizeBytes, Width, Height, IsSpoiler,
                   AverageColor, PreviewObjectKey, PreviewContentType, PreviewSizeBytes
            FROM Attachments WHERE Id = $id;
            """;
        command.Parameters.AddWithValue("$id", attachmentId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(attachmentId, reader.GetString(0));
        Assert.Equal(objectKey, reader.GetString(1));
        Assert.Equal("image/png", reader.GetString(2));
        Assert.Equal(1234, reader.GetInt64(3));
        Assert.True(reader.IsDBNull(4));
        Assert.True(reader.IsDBNull(5));
        Assert.Equal(0, reader.GetInt64(6));
        Assert.True(reader.IsDBNull(7));
        Assert.True(reader.IsDBNull(8));
        Assert.True(reader.IsDBNull(9));
        Assert.True(reader.IsDBNull(10));
    }

    [Fact]
    public async Task EfCreatedAndCompatibilitySchemasAgreeAndNullablePreviewKeysRemainUnique()
    {
        await using var fixture = await CompatibilityFixture.CreateAsync();
        await fixture.Db.Database.EnsureCreatedAsync();

        await DatabaseCompatibility.EnsureAttachmentsTableAsync(fixture.Db);
        await DatabaseCompatibility.EnsureAttachmentsTableAsync(fixture.Db);

        await AssertCurrentSchemaAsync(fixture.Connection);
        await ExecuteAsync(fixture.Connection, "PRAGMA foreign_keys = OFF;");
        await ExecuteAsync(fixture.Connection, """
            INSERT INTO Attachments
                (Id, UploaderAccountId, OriginalFileName, StoredObjectKey, ContentType, SizeBytes, CreatedAt, IsSpoiler)
            VALUES
                ('11111111-1111-1111-1111-111111111111', 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
                 'one.bin', '00000000000000000000000000000001', 'application/octet-stream', 1, 1, 0),
                ('22222222-2222-2222-2222-222222222222', 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb',
                 'two.bin', '00000000000000000000000000000002', 'application/octet-stream', 1, 1, 0);
            """);

        await ExecuteAsync(fixture.Connection,
            "UPDATE Attachments SET PreviewObjectKey = 'ffffffffffffffffffffffffffffffff' WHERE Id = '11111111-1111-1111-1111-111111111111';");
        var duplicate = await Assert.ThrowsAsync<SqliteException>(() => ExecuteAsync(fixture.Connection,
            "UPDATE Attachments SET PreviewObjectKey = 'ffffffffffffffffffffffffffffffff' WHERE Id = '22222222-2222-2222-2222-222222222222';"));
        Assert.Equal(19, duplicate.SqliteErrorCode);
    }

    private static async Task AssertCurrentSchemaAsync(SqliteConnection connection)
    {
        var columns = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info('Attachments');";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns[reader.GetString(1)] = reader.GetInt64(3) != 0;
        }

        Assert.False(columns["Width"]);
        Assert.False(columns["Height"]);
        Assert.True(columns["IsSpoiler"]);
        Assert.False(columns["AverageColor"]);
        Assert.False(columns["PreviewObjectKey"]);
        Assert.False(columns["PreviewContentType"]);
        Assert.False(columns["PreviewSizeBytes"]);

        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA index_list('Attachments');";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) indexes.Add(reader.GetString(1));
        }
        Assert.Contains("IX_Attachments_StoredObjectKey", indexes);
        Assert.Contains("IX_Attachments_PreviewObjectKey", indexes);
        Assert.Contains("IX_Attachments_ChannelMessageId", indexes);
        Assert.Contains("IX_Attachments_DirectMessageId", indexes);
        Assert.Contains("IX_Attachments_UploaderAccountId", indexes);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private sealed class CompatibilityFixture(SqliteConnection connection, IridiumDbContext db) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;
        public IridiumDbContext Db { get; } = db;

        public static async Task<CompatibilityFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
            return new(connection, new IridiumDbContext(options));
        }

        public async ValueTask DisposeAsync()
        {
            await Db.DisposeAsync();
            await Connection.DisposeAsync();
        }
    }
}
