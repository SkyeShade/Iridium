using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class CommunityManagementTests
{
    [Fact]
    public async Task OwnerHasEveryPermissionWithoutRoleAssignments()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var owner = Account("owner");
        var community = Community("owned", owner);
        fixture.Db.AddRange(owner, community);
        await fixture.Db.SaveChangesAsync();

        var access = await new CommunityAuthorizationService().GetAccessAsync(community.Id, owner.Id, fixture.Db);

        Assert.True(access.IsOwner);
        foreach (var permission in PermissionValues()) Assert.True(access.Has(permission));
    }

    [Fact]
    public async Task DefaultAndAssignedRolePermissionsAreUnionedAndCommunityScoped()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var ownerA = Account("owner-a");
        var ownerB = Account("owner-b");
        var member = Account("member");
        var communityA = Community("A", ownerA);
        var communityB = Community("B", ownerB);
        var membershipA = Member(communityA, member);
        var membershipB = Member(communityB, member);
        var everyoneA = Role(communityA, "@everyone", 0, CommunityPermission.ViewChannels, true);
        var senderA = Role(communityA, "Sender", 1, CommunityPermission.SendMessages);
        var adminA = Role(communityA, "Admin", 2, CommunityPermission.Administrator);
        var everyoneB = Role(communityB, "@everyone", 0, CommunityPermission.ViewChannels, true);
        fixture.Db.AddRange(ownerA, ownerB, member, communityA, communityB, membershipA, membershipB,
            everyoneA, senderA, adminA, everyoneB,
            Assignment(membershipA, senderA));
        await fixture.Db.SaveChangesAsync();
        var authorization = new CommunityAuthorizationService();

        var accessA = await authorization.GetAccessAsync(communityA.Id, member.Id, fixture.Db);
        var accessB = await authorization.GetAccessAsync(communityB.Id, member.Id, fixture.Db);

        Assert.True(accessA.Has(CommunityPermission.ViewChannels));
        Assert.True(accessA.Has(CommunityPermission.SendMessages));
        Assert.False(accessA.Has(CommunityPermission.ManageChannels));
        Assert.True(accessB.Has(CommunityPermission.ViewChannels));
        Assert.False(accessB.Has(CommunityPermission.SendMessages));
        Assert.False(await authorization.CanManageRoleAsync(communityB.Id, member.Id, everyoneB.Id, fixture.Db));

        fixture.Db.CommunityMemberRoles.Remove(await fixture.Db.CommunityMemberRoles.SingleAsync());
        await fixture.Db.SaveChangesAsync();
        Assert.False((await authorization.GetAccessAsync(communityA.Id, member.Id, fixture.Db))
            .Has(CommunityPermission.SendMessages));
    }

    [Fact]
    public async Task AdministratorAndRoleHierarchyNeverCrossCommunityBoundary()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var ownerA = Account("owner-a");
        var ownerB = Account("owner-b");
        var admin = Account("admin");
        var communityA = Community("A", ownerA);
        var communityB = Community("B", ownerB);
        var memberA = Member(communityA, admin);
        var memberB = Member(communityB, admin);
        var adminRole = Role(communityA, "Admin", 10, CommunityPermission.Administrator);
        var targetRoleB = Role(communityB, "Target", 1, CommunityPermission.None);
        fixture.Db.AddRange(ownerA, ownerB, admin, communityA, communityB, memberA, memberB, adminRole, targetRoleB,
            Assignment(memberA, adminRole));
        await fixture.Db.SaveChangesAsync();
        var authorization = new CommunityAuthorizationService();

        Assert.True(await authorization.HasPermissionAsync(communityA.Id, admin.Id, CommunityPermission.BanMembers, fixture.Db));
        Assert.False(await authorization.HasPermissionAsync(communityB.Id, admin.Id, CommunityPermission.ManageRoles, fixture.Db));
        Assert.False(await authorization.CanManageRoleAsync(communityB.Id, admin.Id, targetRoleB.Id, fixture.Db));
    }

    [Fact]
    public async Task MemberRoleChangesRespectActorHierarchyAndCommunityScope()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var ownerA = Account("owner-a");
        var ownerB = Account("owner-b");
        var manager = Account("manager");
        var target = Account("target");
        var communityA = Community("A", ownerA);
        var communityB = Community("B", ownerB);
        var managerA = Member(communityA, manager);
        var targetA = Member(communityA, target);
        var managerB = Member(communityB, manager);
        var targetB = Member(communityB, target);
        var managerRole = Role(communityA, "Manager", 10, CommunityPermission.ManageRoles);
        var lowerRole = Role(communityA, "Lower", 5, CommunityPermission.None);
        var higherRole = Role(communityA, "Higher", 11, CommunityPermission.None);
        var foreignRole = Role(communityB, "Foreign", 1, CommunityPermission.None);
        fixture.Db.AddRange(ownerA, ownerB, manager, target, communityA, communityB,
            managerA, targetA, managerB, targetB, managerRole, lowerRole, higherRole, foreignRole,
            Assignment(managerA, managerRole));
        await fixture.Db.SaveChangesAsync();
        var authorization = new CommunityAuthorizationService();

        Assert.True(await authorization.CanSetMemberRolesAsync(
            communityA.Id, manager.Id, target.Id, [lowerRole.Id], fixture.Db));
        Assert.False(await authorization.CanSetMemberRolesAsync(
            communityA.Id, manager.Id, target.Id, [higherRole.Id], fixture.Db));
        Assert.False(await authorization.CanSetMemberRolesAsync(
            communityB.Id, manager.Id, target.Id, [foreignRole.Id], fixture.Db));
        Assert.True(await authorization.CanSetMemberRolesAsync(
            communityA.Id, ownerA.Id, target.Id, [higherRole.Id], fixture.Db));
    }

    [Fact]
    public async Task CommunityOwnerCannotBeModerated()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var owner = Account("owner");
        var moderator = Account("moderator");
        var community = Community("A", owner);
        var membership = Member(community, moderator);
        var role = Role(community, "Moderator", 5, CommunityPermission.KickMembers | CommunityPermission.BanMembers);
        fixture.Db.AddRange(owner, moderator, community, membership, role, Assignment(membership, role));
        await fixture.Db.SaveChangesAsync();
        var authorization = new CommunityAuthorizationService();

        Assert.False(await authorization.CanModerateMemberAsync(
            community.Id, moderator.Id, owner.Id, CommunityPermission.KickMembers, fixture.Db));
        Assert.False(await authorization.CanModerateMemberAsync(
            community.Id, moderator.Id, owner.Id, CommunityPermission.BanMembers, fixture.Db));
    }

    [Fact]
    public void InviteTokensAreHighEntropyHashedAndValidityIsDeterministic()
    {
        var first = InviteTokenService.CreateToken();
        var second = InviteTokenService.CreateToken();
        Assert.NotEqual(first, second);
        Assert.True(first.Length >= 32);
        Assert.Equal(64, InviteTokenService.Hash(first).Length);
        Assert.DoesNotContain(first, InviteTokenService.Hash(first), StringComparison.Ordinal);

        var now = DateTimeOffset.UtcNow;
        var invite = new CommunityInvite
        {
            Id = Guid.NewGuid(), CommunityId = Guid.NewGuid(), Community = null!, TokenHash = InviteTokenService.Hash(first),
            CodePrefix = InviteTokenService.Prefix(first), CreatedByAccountId = Guid.NewGuid(), CreatedByAccount = null!, CreatedAt = now,
            ExpiresAt = now.AddMinutes(1), MaxUses = 1
        };
        Assert.Equal(CommunityInviteStatus.Valid, InviteTokenService.GetStatus(invite, now));
        invite.Uses = 1;
        Assert.Equal(CommunityInviteStatus.Exhausted, InviteTokenService.GetStatus(invite, now));
        invite.Uses = 0;
        Assert.Equal(CommunityInviteStatus.Expired, InviteTokenService.GetStatus(invite, now.AddMinutes(2)));
        invite.Revoked = true;
        Assert.Equal(CommunityInviteStatus.Revoked, InviteTokenService.GetStatus(invite, now));
    }

    [Fact]
    public async Task ValidInviteJoinsOnceAndDuplicateJoinDoesNotConsumeAnotherUse()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var owner = Account("owner");
        var otherOwner = Account("other-owner");
        var joining = Account("joining");
        var community = Community("A", owner);
        var otherCommunity = Community("B", otherOwner);
        var token = InviteTokenService.CreateToken();
        var invite = Invite(community, owner, token, maxUses: 2);
        fixture.Db.AddRange(owner, otherOwner, joining, community, otherCommunity, invite);
        await fixture.Db.SaveChangesAsync();
        var service = new CommunityInviteService();

        var first = await service.JoinAsync(token, joining, fixture.Db);
        var duplicate = await service.JoinAsync(token, joining, fixture.Db);

        Assert.False(first.AlreadyMember);
        Assert.True(duplicate.AlreadyMember);
        Assert.Equal(1, duplicate.Uses);
        Assert.Equal(1, await fixture.Db.CommunityMembers.CountAsync(value =>
            value.CommunityId == community.Id && value.AccountId == joining.Id));
        Assert.False(await fixture.Db.CommunityMembers.AnyAsync(value =>
            value.CommunityId == otherCommunity.Id && value.AccountId == joining.Id));
    }

    [Fact]
    public async Task ExpiredRevokedAndExhaustedInvitesCannotJoin()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var owner = Account("owner");
        var joining = Account("joining");
        var community = Community("A", owner);
        var expiredToken = InviteTokenService.CreateToken();
        var revokedToken = InviteTokenService.CreateToken();
        var exhaustedToken = InviteTokenService.CreateToken();
        var expired = Invite(community, owner, expiredToken); expired.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var revoked = Invite(community, owner, revokedToken); revoked.Revoked = true;
        var exhausted = Invite(community, owner, exhaustedToken, 1); exhausted.Uses = 1;
        fixture.Db.AddRange(owner, joining, community, expired, revoked, exhausted);
        await fixture.Db.SaveChangesAsync();
        var service = new CommunityInviteService();

        Assert.Equal(CommunityInviteStatus.Expired, (await Assert.ThrowsAsync<CommunityInviteJoinException>(
            () => service.JoinAsync(expiredToken, joining, fixture.Db))).Status);
        Assert.Equal(CommunityInviteStatus.Revoked, (await Assert.ThrowsAsync<CommunityInviteJoinException>(
            () => service.JoinAsync(revokedToken, joining, fixture.Db))).Status);
        Assert.Equal(CommunityInviteStatus.Exhausted, (await Assert.ThrowsAsync<CommunityInviteJoinException>(
            () => service.JoinAsync(exhaustedToken, joining, fixture.Db))).Status);
    }

    [Fact]
    public async Task CommunityBanBlocksOnlyThatCommunityInvite()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var owner = Account("owner");
        var joining = Account("joining");
        var community = Community("A", owner);
        var token = InviteTokenService.CreateToken();
        var invite = Invite(community, owner, token);
        var ban = new CommunityBan
        {
            CommunityId = community.Id, AccountId = joining.Id, BannedByAccountId = owner.Id,
            BannedAt = DateTimeOffset.UtcNow, Community = community, Account = joining, BannedByAccount = owner
        };
        fixture.Db.AddRange(owner, joining, community, invite, ban);
        await fixture.Db.SaveChangesAsync();

        var exception = await Assert.ThrowsAsync<CommunityInviteJoinException>(
            () => new CommunityInviteService().JoinAsync(token, joining, fixture.Db));

        Assert.Null(exception.Status);
        Assert.Contains("banned", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await fixture.Db.CommunityMembers.AnyAsync(value =>
            value.CommunityId == community.Id && value.AccountId == joining.Id));
    }

    [Fact]
    public async Task CompatibilityMigrationAddsOneDefaultRoleWithoutChangingCommunityIdentity()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var owner = Account("owner");
        var community = Community("Existing", owner);
        fixture.Db.AddRange(owner, community);
        await fixture.Db.SaveChangesAsync();

        await DatabaseCompatibility.EnsureCommunityManagementSchemaAsync(fixture.Db);

        var stored = await fixture.Db.Communities.SingleAsync();
        var roles = await fixture.Db.CommunityRoles.Where(value => value.CommunityId == community.Id).ToListAsync();
        Assert.Equal(community.Id, stored.Id);
        var defaultRole = Assert.Single(roles);
        Assert.True(defaultRole.IsDefault);
        Assert.Equal("@everyone", defaultRole.Name);
        Assert.True((defaultRole.Permissions & CommunityPermission.ViewChannels) != 0);
    }

    [Fact]
    public async Task CompatibilityMigrationNormalizesLegacySidebarPositionsWithoutChangingMembership()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var owner = Account("owner");
        var community = Community("Existing", owner);
        var category = new CommunityCategory
        {
            Id = Guid.NewGuid(), CommunityId = community.Id, Community = community, Name = "Category", Position = 0
        };
        var topChannel = new CommunityChannel
        {
            Id = Guid.NewGuid(), CommunityId = community.Id, Community = community, Category = category, Name = "welcome",
            CategoryId = category.Id, Position = 8, CreatedAt = DateTimeOffset.UtcNow
        };
        var nestedSecond = new CommunityChannel
        {
            Id = Guid.NewGuid(), CommunityId = community.Id, Community = community, Category = category,
            CategoryId = category.Id, Name = "second", Position = 9, CreatedAt = DateTimeOffset.UtcNow
        };
        var nestedFirst = new CommunityChannel
        {
            Id = Guid.NewGuid(), CommunityId = community.Id, Community = community, Category = category,
            CategoryId = category.Id, Name = "first", Position = 4, CreatedAt = DateTimeOffset.UtcNow
        };
        fixture.Db.AddRange(owner, community, category, topChannel, nestedSecond, nestedFirst);
        await fixture.Db.SaveChangesAsync();

        await DatabaseCompatibility.EnsureUnifiedCommunitySidebarOrderingAsync(fixture.Db);
        await DatabaseCompatibility.EnsureUnifiedCommunitySidebarOrderingAsync(fixture.Db);

        var storedCategory = await fixture.Db.CommunityCategories.SingleAsync(value => value.Id == category.Id);
        var storedChannels = await fixture.Db.CommunityChannels.Where(value => value.CategoryId == category.Id)
            .ToDictionaryAsync(value => value.Id);
        var storedCommunity = await fixture.Db.Communities.SingleAsync(value => value.Id == community.Id);
        Assert.Equal(1, storedChannels[topChannel.Id].Position);
        Assert.Equal(0, storedCategory.Position);
        Assert.Equal(0, storedChannels[nestedFirst.Id].Position);
        Assert.Equal(2, storedChannels[nestedSecond.Id].Position);
        Assert.Equal(owner.Id, storedCommunity.OwnerAccountId);
        Assert.Empty(await fixture.Db.CommunityMemberRoles.ToListAsync());
    }

    [Theory]
    [InlineData("https://friends.example/invite/abcdefghijklmnopqrstuvwxyz012345")]
    [InlineData("http://localhost:5159/invite/abcdefghijklmnopqrstuvwxyz012345")]
    public void InviteLinksParseNodeAuthorityAndOpaqueToken(string url)
    {
        var parsed = CommunityInviteLink.Find($"join us: {url}");
        Assert.NotNull(parsed);
        Assert.Equal(new Uri(url).Authority, parsed.NodeAuthority);
        Assert.Equal("abcdefghijklmnopqrstuvwxyz012345", parsed.Token);
    }

    private static IEnumerable<CommunityPermission> PermissionValues() =>
        Enum.GetValues<CommunityPermission>().Where(value => value != CommunityPermission.None && value != CommunityPermission.All);

    private static NodeAccount Account(string username) => new()
    {
        Id = Guid.NewGuid(), Username = username, DisplayName = username, PasswordHash = "test", CreatedAt = DateTimeOffset.UtcNow
    };

    private static Community Community(string name, NodeAccount owner) => new()
    {
        Id = Guid.NewGuid(), Name = name, OwnerAccountId = owner.Id, OwnerAccount = owner, CreatedAt = DateTimeOffset.UtcNow
    };

    private static CommunityMember Member(Community community, NodeAccount account) => new()
    {
        CommunityId = community.Id, AccountId = account.Id, Community = community, Account = account, JoinedAt = DateTimeOffset.UtcNow
    };

    private static CommunityRole Role(
        Community community, string name, int position, CommunityPermission permissions, bool isDefault = false) => new()
    {
        Id = Guid.NewGuid(), CommunityId = community.Id, Community = community, Name = name, Position = position,
        Permissions = permissions, IsDefault = isDefault
    };

    private static CommunityMemberRole Assignment(CommunityMember member, CommunityRole role) => new()
    {
        CommunityId = member.CommunityId, AccountId = member.AccountId, RoleId = role.Id, Member = member, Role = role
    };

    private static CommunityInvite Invite(Community community, NodeAccount creator, string token, int? maxUses = null) => new()
    {
        Id = Guid.NewGuid(), CommunityId = community.Id, Community = community,
        TokenHash = InviteTokenService.Hash(token), CodePrefix = InviteTokenService.Prefix(token),
        CreatedByAccountId = creator.Id, CreatedByAccount = creator, CreatedAt = DateTimeOffset.UtcNow, MaxUses = maxUses
    };

    private sealed class DatabaseFixture(SqliteConnection connection, IridiumDbContext db) : IAsyncDisposable
    {
        public IridiumDbContext Db { get; } = db;
        public static async Task<DatabaseFixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new IridiumDbContext(new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            return new DatabaseFixture(connection, db);
        }
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
