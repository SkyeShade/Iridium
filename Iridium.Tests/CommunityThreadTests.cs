using Iridium.Protocol;
using Iridium.Server.Api;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class CommunityThreadTests
{
    [Fact]
    public void ThreadPermissionsAreIndependentAndIncludedInAll()
    {
        var threadPermissions = CommunityPermission.CreatePublicThreads |
                                CommunityPermission.CreatePrivateThreads |
                                CommunityPermission.SendMessagesInThreads |
                                CommunityPermission.ManageThreads |
                                CommunityPermission.BypassSlowmode;
        Assert.Equal(threadPermissions, CommunityPermission.All & threadPermissions);
        Assert.Equal(CommunityPermission.None,
            CommunityPermission.SendMessages & CommunityPermission.SendMessagesInThreads);
        Assert.Equal(ThreadAutoArchiveDuration.ThreeDays,
            new CreateCommunityThreadRequest("test").AutoArchiveDuration);
        Assert.Contains(21600, CommunityThreadLimits.SlowmodeSeconds);
    }

    [Fact]
    public async Task ThreadConversationInheritsParentPermissionsButUsesIndependentSendBit()
    {
        await using var fixture = await Fixture.CreateAsync(CommunityPermission.ViewChannels |
            CommunityPermission.SendMessagesInThreads);
        var access = await fixture.Authorization.GetChannelAccessAsync(fixture.Community.Id,
            fixture.Discussion.Id, fixture.Member.Id, fixture.Db);
        Assert.True(access.Has(CommunityPermission.ViewChannels));
        Assert.True(access.Has(CommunityPermission.SendMessagesInThreads));
        Assert.False(access.Has(CommunityPermission.SendMessages));
    }

    [Fact]
    public async Task PrivateThreadRequiresParentVisibilityAndMembershipUnlessManageThreads()
    {
        await using var fixture = await Fixture.CreateAsync(CommunityPermission.ViewChannels |
            CommunityPermission.SendMessagesInThreads, isPrivate: true);
        var hidden = await fixture.Authorization.GetChannelAccessAsync(fixture.Community.Id,
            fixture.Discussion.Id, fixture.Member.Id, fixture.Db);
        Assert.False(hidden.Has(CommunityPermission.ViewChannels));

        fixture.Thread.OwnerAccountId = fixture.Member.Id;
        await fixture.Db.SaveChangesAsync();
        var creator = await fixture.Authorization.GetChannelAccessAsync(fixture.Community.Id,
            fixture.Discussion.Id, fixture.Member.Id, fixture.Db);
        Assert.True(creator.Has(CommunityPermission.ViewChannels));
        fixture.Thread.OwnerAccountId = fixture.Community.OwnerAccountId;
        await fixture.Db.SaveChangesAsync();

        fixture.Db.CommunityThreadMembers.Add(new CommunityThreadMember
        {
            ThreadId = fixture.Thread.Id, AccountId = fixture.Member.Id, JoinedAt = DateTimeOffset.UtcNow,
            Thread = fixture.Thread, Account = fixture.Member
        });
        await fixture.Db.SaveChangesAsync();
        var joined = await fixture.Authorization.GetChannelAccessAsync(fixture.Community.Id,
            fixture.Discussion.Id, fixture.Member.Id, fixture.Db);
        Assert.True(joined.Has(CommunityPermission.ViewChannels));

        fixture.Everyone.Permissions |= CommunityPermission.ManageThreads;
        fixture.Db.CommunityThreadMembers.RemoveRange(fixture.Db.CommunityThreadMembers);
        await fixture.Db.SaveChangesAsync();
        var moderator = await fixture.Authorization.GetChannelAccessAsync(fixture.Community.Id,
            fixture.Discussion.Id, fixture.Member.Id, fixture.Db);
        Assert.True(moderator.Has(CommunityPermission.ViewChannels));

        fixture.Db.CommunityPermissionOverwrites.Add(new CommunityPermissionOverwrite
        {
            Id = Guid.NewGuid(), CommunityId = fixture.Community.Id, Community = fixture.Community,
            ScopeType = PermissionOverwriteScopeType.Channel, ScopeId = fixture.Parent.Id,
            TargetType = PermissionOverwriteTargetType.Everyone, Deny = CommunityPermission.ViewChannels
        });
        await fixture.Db.SaveChangesAsync();
        var parentHidden = await fixture.Authorization.GetChannelAccessAsync(fixture.Community.Id,
            fixture.Discussion.Id, fixture.Member.Id, fixture.Db);
        Assert.False(parentHidden.Has(CommunityPermission.ViewChannels));
    }

    [Fact]
    public async Task AutoArchiveUsesLastActivityWithoutDeletingHistory()
    {
        await using var fixture = await Fixture.CreateAsync(CommunityPermission.ViewChannels);
        fixture.Thread.AutoArchiveDuration = ThreadAutoArchiveDuration.OneHour;
        fixture.Thread.LastActivityAt = DateTimeOffset.UtcNow.AddHours(-2);
        await fixture.Db.SaveChangesAsync();

        var changed = await CommunityThreadEndpoints.ArchiveExpiredAsync(fixture.Db);

        Assert.Contains(fixture.Community.Id, changed);
        Assert.True(fixture.Thread.IsArchived);
        Assert.NotNull(fixture.Thread.ArchivedAt);
        Assert.NotNull(await fixture.Db.CommunityChannels.FindAsync(fixture.Community.Id, fixture.Discussion.Id));
    }

    [Fact]
    public async Task CompatibilitySchemaIsAdditiveAndIdempotentBeforeEfMaterialization()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                PRAGMA foreign_keys = ON;
                CREATE TABLE Communities (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE Accounts (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE ChannelMessages (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE CommunityChannels (
                    CommunityId TEXT NOT NULL, Id TEXT NOT NULL, CategoryId TEXT NULL,
                    ParentForumChannelId TEXT NULL, Name TEXT NOT NULL, Kind INTEGER NOT NULL DEFAULT 0,
                    PermissionsSyncedToCategory INTEGER NOT NULL DEFAULT 0, Position INTEGER NOT NULL,
                    CreatedAt TEXT NOT NULL, PRIMARY KEY (CommunityId, Id));
                """;
            await command.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);
        await DatabaseCompatibility.EnsureCommunityThreadSchemaAsync(db);
        await DatabaseCompatibility.EnsureCommunityThreadSchemaAsync(db);
        Assert.Contains("ParentThreadChannelId", await NamesAsync(connection,
            "PRAGMA table_info('CommunityChannels');", 1));
        Assert.Contains("CommunityThreads", await NamesAsync(connection,
            "SELECT name FROM sqlite_master WHERE type='table';", 0));
        Assert.Contains("CommunityThreadMembers", await NamesAsync(connection,
            "SELECT name FROM sqlite_master WHERE type='table';", 0));
    }

    [Fact]
    public void UiContractsExposeDiscoveryNestedChildrenAndFullConversation()
    {
        var root = RepositoryRoot();
        var home = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Pages", "Home.razor"));
        var row = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageRow.razor"));
        var browser = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "ThreadBrowser.razor"));
        Assert.Contains("ThreadConversationView", home);
        Assert.Contains("ThreadSidebarChildren", File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components",
            "CommunitySidebar.razor")));
        Assert.Contains("message-thread-indicator", row);
        Assert.Contains("Create Thread", row);
        Assert.Contains("CommunityThreadListKind.Archived", browser);
        Assert.Contains("Private Thread", browser);
    }

    private static async Task<HashSet<string>> NamesAsync(SqliteConnection connection, string sql, int ordinal)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand(); command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(ordinal));
        return result;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null;
             directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Iridium.sln"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate Iridium.sln.");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required SqliteConnection Connection { get; init; }
        public required IridiumDbContext Db { get; init; }
        public required CommunityAuthorizationService Authorization { get; init; }
        public required Community Community { get; init; }
        public required NodeAccount Member { get; init; }
        public required CommunityRole Everyone { get; init; }
        public required CommunityChannel Parent { get; init; }
        public required CommunityChannel Discussion { get; init; }
        public required CommunityThread Thread { get; init; }

        public static async Task<Fixture> CreateAsync(CommunityPermission permissions, bool isPrivate = false)
        {
            var connection = new SqliteConnection("Data Source=:memory:"); await connection.OpenAsync();
            var db = new IridiumDbContext(new DbContextOptionsBuilder<IridiumDbContext>()
                .UseSqlite(connection).Options); await db.Database.EnsureCreatedAsync();
            var owner = Account("owner"); var member = Account("member");
            var community = new Community { Id = Guid.NewGuid(), Name = "Threads", OwnerAccountId = owner.Id,
                OwnerAccount = owner, CreatedAt = DateTimeOffset.UtcNow };
            var membership = new CommunityMember { CommunityId = community.Id, AccountId = member.Id,
                Community = community, Account = member, JoinedAt = DateTimeOffset.UtcNow };
            var everyone = new CommunityRole { Id = Guid.NewGuid(), CommunityId = community.Id,
                Community = community, Name = "@everyone", IsDefault = true, Permissions = permissions };
            var parent = Channel(community, "general"); var discussion = Channel(community, "thread");
            discussion.ParentThreadChannelId = parent.Id;
            var thread = new CommunityThread { Id = Guid.NewGuid(), CommunityId = community.Id,
                ParentChannelId = parent.Id, DiscussionChannelId = discussion.Id, OwnerAccountId = owner.Id,
                Name = "thread", CreatedAt = DateTimeOffset.UtcNow, LastActivityAt = DateTimeOffset.UtcNow,
                IsPrivate = isPrivate, Community = community, ParentChannel = parent,
                DiscussionChannel = discussion, OwnerAccount = owner };
            db.AddRange(owner, member, community, membership, everyone, parent, discussion, thread);
            await db.SaveChangesAsync();
            return new Fixture { Connection = connection, Db = db, Authorization = new(), Community = community,
                Member = member, Everyone = everyone, Parent = parent, Discussion = discussion, Thread = thread };
        }

        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await Connection.DisposeAsync(); }
        private static NodeAccount Account(string name) => new() { Id = Guid.NewGuid(), Username = name,
            DisplayName = name, PasswordHash = "test", CreatedAt = DateTimeOffset.UtcNow };
        private static CommunityChannel Channel(Community community, string name) => new()
        { Id = Guid.NewGuid(), CommunityId = community.Id, Community = community, Name = name,
            Kind = CommunityChannelKind.Text, CreatedAt = DateTimeOffset.UtcNow };
    }
}
