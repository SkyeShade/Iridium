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
}
