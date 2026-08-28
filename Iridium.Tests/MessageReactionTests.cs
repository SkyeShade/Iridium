using Iridium.Protocol;
using Iridium.Server.Domain;
using Iridium.Server.Messages;
using Iridium.Server.Persistence;
using Iridium.Server.Security;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class MessageReactionTests
{
    private static readonly ReactionEmojiRequest ThumbsUp = new(ReactionEmojiKind.Standard, "👍");
    private static readonly ReactionEmojiRequest Heart = new(ReactionEmojiKind.Standard, "❤️");

    [Fact]
    public async Task UsersJoinOneGroupAndCannotDuplicateThenOwnToggleRemovesIt()
    {
        await using var fixture = await Fixture.CreateAsync();
        var first = await fixture.Service.AddAsync(fixture.Message.Id, fixture.Alice.Id, ThumbsUp);
        var duplicate = await fixture.Service.AddAsync(fixture.Message.Id, fixture.Alice.Id, ThumbsUp);
        var joined = await fixture.Service.AddAsync(fixture.Message.Id, fixture.Bob.Id, ThumbsUp);

        Assert.Equal(1, first.Count);
        Assert.Equal(1, duplicate.Count);
        Assert.Equal(2, joined.Count);
        Assert.Equal(2, await fixture.Db.MessageReactions.CountAsync());

        var removed = await fixture.Service.RemoveAsync(fixture.Message.Id, fixture.Alice.Id, ThumbsUp);
        Assert.Equal(1, removed.Count);
        var last = await fixture.Service.RemoveAsync(fixture.Message.Id, fixture.Bob.Id, ThumbsUp);
        Assert.Equal(0, last.Count);
        Assert.Empty((await fixture.Service.AttachSummariesAsync([fixture.Dto()], fixture.Bob.Id))[0].Reactions!);
    }

    [Fact]
    public async Task AddReactionsDenialBlocksNewGroupButAllowsExistingGroup()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.AddAsync(fixture.Message.Id, fixture.Owner.Id, ThumbsUp);
        fixture.DefaultRole.Permissions &= ~CommunityPermission.AddReactions;
        await fixture.Db.SaveChangesAsync();

        var joined = await fixture.Service.AddAsync(fixture.Message.Id, fixture.Alice.Id, ThumbsUp);
        Assert.Equal(2, joined.Count);
        var denied = await Assert.ThrowsAsync<HubException>(() =>
            fixture.Service.AddAsync(fixture.Message.Id, fixture.Alice.Id, Heart));
        Assert.Contains("new reaction", denied.Message, StringComparison.OrdinalIgnoreCase);

        var ownerOverride = await fixture.Service.AddAsync(fixture.Message.Id, fixture.Owner.Id, Heart);
        Assert.Equal(1, ownerOverride.Count);
    }

    [Fact]
    public async Task ExternalPermissionControlsCreatingAndJoiningButLocalAndStandardRemainUsable()
    {
        await using var fixture = await Fixture.CreateAsync();
        var local = fixture.AddEmoji(fixture.Community, "local");
        var externalCommunity = fixture.AddCommunity("Elsewhere", fixture.Owner);
        fixture.AddMember(externalCommunity, fixture.Owner);
        fixture.AddMember(externalCommunity, fixture.Alice);
        fixture.AddMember(externalCommunity, fixture.Bob);
        var external = fixture.AddEmoji(externalCommunity, "external");
        await fixture.Db.SaveChangesAsync();

        fixture.DefaultRole.Permissions &= ~CommunityPermission.UseExternalEmoji;
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.AddAsync(fixture.Message.Id, fixture.Alice.Id, ThumbsUp);
        await fixture.Service.AddAsync(fixture.Message.Id, fixture.Alice.Id,
            new(ReactionEmojiKind.Custom, CustomEmojiId: local.Id));
        await Assert.ThrowsAsync<HubException>(() => fixture.Service.AddAsync(fixture.Message.Id,
            fixture.Alice.Id, new(ReactionEmojiKind.Custom, CustomEmojiId: external.Id)));

        fixture.DefaultRole.Permissions |= CommunityPermission.UseExternalEmoji;
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.AddAsync(fixture.Message.Id, fixture.Owner.Id,
            new(ReactionEmojiKind.Custom, CustomEmojiId: external.Id));
        fixture.DefaultRole.Permissions &= ~CommunityPermission.UseExternalEmoji;
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<HubException>(() => fixture.Service.AddAsync(fixture.Message.Id,
            fixture.Bob.Id, new(ReactionEmojiKind.Custom, CustomEmojiId: external.Id)));
    }

    [Fact]
    public async Task ManageMessagesRemovesOthersWhileMembersCannotAndUsersRemoveOwn()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.AddAsync(fixture.Message.Id, fixture.Alice.Id, ThumbsUp);
        await Assert.ThrowsAsync<HubException>(() => fixture.Service.RemoveAsync(fixture.Message.Id,
            fixture.Bob.Id, ThumbsUp, fixture.Alice.Id));

        var moderator = fixture.AddAccount("moderator");
        fixture.AddMember(fixture.Community, moderator);
        var role = new CommunityRole { Id = Guid.NewGuid(), CommunityId = fixture.Community.Id,
            Community = fixture.Community, Name = "Moderator", Position = 1,
            Permissions = CommunityPermission.ManageMessages };
        fixture.Db.CommunityRoles.Add(role);
        fixture.Db.CommunityMemberRoles.Add(new CommunityMemberRole { CommunityId = fixture.Community.Id,
            AccountId = moderator.Id, RoleId = role.Id, Member = null!, Role = role });
        await fixture.Db.SaveChangesAsync();
        var removed = await fixture.Service.RemoveAsync(fixture.Message.Id, moderator.Id, ThumbsUp,
            fixture.Alice.Id);
        Assert.Equal(0, removed.Count);
    }

    [Fact]
    public async Task AdministratorBypassesChannelReactionAndModerationDenials()
    {
        await using var fixture = await Fixture.CreateAsync();
        var admin = fixture.AddAccount("admin");
        fixture.AddMember(fixture.Community, admin);
        var role = new CommunityRole { Id = Guid.NewGuid(), CommunityId = fixture.Community.Id,
            Community = fixture.Community, Name = "Admin", Position = 2,
            Permissions = CommunityPermission.Administrator };
        fixture.Db.CommunityRoles.Add(role);
        fixture.Db.CommunityMemberRoles.Add(new CommunityMemberRole { CommunityId = fixture.Community.Id,
            AccountId = admin.Id, RoleId = role.Id, Member = null!, Role = role });
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.AddAsync(fixture.Message.Id, fixture.Alice.Id, ThumbsUp);
        fixture.Db.CommunityPermissionOverwrites.Add(new CommunityPermissionOverwrite
        {
            CommunityId = fixture.Community.Id, Community = fixture.Community,
            ScopeType = PermissionOverwriteScopeType.Channel, ScopeId = fixture.Channel.Id,
            TargetType = PermissionOverwriteTargetType.Everyone,
            Deny = CommunityPermission.AddReactions | CommunityPermission.ManageMessages,
            Allow = CommunityPermission.None
        });
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.AddAsync(fixture.Message.Id, admin.Id, Heart);
        var removed = await fixture.Service.RemoveAsync(fixture.Message.Id, admin.Id, ThumbsUp,
            fixture.Alice.Id);
        Assert.Equal(0, removed.Count);
    }

    [Fact]
    public async Task HiddenChannelIsRejectedAndDeletedCustomEmojiUsesTombstone()
    {
        await using var fixture = await Fixture.CreateAsync();
        var custom = fixture.AddEmoji(fixture.Community, "mudrock");
        await fixture.Db.SaveChangesAsync();
        var request = new ReactionEmojiRequest(ReactionEmojiKind.Custom, CustomEmojiId: custom.Id);
        await fixture.Service.AddAsync(fixture.Message.Id, fixture.Alice.Id, request);
        fixture.Db.CommunityEmojis.Remove(custom);
        await fixture.Db.SaveChangesAsync();

        var summary = Assert.Single((await fixture.Service.AttachSummariesAsync([fixture.Dto()],
            fixture.Alice.Id))[0].Reactions!);
        Assert.Equal(custom.Id, summary.Emoji.CustomEmojiId);
        Assert.Equal("mudrock", summary.Emoji.CustomEmojiName);
        Assert.False(summary.Emoji.CustomEmojiAvailable);
        Assert.Equal(0, (await fixture.Service.RemoveAsync(fixture.Message.Id, fixture.Alice.Id, request)).Count);

        fixture.Db.CommunityPermissionOverwrites.Add(new CommunityPermissionOverwrite
        {
            CommunityId = fixture.Community.Id, Community = fixture.Community,
            ScopeType = PermissionOverwriteScopeType.Channel, ScopeId = fixture.Channel.Id,
            TargetType = PermissionOverwriteTargetType.Everyone,
            Deny = CommunityPermission.ViewChannels, Allow = CommunityPermission.None
        });
        await fixture.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.AddAsync(
            fixture.Message.Id, fixture.Bob.Id, ThumbsUp));
    }

    [Fact]
    public async Task SummaryLoadAggregatesPageInOneShapeAndTracksCurrentAccount()
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Service.AddAsync(fixture.Message.Id, fixture.Alice.Id, ThumbsUp);
        await fixture.Service.AddAsync(fixture.Message.Id, fixture.Bob.Id, ThumbsUp);
        await fixture.Service.AddAsync(fixture.Message.Id, fixture.Bob.Id, Heart);
        var second = fixture.AddMessage("second");
        await fixture.Db.SaveChangesAsync();
        await fixture.Service.AddAsync(second.Id, fixture.Alice.Id, ThumbsUp);

        var mapped = await fixture.Service.AttachSummariesAsync([fixture.Dto(), fixture.Dto(second)],
            fixture.Alice.Id);
        Assert.Equal(2, mapped.Count);
        Assert.Equal(2, mapped[0].Reactions!.Count);
        Assert.True(mapped[0].Reactions!.Single(value => value.Emoji.StandardEmojiValue == "👍").CurrentUserReacted);
        Assert.False(mapped[0].Reactions!.Single(value => value.Emoji.StandardEmojiValue == "❤️").CurrentUserReacted);
        Assert.Single(mapped[1].Reactions!);
    }

    [Fact]
    public async Task CompatibilityCreatesIndexesAndUnlocksExistingDefaultRoles()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.DefaultRole.Permissions &= ~(CommunityPermission.AddReactions |
                                             CommunityPermission.UseExternalEmoji);
        await fixture.Db.SaveChangesAsync();
        await DatabaseCompatibility.EnsureMessageReactionSchemaAsync(fixture.Db);
        fixture.Db.ChangeTracker.Clear();
        var permissions = await fixture.Db.CommunityRoles.Where(value => value.IsDefault)
            .Select(value => value.Permissions).SingleAsync();
        Assert.True(permissions.HasFlag(CommunityPermission.AddReactions));
        Assert.True(permissions.HasFlag(CommunityPermission.UseExternalEmoji));
        var indexes = new List<string>();
        await using var command = fixture.Db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index' AND tbl_name = 'MessageReactions'";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) indexes.Add(reader.GetString(0));
        Assert.Contains("IX_MessageReactions_MessageId_EmojiKey", indexes);
        Assert.Contains("IX_MessageReactions_AccountId", indexes);
    }

    [Fact]
    public async Task DistinctReactionLimitIsEnforcedServerSide()
    {
        await using var fixture = await Fixture.CreateAsync();
        var emoji = StandardEmojiCatalog.All.Take(MessageReactionLimits.MaximumDistinctPerMessage + 1).ToArray();
        foreach (var standard in emoji.Take(MessageReactionLimits.MaximumDistinctPerMessage))
            await fixture.Service.AddAsync(fixture.Message.Id, fixture.Alice.Id,
                new(ReactionEmojiKind.Standard, standard.Glyph));
        var denied = await Assert.ThrowsAsync<HubException>(() => fixture.Service.AddAsync(fixture.Message.Id,
            fixture.Alice.Id, new(ReactionEmojiKind.Standard, emoji[^1].Glyph)));
        Assert.Contains("at most", denied.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DirectParticipantsReactWithoutCommunityPermissionsAndSummariesRemainCollisionSafe()
    {
        await using var fixture = await Fixture.CreateAsync();
        fixture.DefaultRole.Permissions = CommunityPermission.None;
        var custom = fixture.AddEmoji(fixture.Community, "dm_custom");
        await fixture.Db.SaveChangesAsync();

        var first = await fixture.Service.AddDirectAsync(fixture.DirectMessageEntity.Id, fixture.Alice.Id, ThumbsUp);
        var joined = await fixture.Service.AddDirectAsync(fixture.DirectMessageEntity.Id, fixture.Bob.Id, ThumbsUp);
        var customAdded = await fixture.Service.AddDirectAsync(fixture.DirectMessageEntity.Id, fixture.Alice.Id,
            new(ReactionEmojiKind.Custom, CustomEmojiId: custom.Id));

        Assert.Equal(1, first.Count);
        Assert.Equal(2, joined.Count);
        Assert.Equal(1, customAdded.Count);
        Assert.Equal(3, await fixture.Db.DirectMessageReactions.CountAsync());
        Assert.Empty(await fixture.Db.MessageReactions.ToListAsync());

        var summaries = await fixture.Service.AttachDirectSummariesAsync([fixture.DirectDto()], fixture.Alice.Id);
        Assert.Equal(2, summaries[0].Reactions!.Count);
        Assert.True(summaries[0].Reactions!.Single(value =>
            value.Emoji.StandardEmojiValue == ThumbsUp.StandardEmojiValue).CurrentUserReacted);
        Assert.Equal(custom.Id, summaries[0].Reactions!.Single(value =>
            value.Emoji.Kind == ReactionEmojiKind.Custom).Emoji.CustomEmojiId);

        var removed = await fixture.Service.RemoveDirectAsync(fixture.DirectMessageEntity.Id, fixture.Alice.Id, ThumbsUp);
        Assert.Equal(1, removed.Count);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => fixture.Service.AddDirectAsync(
            fixture.DirectMessageEntity.Id, fixture.Owner.Id, Heart));
    }

    private sealed class Fixture(SqliteConnection connection, IridiumDbContext db) : IAsyncDisposable
    {
        public IridiumDbContext Db { get; } = db;
        public MessageReactionService Service { get; } = new(db, new CommunityAuthorizationService());
        public required NodeAccount Owner { get; init; }
        public required NodeAccount Alice { get; init; }
        public required NodeAccount Bob { get; init; }
        public required Community Community { get; init; }
        public required CommunityRole DefaultRole { get; init; }
        public required CommunityChannel Channel { get; init; }
        public required ChannelMessage Message { get; init; }
        public required DirectConversation DirectConversation { get; init; }
        public required DirectMessage DirectMessageEntity { get; init; }

        public static async Task<Fixture> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var db = new IridiumDbContext(new DbContextOptionsBuilder<IridiumDbContext>()
                .UseSqlite(connection).Options);
            await db.Database.EnsureCreatedAsync();
            var owner = Account("owner"); var alice = Account("alice"); var bob = Account("bob");
            db.Accounts.AddRange(owner, alice, bob);
            var community = new Community { Id = Guid.NewGuid(), Name = "Test Server", OwnerAccountId = owner.Id,
                OwnerAccount = owner, CreatedAt = DateTimeOffset.UtcNow };
            var role = new CommunityRole { Id = Guid.NewGuid(), CommunityId = community.Id,
                Community = community, Name = "@everyone", IsDefault = true,
                Permissions = CommunityPermission.ViewChannels | CommunityPermission.ReadMessageHistory |
                              CommunityPermission.AddReactions | CommunityPermission.UseExternalEmoji };
            var channel = new CommunityChannel { Id = Guid.NewGuid(), CommunityId = community.Id,
                Community = community, Name = "general", CreatedAt = DateTimeOffset.UtcNow };
            db.Communities.Add(community); db.CommunityRoles.Add(role); db.CommunityChannels.Add(channel);
            foreach (var account in new[] { owner, alice, bob }) db.CommunityMembers.Add(new CommunityMember
            { CommunityId = community.Id, Community = community, AccountId = account.Id, Account = account,
                JoinedAt = DateTimeOffset.UtcNow });
            var message = new ChannelMessage { Id = Guid.NewGuid(), CommunityId = community.Id,
                ChannelId = channel.Id, Channel = channel, AuthorAccountId = owner.Id, AuthorAccount = owner,
                Content = "hello", CreatedAt = DateTimeOffset.UtcNow };
            db.ChannelMessages.Add(message);
            var directConversation = new DirectConversation
            {
                Id = Guid.NewGuid(), ParticipantAAccountId = alice.Id, ParticipantAAccount = alice,
                ParticipantBAccountId = bob.Id, ParticipantBAccount = bob, CreatedAt = DateTimeOffset.UtcNow
            };
            var directMessage = new DirectMessage
            {
                Id = Guid.NewGuid(), ConversationId = directConversation.Id, Conversation = directConversation,
                AuthorAccountId = alice.Id, AuthorAccount = alice, Content = "hello privately",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.DirectConversations.Add(directConversation);
            db.DirectMessages.Add(directMessage);
            await db.SaveChangesAsync();
            return new(connection, db) { Owner = owner, Alice = alice, Bob = bob, Community = community,
                DefaultRole = role, Channel = channel, Message = message, DirectConversation = directConversation,
                DirectMessageEntity = directMessage };
        }

        public NodeAccount AddAccount(string name) { var value = Account(name); Db.Accounts.Add(value); return value; }
        public Community AddCommunity(string name, NodeAccount owner)
        {
            var value = new Community { Id = Guid.NewGuid(), Name = name, OwnerAccountId = owner.Id,
                OwnerAccount = owner, CreatedAt = DateTimeOffset.UtcNow };
            Db.Communities.Add(value); return value;
        }
        public void AddMember(Community community, NodeAccount account) => Db.CommunityMembers.Add(new()
        { CommunityId = community.Id, Community = community, AccountId = account.Id, Account = account,
            JoinedAt = DateTimeOffset.UtcNow });
        public CommunityEmoji AddEmoji(Community community, string name)
        {
            var value = new CommunityEmoji { Id = Guid.NewGuid(), CommunityId = community.Id,
                Community = community, Name = name, ObjectKey = Guid.NewGuid().ToString("N"), ContentType = "image/png",
                Width = 128, Height = 128, SizeBytes = 10, Revision = 1, CreatedAt = DateTimeOffset.UtcNow,
                CreatedByAccountId = Owner.Id };
            Db.CommunityEmojis.Add(value); return value;
        }
        public ChannelMessage AddMessage(string content)
        {
            var value = new ChannelMessage { Id = Guid.NewGuid(), CommunityId = Community.Id,
                ChannelId = Channel.Id, Channel = Channel, AuthorAccountId = Owner.Id, AuthorAccount = Owner,
                Content = content, CreatedAt = DateTimeOffset.UtcNow.AddMilliseconds(1) };
            Db.ChannelMessages.Add(value); return value;
        }
        public ChannelMessageDto Dto(ChannelMessage? message = null)
        {
            var value = message ?? Message;
            return new(value.Id, value.CommunityId, value.ChannelId,
                new(value.AuthorAccountId, value.AuthorAccount.Username, value.AuthorAccount.DisplayName),
                value.Content, value.CreatedAt, null, false, null);
        }
        public DirectMessageDto DirectDto() => new(DirectMessageEntity.Id, DirectConversation.Id,
            new(DirectMessageEntity.AuthorAccountId, DirectMessageEntity.AuthorAccount.Username,
                DirectMessageEntity.AuthorAccount.DisplayName), DirectMessageEntity.Content, DirectMessageEntity.CreatedAt, null,
            false, null);
        private static NodeAccount Account(string name) => new() { Id = Guid.NewGuid(), Username = name,
            DisplayName = char.ToUpperInvariant(name[0]) + name[1..], PasswordHash = "hash",
            CreatedAt = DateTimeOffset.UtcNow };
        public async ValueTask DisposeAsync() { await Db.DisposeAsync(); await connection.DisposeAsync(); }
    }
}
