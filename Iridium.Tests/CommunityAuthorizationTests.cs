using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class CommunityAuthorizationTests
{
    [Fact]
    public void ChannelOverwriteResolutionUsesDiscordCompatibleLayerOrder()
    {
        var roleA = Guid.NewGuid();
        var roleB = Guid.NewGuid();
        var account = Guid.NewGuid();
        var rows = new[]
        {
            Overwrite(PermissionOverwriteTargetType.Everyone, null,
                CommunityPermission.ConnectVoice, CommunityPermission.ViewChannels),
            Overwrite(PermissionOverwriteTargetType.Role, roleA,
                CommunityPermission.ViewChannels, CommunityPermission.SendMessages),
            Overwrite(PermissionOverwriteTargetType.Role, roleB,
                CommunityPermission.SendMessages, CommunityPermission.ViewChannels),
            Overwrite(PermissionOverwriteTargetType.Member, account,
                CommunityPermission.ShareScreen, CommunityPermission.ConnectVoice)
        };

        var resolved = CommunityAuthorizationService.Resolve(
            CommunityPermission.ViewChannels | CommunityPermission.SendMessages, rows, [roleA, roleB], account);

        // At the combined-role layer collective denies apply first and collective allows apply last.
        Assert.True(resolved.HasFlag(CommunityPermission.ViewChannels));
        Assert.True(resolved.HasFlag(CommunityPermission.SendMessages));
        Assert.False(resolved.HasFlag(CommunityPermission.ConnectVoice));
        Assert.True(resolved.HasFlag(CommunityPermission.ShareScreen));
    }

    [Fact]
    public void MemberAllowIsAppliedAfterMemberDenyAndRoleOverwrites()
    {
        var account = Guid.NewGuid();
        var resolved = CommunityAuthorizationService.Resolve(CommunityPermission.None,
        [
            Overwrite(PermissionOverwriteTargetType.Everyone, null, CommunityPermission.None,
                CommunityPermission.ViewChannels),
            Overwrite(PermissionOverwriteTargetType.Member, account, CommunityPermission.ViewChannels,
                CommunityPermission.SendMessages)
        ], [], account);
        Assert.True(resolved.HasFlag(CommunityPermission.ViewChannels));
        Assert.False(resolved.HasFlag(CommunityPermission.SendMessages));
    }
    [Fact]
    public async Task ManagementRoleNeverLeaksIntoAnotherCommunity()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var ownerA = Account("owner-a");
        var ownerB = Account("owner-b");
        var moderator = Account("moderator");
        var communityA = Community("A", ownerA);
        var communityB = Community("B", ownerB);
        var memberA = Member(communityA, moderator);
        var memberB = Member(communityB, moderator);
        var roleA = new CommunityRole
        {
            Id = Guid.NewGuid(), CommunityId = communityA.Id, Community = communityA,
            Name = "Manager", Permissions = CommunityPermission.ManageCommunity
        };
        var assignment = new CommunityMemberRole
        {
            CommunityId = communityA.Id, AccountId = moderator.Id, RoleId = roleA.Id,
            Member = memberA, Role = roleA
        };
        db.AddRange(ownerA, ownerB, moderator, communityA, communityB, memberA, memberB, roleA, assignment);
        await db.SaveChangesAsync();

        var authorization = new CommunityAuthorizationService();
        Assert.True(await authorization.CanManageAsync(communityA.Id, moderator.Id, db));
        Assert.False(await authorization.CanManageAsync(communityB.Id, moderator.Id, db));
        Assert.True(await authorization.CanManageAsync(communityB.Id, ownerB.Id, db));
    }

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

    private static CommunityPermissionOverwrite Overwrite(PermissionOverwriteTargetType type, Guid? targetId,
        CommunityPermission allow, CommunityPermission deny) => new()
    {
        Id = Guid.NewGuid(), CommunityId = Guid.NewGuid(), Community = null!, ScopeId = Guid.NewGuid(),
        ScopeType = PermissionOverwriteScopeType.Channel, TargetType = type, TargetId = targetId,
        Allow = allow, Deny = deny
    };
}
