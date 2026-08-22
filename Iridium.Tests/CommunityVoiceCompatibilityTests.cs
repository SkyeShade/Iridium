using Iridium.Protocol;
using Iridium.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class CommunityVoiceCompatibilityTests
{
    [Fact]
    public async Task LegacyChannelsBecomeTextAndDefaultRoleReceivesVoicePermissions()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE CommunityChannels (CommunityId TEXT NOT NULL, Id TEXT NOT NULL, CategoryId TEXT NULL,
                    Name TEXT NOT NULL, Position INTEGER NOT NULL, CreatedAt INTEGER NOT NULL,
                    PRIMARY KEY (CommunityId, Id));
                CREATE TABLE CommunityRoles (CommunityId TEXT NOT NULL, Id TEXT NOT NULL, Permissions INTEGER NOT NULL,
                    IsDefault INTEGER NOT NULL DEFAULT 0, PRIMARY KEY (CommunityId, Id));
                INSERT INTO CommunityChannels VALUES
                    ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA','BBBBBBBB-BBBB-BBBB-BBBB-BBBBBBBBBBBB',NULL,'general',0,0);
                INSERT INTO CommunityRoles VALUES
                    ('AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA','CCCCCCCC-CCCC-CCCC-CCCC-CCCCCCCCCCCC',1,1);
                """;
            await setup.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);

        await DatabaseCompatibility.EnsureCommunityVoiceSchemaAsync(db);
        await DatabaseCompatibility.EnsureCommunityVoiceSchemaAsync(db);

        await using var inspect = connection.CreateCommand();
        inspect.CommandText = "SELECT Kind FROM CommunityChannels;";
        Assert.Equal((long)CommunityChannelKind.Text, (long)(await inspect.ExecuteScalarAsync())!);
        inspect.CommandText = "SELECT Permissions FROM CommunityRoles WHERE IsDefault = 1;";
        var permissions = (CommunityPermission)(long)(await inspect.ExecuteScalarAsync())!;
        Assert.True((permissions & CommunityPermission.ConnectVoice) != 0);
        Assert.True((permissions & CommunityPermission.SpeakVoice) != 0);
    }
}
