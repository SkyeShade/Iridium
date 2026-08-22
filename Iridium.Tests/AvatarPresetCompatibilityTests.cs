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
        inspect.CommandText = "SELECT Username, ActiveAvatarPresetId, AvatarRevision FROM Accounts;";
        await using var reader = await inspect.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("legacy", reader.GetString(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(0, reader.GetInt64(2));
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
}
