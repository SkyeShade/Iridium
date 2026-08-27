using Iridium.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class AvatarPresetCompatibilityTests
{
    [Fact]
    public async Task LegacyAccountRowsArePreservedAndReceiveEmptyPresetState()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE Accounts (
                    Id TEXT NOT NULL PRIMARY KEY,
                    Username TEXT NOT NULL,
                    DisplayName TEXT NOT NULL,
                    PasswordHash TEXT NOT NULL,
                    CreatedAt INTEGER NOT NULL
                );
                INSERT INTO Accounts VALUES
                    ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', 'legacy', 'Legacy User', 'hash', 1);
                """;
            await setup.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);

        await DatabaseCompatibility.EnsureAvatarPresetSchemaAsync(db);
        await DatabaseCompatibility.EnsureAvatarPresetSchemaAsync(db);

        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT Username, ActiveAvatarPresetId, BaseAvatarPresetId, AvatarRevision FROM Accounts;";
        await using var reader = await inspect.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("legacy", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.True(reader.IsDBNull(2));
        Assert.Equal(0, reader.GetInt64(3));
        await reader.DisposeAsync();
        inspect.CommandText = "SELECT COUNT(*) FROM AccountAvatarPresets;";
        Assert.Equal(0L, await inspect.ExecuteScalarAsync());
    }

    [Fact]
    public async Task LegacyAccountRowsReceiveEmptyBannerPresetStateWithoutDataLoss()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE Accounts (Id TEXT NOT NULL PRIMARY KEY, Username TEXT NOT NULL,
                    DisplayName TEXT NOT NULL, PasswordHash TEXT NOT NULL, CreatedAt INTEGER NOT NULL);
                INSERT INTO Accounts VALUES
                    ('BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB', 'banner-legacy', 'Banner Legacy', 'hash', 1);
                """;
            await setup.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);
        await DatabaseCompatibility.EnsureBannerPresetSchemaAsync(db);
        await DatabaseCompatibility.EnsureBannerPresetSchemaAsync(db);
        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT Username, ActiveBannerPresetId, BannerRevision FROM Accounts;";
        await using var reader = await inspect.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("banner-legacy", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(0, reader.GetInt64(2));
        await reader.DisposeAsync();
        inspect.CommandText = "SELECT COUNT(*) FROM AccountBannerPresets;";
        Assert.Equal(0L, await inspect.ExecuteScalarAsync());
    }

    [Fact]
    public async Task GlobalAssignedPresetsBecomeIndependentCommunityPresetsAndUnassignedRowsAreRemoved()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE Accounts (Id TEXT NOT NULL PRIMARY KEY, Username TEXT NOT NULL,
                    DisplayName TEXT NOT NULL, PasswordHash TEXT NOT NULL, CreatedAt INTEGER NOT NULL);
                CREATE TABLE Communities (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE CommunityMembers (
                    CommunityId TEXT NOT NULL, AccountId TEXT NOT NULL, ProfilePresetId TEXT NULL,
                    PRIMARY KEY (CommunityId, AccountId));
                CREATE TABLE UserProfilePresets (
                    Id TEXT NOT NULL PRIMARY KEY, AccountId TEXT NOT NULL, DisplayName TEXT NOT NULL,
                    AvatarPresetId TEXT NULL, Position INTEGER NOT NULL, CreatedAt INTEGER NOT NULL, UpdatedAt INTEGER NOT NULL);
                CREATE UNIQUE INDEX IX_UserProfilePresets_AccountId_Position
                    ON UserProfilePresets (AccountId, Position);
                INSERT INTO Accounts VALUES
                    ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', 'legacy', 'Legacy', 'hash', 1);
                INSERT INTO Communities VALUES ('BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB');
                INSERT INTO Communities VALUES ('CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC');
                INSERT INTO UserProfilePresets VALUES
                    ('DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD', 'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', 'Shared', NULL, 0, 1, 1),
                    ('EEEEEEEE-EEEE-EEEE-EEEE-EEEEEEEEEEEE', 'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', 'Unused', NULL, 1, 1, 1);
                INSERT INTO CommunityMembers VALUES
                    ('BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB', 'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', 'DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD'),
                    ('CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC', 'AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA', 'DDDDDDDD-DDDD-DDDD-DDDD-DDDDDDDDDDDD');
                """;
            await setup.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);

        await DatabaseCompatibility.EnsureAvatarPresetSchemaAsync(db);
        await DatabaseCompatibility.EnsureAvatarPresetSchemaAsync(db);

        await using var inspect = connection.CreateCommand();
        inspect.CommandText = """
            SELECT COUNT(*) FROM CommunityMembers m
            JOIN UserProfilePresets p ON p.Id = m.ProfilePresetId
            WHERE p.AccountId = m.AccountId AND p.CommunityId = m.CommunityId AND p.DisplayName = 'Shared';
            """;
        Assert.Equal(2L, await inspect.ExecuteScalarAsync());
        inspect.CommandText = "SELECT COUNT(*) FROM UserProfilePresets WHERE DisplayName = 'Shared';";
        Assert.Equal(2L, await inspect.ExecuteScalarAsync());
        inspect.CommandText = "SELECT COUNT(*) FROM UserProfilePresets WHERE DisplayName = 'Unused';";
        Assert.Equal(0L, await inspect.ExecuteScalarAsync());
        inspect.CommandText = "SELECT COUNT(DISTINCT ProfilePresetId) FROM CommunityMembers;";
        Assert.Equal(2L, await inspect.ExecuteScalarAsync());
        inspect.CommandText = "DELETE FROM Communities WHERE Id = 'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB';";
        await inspect.ExecuteNonQueryAsync();
        inspect.CommandText = "SELECT COUNT(*) FROM UserProfilePresets WHERE CommunityId = 'BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB';";
        Assert.Equal(0L, await inspect.ExecuteScalarAsync());
    }
}
