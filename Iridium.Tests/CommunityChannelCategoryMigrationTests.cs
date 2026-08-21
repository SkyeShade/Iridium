using Iridium.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class CommunityChannelCategoryMigrationTests
{
    [Fact]
    public async Task RequiredCategorySchemaUpgradesToNullableWithoutMovingExistingChannels()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE Communities (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE CommunityCategories (CommunityId TEXT NOT NULL, Id TEXT NOT NULL, Name TEXT NOT NULL,
                    Position INTEGER NOT NULL, ParentCategoryId TEXT NULL, PRIMARY KEY (CommunityId, Id));
                CREATE TABLE CommunityChannels (CommunityId TEXT NOT NULL, Id TEXT NOT NULL, CategoryId TEXT NOT NULL,
                    Name TEXT NOT NULL, Position INTEGER NOT NULL, CreatedAt TEXT NOT NULL, PRIMARY KEY (CommunityId, Id));
                INSERT INTO Communities VALUES ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA');
                INSERT INTO CommunityCategories VALUES ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                    'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB', 'GENERAL', 0, NULL);
                INSERT INTO CommunityChannels VALUES ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA',
                    'CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC', 'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',
                    'chat', 0, '2026-01-01 00:00:00+00:00');
                """;
            await setup.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);

        await DatabaseCompatibility.EnsureCommunityStructureTablesAsync(db);

        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT [notnull] FROM pragma_table_info('CommunityChannels') WHERE name = 'CategoryId';";
        Assert.Equal(0L, (long)(await inspect.ExecuteScalarAsync())!);
        var existing = await db.CommunityChannels.SingleAsync();
        Assert.Equal(Guid.Parse("BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB"), existing.CategoryId);
        existing.CategoryId = null;
        await db.SaveChangesAsync();
        Assert.Null((await db.CommunityChannels.SingleAsync()).CategoryId);
    }

    [Fact]
    public async Task LegacyUncategorizedChannelsRemainAtRootIdempotently()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE Communities (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE CommunityCategories (
                    CommunityId TEXT NOT NULL, Id TEXT NOT NULL, Name TEXT NOT NULL,
                    Position INTEGER NOT NULL, ParentCategoryId TEXT NULL,
                    PRIMARY KEY (CommunityId, Id));
                CREATE TABLE CommunityChannels (
                    CommunityId TEXT NOT NULL, Id TEXT NOT NULL, CategoryId TEXT NULL,
                    Name TEXT NOT NULL, Position INTEGER NOT NULL, CreatedAt INTEGER NOT NULL,
                    PRIMARY KEY (CommunityId, Id));
                INSERT INTO Communities (Id) VALUES ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA');
                INSERT INTO CommunityChannels (CommunityId, Id, CategoryId, Name, Position, CreatedAt) VALUES
                    ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', '11111111-1111-1111-1111-111111111111', NULL, 'alpha', 8, '2026-01-01 00:00:00+00:00'),
                    ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', '22222222-2222-2222-2222-222222222222', NULL, 'beta', 12, '2026-01-01 00:00:01+00:00');
                """;
            await setup.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);

        await DatabaseCompatibility.EnsureUnifiedCommunitySidebarOrderingAsync(db);
        await DatabaseCompatibility.EnsureUnifiedCommunitySidebarOrderingAsync(db);

        Assert.Empty(await db.CommunityCategories.ToListAsync());
        var migratedChannel = await db.CommunityChannels.OrderBy(value => value.Position).FirstAsync();
        migratedChannel.Name = "alpha-updated";
        await db.SaveChangesAsync();

        await using var verify = connection.CreateCommand();
        verify.CommandText = """
            SELECT ch.CategoryId, ch.Name, ch.Position
            FROM CommunityChannels ch
            ORDER BY ch.Position;
            """;
        await using var reader = await verify.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal("alpha-updated", reader.GetString(1));
        Assert.Equal(0, reader.GetInt32(2));
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.IsDBNull(0));
        Assert.Equal("beta", reader.GetString(1));
        Assert.Equal(1, reader.GetInt32(2));
        Assert.False(await reader.ReadAsync());
    }
}
