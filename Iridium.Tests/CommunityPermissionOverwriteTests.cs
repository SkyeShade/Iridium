using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class CommunityPermissionOverwriteTests
{
    [Fact]
    public void GenericOverwriteCatalogDoesNotExposePrivateVisibilityBit()
    {
        Assert.DoesNotContain(CommunityPermission.ViewChannels,
            CommunityPermissionCatalog.GeneralOverwritePermissions);
        Assert.Contains(CommunityPermission.ManageChannels,
            CommunityPermissionCatalog.GeneralOverwritePermissions);
        Assert.Contains(CommunityPermission.ManagePermissions,
            CommunityPermissionCatalog.GeneralOverwritePermissions);
    }

    [Fact]
    public void MemberColorUsesHighestPriorityAssignedColoredRole()
    {
        var lower = new CommunityRoleDto(Guid.NewGuid(), Guid.NewGuid(), "Member", 2,
            CommunityPermission.None, false, "#336699", false);
        var higher = new CommunityRoleDto(Guid.NewGuid(), lower.CommunityId, "VIP", 8,
            CommunityPermission.None, false, "#E0A040", false);
        var member = new CommunityMemberDto(Guid.NewGuid(), "alice", "Alice", null, null, null,
            DateTimeOffset.UtcNow, false, PublicPresence.Online, [lower.Id, higher.Id]);

        Assert.Equal("#E0A040", CommunityRolePresentation.MemberColor(member, [lower, higher]));
        Assert.Equal("#336699", CommunityRolePresentation.RoleColor(lower.Id, [lower, higher]));
    }

    [Fact]
    public async Task CompatibilityMigrationSeedsCategorizedChannelsOnceWithoutResyncingCustomChannels()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE Communities (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE CommunityChannels (CommunityId TEXT NOT NULL, Id TEXT NOT NULL, CategoryId TEXT NULL,
                    Name TEXT NOT NULL, Kind INTEGER NOT NULL, Position INTEGER NOT NULL, CreatedAt INTEGER NOT NULL,
                    PRIMARY KEY (CommunityId, Id));
                INSERT INTO Communities VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa');
                INSERT INTO CommunityChannels VALUES ('aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',
                    'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb', 'cccccccc-cccc-cccc-cccc-cccccccccccc',
                    'chat', 0, 0, 0);
                """;
            await command.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);
        await DatabaseCompatibility.EnsureCommunityPermissionOverwriteSchemaAsync(db);
        Assert.Equal(1L, await SyncValueAsync(connection));
        await db.Database.ExecuteSqlRawAsync("UPDATE CommunityChannels SET PermissionsSyncedToCategory = 0");
        await DatabaseCompatibility.EnsureCommunityPermissionOverwriteSchemaAsync(db);
        Assert.Equal(0L, await SyncValueAsync(connection));
    }

    [Fact]
    public async Task SyncedChannelUsesImmediateCategoryWhileUnsyncedChannelUsesOwnRows()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var owner = Account("owner"); var member = Account("member");
        var community = new Community { Id = Guid.NewGuid(), Name = "Test", OwnerAccountId = owner.Id,
            OwnerAccount = owner, CreatedAt = DateTimeOffset.UtcNow };
        var membership = new CommunityMember { CommunityId = community.Id, AccountId = member.Id,
            Community = community, Account = member, JoinedAt = DateTimeOffset.UtcNow };
        var everyone = new CommunityRole { Id = Guid.NewGuid(), CommunityId = community.Id, Community = community,
            Name = "@everyone", IsDefault = true, Permissions = CommunityPermission.ViewChannels |
                CommunityPermission.SendMessages };
        var category = new CommunityCategory { Id = Guid.NewGuid(), CommunityId = community.Id, Community = community,
            Name = "private", Position = 0 };
        var synced = Channel(community, category, "synced", true);
        var custom = Channel(community, category, "custom", false);
        db.AddRange(owner, member, community, membership, everyone, category, synced, custom,
            Row(community, PermissionOverwriteScopeType.Category, category.Id, CommunityPermission.None,
                CommunityPermission.ViewChannels),
            Row(community, PermissionOverwriteScopeType.Channel, custom.Id, CommunityPermission.ViewChannels,
                CommunityPermission.SendMessages));
        await db.SaveChangesAsync();

        var authorization = new CommunityAuthorizationService();
        var syncedAccess = await authorization.GetChannelAccessAsync(community.Id, synced.Id, member.Id, db);
        var customAccess = await authorization.GetChannelAccessAsync(community.Id, custom.Id, member.Id, db);
        Assert.False(syncedAccess.Has(CommunityPermission.ViewChannels));
        Assert.True(customAccess.Has(CommunityPermission.ViewChannels));
        Assert.False(customAccess.Has(CommunityPermission.SendMessages));
    }

    [Fact]
    public async Task OwnerAndAdministratorBypassDenyOverwrite()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var owner = Account("owner"); var admin = Account("admin");
        var community = new Community { Id = Guid.NewGuid(), Name = "Test", OwnerAccountId = owner.Id,
            OwnerAccount = owner, CreatedAt = DateTimeOffset.UtcNow };
        var membership = new CommunityMember { CommunityId = community.Id, AccountId = admin.Id,
            Community = community, Account = admin, JoinedAt = DateTimeOffset.UtcNow };
        var role = new CommunityRole { Id = Guid.NewGuid(), CommunityId = community.Id, Community = community,
            Name = "Admin", Permissions = CommunityPermission.Administrator };
        var assignment = new CommunityMemberRole { CommunityId = community.Id, AccountId = admin.Id,
            RoleId = role.Id, Member = membership, Role = role };
        var channel = Channel(community, null, "hidden", false);
        db.AddRange(owner, admin, community, membership, role, assignment, channel,
            Row(community, PermissionOverwriteScopeType.Channel, channel.Id, CommunityPermission.None,
                CommunityPermission.ViewChannels));
        await db.SaveChangesAsync();
        var authorization = new CommunityAuthorizationService();
        Assert.True((await authorization.GetChannelAccessAsync(community.Id, channel.Id, owner.Id, db))
            .Has(CommunityPermission.ViewChannels));
        Assert.True((await authorization.GetChannelAccessAsync(community.Id, channel.Id, admin.Id, db))
            .Has(CommunityPermission.ViewChannels));
    }

    [Fact]
    public async Task PrivateChannelAllowsSelectedRoleAndMemberButRemainsHiddenFromOthers()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);
        await db.Database.EnsureCreatedAsync();
        var owner = Account("owner"); var moderator = Account("moderator");
        var alice = Account("alice"); var bob = Account("bob");
        var community = new Community { Id = Guid.NewGuid(), Name = "Test", OwnerAccountId = owner.Id,
            OwnerAccount = owner, CreatedAt = DateTimeOffset.UtcNow };
        var moderatorMember = Member(community, moderator); var aliceMember = Member(community, alice);
        var bobMember = Member(community, bob);
        var everyone = new CommunityRole { Id = Guid.NewGuid(), CommunityId = community.Id, Community = community,
            Name = "@everyone", IsDefault = true, Permissions = CommunityPermission.ViewChannels |
                CommunityPermission.SendMessages };
        var moderatorRole = new CommunityRole { Id = Guid.NewGuid(), CommunityId = community.Id, Community = community,
            Name = "Moderator", Permissions = CommunityPermission.None };
        var assignment = new CommunityMemberRole { CommunityId = community.Id, AccountId = moderator.Id,
            RoleId = moderatorRole.Id, Member = moderatorMember, Role = moderatorRole };
        var channel = Channel(community, null, "staff", false);
        db.AddRange(owner, moderator, alice, bob, community, moderatorMember, aliceMember, bobMember, everyone,
            moderatorRole, assignment, channel,
            Row(community, PermissionOverwriteScopeType.Channel, channel.Id,
                PermissionOverwriteTargetType.Everyone, null, CommunityPermission.None, CommunityPermission.ViewChannels),
            Row(community, PermissionOverwriteScopeType.Channel, channel.Id,
                PermissionOverwriteTargetType.Role, moderatorRole.Id, CommunityPermission.ViewChannels,
                CommunityPermission.SendMessages),
            Row(community, PermissionOverwriteScopeType.Channel, channel.Id,
                PermissionOverwriteTargetType.Member, alice.Id, CommunityPermission.ViewChannels, CommunityPermission.None));
        await db.SaveChangesAsync();

        var authorization = new CommunityAuthorizationService();
        var moderatorAccess = await authorization.GetChannelAccessAsync(community.Id, channel.Id, moderator.Id, db);
        var aliceAccess = await authorization.GetChannelAccessAsync(community.Id, channel.Id, alice.Id, db);
        Assert.True(moderatorAccess.Has(CommunityPermission.ViewChannels));
        Assert.False(moderatorAccess.Has(CommunityPermission.SendMessages));
        Assert.True(aliceAccess.Has(CommunityPermission.ViewChannels));
        Assert.True(aliceAccess.Has(CommunityPermission.SendMessages));
        Assert.False((await authorization.GetChannelAccessAsync(community.Id, channel.Id, bob.Id, db))
            .Has(CommunityPermission.ViewChannels));
        Assert.True((await authorization.GetChannelAccessAsync(community.Id, channel.Id, owner.Id, db))
            .Has(CommunityPermission.ViewChannels));
    }

    private static NodeAccount Account(string name) => new() { Id = Guid.NewGuid(), Username = name,
        DisplayName = name, PasswordHash = "test", CreatedAt = DateTimeOffset.UtcNow };
    private static CommunityChannel Channel(Community community, CommunityCategory? category, string name, bool synced) =>
        new() { Id = Guid.NewGuid(), CommunityId = community.Id, Community = community, Category = category,
            CategoryId = category?.Id, Name = name, Kind = CommunityChannelKind.Text, Position = 0,
            CreatedAt = DateTimeOffset.UtcNow, PermissionsSyncedToCategory = synced };
    private static CommunityMember Member(Community community, NodeAccount account) => new()
    {
        CommunityId = community.Id, AccountId = account.Id, Community = community, Account = account,
        JoinedAt = DateTimeOffset.UtcNow
    };
    private static CommunityPermissionOverwrite Row(Community community, PermissionOverwriteScopeType scopeType,
        Guid scopeId, CommunityPermission allow, CommunityPermission deny) => new()
        {
            Id = Guid.NewGuid(), CommunityId = community.Id, Community = community, ScopeType = scopeType,
            ScopeId = scopeId, TargetType = PermissionOverwriteTargetType.Everyone,
            Allow = allow, Deny = deny
        };
    private static CommunityPermissionOverwrite Row(Community community, PermissionOverwriteScopeType scopeType,
        Guid scopeId, PermissionOverwriteTargetType targetType, Guid? targetId, CommunityPermission allow,
        CommunityPermission deny) => new()
        {
            Id = Guid.NewGuid(), CommunityId = community.Id, Community = community, ScopeType = scopeType,
            ScopeId = scopeId, TargetType = targetType, TargetId = targetId, Allow = allow, Deny = deny
        };
    private static async Task<long> SyncValueAsync(SqliteConnection connection)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT PermissionsSyncedToCategory FROM CommunityChannels LIMIT 1";
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
