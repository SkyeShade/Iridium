using Iridium.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Persistence;

public sealed class IridiumDbContext(DbContextOptions<IridiumDbContext> options) : DbContext(options)
{
    public DbSet<NodeAccount> Accounts => Set<NodeAccount>();
    public DbSet<Community> Communities => Set<Community>();
    public DbSet<CommunityMember> CommunityMembers => Set<CommunityMember>();
    public DbSet<CommunityRole> CommunityRoles => Set<CommunityRole>();
    public DbSet<CommunityMemberRole> CommunityMemberRoles => Set<CommunityMemberRole>();
    public DbSet<CommunityInvite> CommunityInvites => Set<CommunityInvite>();
    public DbSet<CommunityBan> CommunityBans => Set<CommunityBan>();
    public DbSet<AccountSession> AccountSessions => Set<AccountSession>();
    public DbSet<Friendship> Friendships => Set<Friendship>();
    public DbSet<AccountBlock> AccountBlocks => Set<AccountBlock>();
    public DbSet<CommunityCategory> CommunityCategories => Set<CommunityCategory>();
    public DbSet<CommunityChannel> CommunityChannels => Set<CommunityChannel>();
    public DbSet<ChannelMessage> ChannelMessages => Set<ChannelMessage>();
    public DbSet<CommunityChannelReadState> CommunityChannelReadStates => Set<CommunityChannelReadState>();
    public DbSet<CommunityMentionNotification> CommunityMentionNotifications => Set<CommunityMentionNotification>();
    public DbSet<DirectConversation> DirectConversations => Set<DirectConversation>();
    public DbSet<DirectConversationState> DirectConversationStates => Set<DirectConversationState>();
    public DbSet<DirectMessage> DirectMessages => Set<DirectMessage>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<AccountAvatarPreset> AccountAvatarPresets => Set<AccountAvatarPreset>();
    public DbSet<AccountBannerPreset> AccountBannerPresets => Set<AccountBannerPreset>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var account = modelBuilder.Entity<NodeAccount>();
        account.HasKey(value => value.Id);
        account.Property(value => value.Username).HasMaxLength(32).UseCollation("NOCASE");
        account.Property(value => value.DisplayName).HasMaxLength(64);
        account.Property(value => value.Pronouns).HasMaxLength(64);
        account.Property(value => value.Description).HasMaxLength(400);
        account.Property(value => value.PreferredPresence).HasDefaultValue(Iridium.Protocol.UserPresence.Online);
        account.HasIndex(value => value.Username).IsUnique();

        var avatarPreset = modelBuilder.Entity<AccountAvatarPreset>();
        avatarPreset.HasKey(value => value.Id);
        avatarPreset.Property(value => value.OriginalObjectKey).HasMaxLength(64);
        avatarPreset.Property(value => value.ProcessedObjectKey).HasMaxLength(64);
        avatarPreset.Property(value => value.ContentType).HasMaxLength(64);
        avatarPreset.Property(value => value.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        avatarPreset.Property(value => value.UpdatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        avatarPreset.HasIndex(value => new { value.AccountId, value.SlotIndex }).IsUnique();
        avatarPreset.HasOne(value => value.Account).WithMany(value => value.AvatarPresets)
            .HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Cascade);

        var bannerPreset = modelBuilder.Entity<AccountBannerPreset>();
        bannerPreset.HasKey(value => value.Id);
        bannerPreset.Property(value => value.OriginalObjectKey).HasMaxLength(64);
        bannerPreset.Property(value => value.ProcessedObjectKey).HasMaxLength(64);
        bannerPreset.Property(value => value.ContentType).HasMaxLength(64);
        bannerPreset.Property(value => value.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        bannerPreset.Property(value => value.UpdatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        bannerPreset.HasIndex(value => new { value.AccountId, value.SlotIndex }).IsUnique();
        bannerPreset.HasOne(value => value.Account).WithMany(value => value.BannerPresets)
            .HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Cascade);

        var community = modelBuilder.Entity<Community>();
        community.HasKey(value => value.Id);
        community.Property(value => value.Name).HasMaxLength(100);
        community.Property(value => value.Description).HasMaxLength(500);
        community.HasOne(value => value.OwnerAccount)
            .WithMany(value => value.OwnedCommunities)
            .HasForeignKey(value => value.OwnerAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        var member = modelBuilder.Entity<CommunityMember>();
        member.HasKey(value => new { value.CommunityId, value.AccountId });
        member.Property(value => value.Nickname).HasMaxLength(64);
        member.HasOne(value => value.Community).WithMany(value => value.Members).HasForeignKey(value => value.CommunityId);
        member.HasOne(value => value.Account).WithMany(value => value.CommunityMemberships).HasForeignKey(value => value.AccountId);

        var role = modelBuilder.Entity<CommunityRole>();
        role.HasKey(value => new { value.CommunityId, value.Id });
        role.Property(value => value.Name).HasMaxLength(64);
        role.Property(value => value.Color).HasMaxLength(7);
        role.Property(value => value.Position).HasDefaultValue(0);
        role.Property(value => value.IsDefault).HasDefaultValue(false);
        role.Property(value => value.DisplaySeparately).HasDefaultValue(false);
        role.Property(value => value.IsMentionable).HasDefaultValue(false);
        role.HasIndex(value => new { value.CommunityId, value.Name }).IsUnique();
        role.HasIndex(value => new { value.CommunityId, value.Position });
        role.HasIndex(value => value.CommunityId).HasFilter("IsDefault = 1").IsUnique();
        role.HasOne(value => value.Community).WithMany(value => value.Roles).HasForeignKey(value => value.CommunityId);

        var memberRole = modelBuilder.Entity<CommunityMemberRole>();
        memberRole.HasKey(value => new { value.CommunityId, value.AccountId, value.RoleId });
        memberRole.HasOne(value => value.Member).WithMany(value => value.Roles)
            .HasForeignKey(value => new { value.CommunityId, value.AccountId });
        memberRole.HasOne(value => value.Role).WithMany(value => value.Members)
            .HasForeignKey(value => new { value.CommunityId, value.RoleId });

        var invite = modelBuilder.Entity<CommunityInvite>();
        invite.HasKey(value => value.Id);
        invite.Property(value => value.TokenHash).HasMaxLength(64);
        invite.Property(value => value.CodePrefix).HasMaxLength(12);
        invite.HasIndex(value => value.TokenHash).IsUnique();
        invite.HasIndex(value => new { value.CommunityId, value.Revoked });
        invite.HasOne(value => value.Community).WithMany(value => value.Invites)
            .HasForeignKey(value => value.CommunityId).OnDelete(DeleteBehavior.Cascade);
        invite.HasOne(value => value.CreatedByAccount).WithMany()
            .HasForeignKey(value => value.CreatedByAccountId).OnDelete(DeleteBehavior.Restrict);

        var ban = modelBuilder.Entity<CommunityBan>();
        ban.HasKey(value => new { value.CommunityId, value.AccountId });
        ban.Property(value => value.Reason).HasMaxLength(500);
        ban.HasOne(value => value.Community).WithMany(value => value.Bans)
            .HasForeignKey(value => value.CommunityId).OnDelete(DeleteBehavior.Cascade);
        ban.HasOne(value => value.Account).WithMany()
            .HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Restrict);
        ban.HasOne(value => value.BannedByAccount).WithMany()
            .HasForeignKey(value => value.BannedByAccountId).OnDelete(DeleteBehavior.Restrict);

        var session = modelBuilder.Entity<AccountSession>();
        session.HasKey(value => value.Id);
        session.HasIndex(value => value.TokenHash).IsUnique();
        session.HasOne(value => value.Account).WithMany().HasForeignKey(value => value.AccountId);

        var friendship = modelBuilder.Entity<Friendship>();
        friendship.HasKey(value => value.Id);
        friendship.HasIndex(value => new { value.RequesterAccountId, value.AddresseeAccountId }).IsUnique();
        friendship.HasOne(value => value.RequesterAccount).WithMany().HasForeignKey(value => value.RequesterAccountId)
            .OnDelete(DeleteBehavior.Restrict);
        friendship.HasOne(value => value.AddresseeAccount).WithMany().HasForeignKey(value => value.AddresseeAccountId)
            .OnDelete(DeleteBehavior.Restrict);

        var block = modelBuilder.Entity<AccountBlock>();
        block.HasKey(value => new { value.BlockingAccountId, value.BlockedAccountId });
        block.Property(value => value.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        block.HasOne(value => value.BlockingAccount).WithMany().HasForeignKey(value => value.BlockingAccountId)
            .OnDelete(DeleteBehavior.Cascade);
        block.HasOne(value => value.BlockedAccount).WithMany().HasForeignKey(value => value.BlockedAccountId)
            .OnDelete(DeleteBehavior.Cascade);

        var category = modelBuilder.Entity<CommunityCategory>();
        category.HasKey(value => new { value.CommunityId, value.Id });
        category.Property(value => value.Name).HasMaxLength(100);
        category.HasIndex(value => new { value.CommunityId, value.ParentCategoryId, value.Position });
        category.HasOne(value => value.Community).WithMany(value => value.Categories).HasForeignKey(value => value.CommunityId);
        category.HasOne(value => value.ParentCategory).WithMany(value => value.ChildCategories)
            .HasForeignKey(value => new { value.CommunityId, value.ParentCategoryId })
            .HasPrincipalKey(value => new { value.CommunityId, value.Id })
            .OnDelete(DeleteBehavior.Restrict);

        var channel = modelBuilder.Entity<CommunityChannel>();
        channel.HasKey(value => new { value.CommunityId, value.Id });
        channel.Property(value => value.Name).HasMaxLength(100);
        channel.Property(value => value.Kind).HasDefaultValue(Iridium.Protocol.CommunityChannelKind.Text);
        channel.HasIndex(value => new { value.CommunityId, value.CategoryId, value.Position });
        channel.HasOne(value => value.Community).WithMany(value => value.Channels).HasForeignKey(value => value.CommunityId);
        channel.HasOne(value => value.Category).WithMany(value => value.Channels)
            .HasForeignKey(value => new { value.CommunityId, value.CategoryId })
            .HasPrincipalKey(value => new { value.CommunityId, value.Id })
            .OnDelete(DeleteBehavior.Restrict);

        var channelRead = modelBuilder.Entity<CommunityChannelReadState>();
        channelRead.HasKey(value => new { value.CommunityId, value.ChannelId, value.AccountId });
        channelRead.Property(value => value.LastReadAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        channelRead.HasIndex(value => new { value.AccountId, value.CommunityId });
        channelRead.HasOne(value => value.Channel).WithMany(value => value.ReadStates)
            .HasForeignKey(value => new { value.CommunityId, value.ChannelId })
            .HasPrincipalKey(value => new { value.CommunityId, value.Id }).OnDelete(DeleteBehavior.Cascade);
        channelRead.HasOne(value => value.Account).WithMany()
            .HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Cascade);

        var message = modelBuilder.Entity<ChannelMessage>();
        message.HasKey(value => value.Id);
        message.Property(value => value.Content);
        message.Property(value => value.MentionsJson).HasMaxLength(8000);
        message.Property(value => value.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        message.Property(value => value.EditedAt)
            .HasConversion(
                value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        message.Property(value => value.DeletedAt)
            .HasConversion(
                value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        message.HasIndex(value => new { value.CommunityId, value.ChannelId, value.CreatedAt, value.Id });
        message.HasIndex(value => new { value.AuthorAccountId, value.CommunityId, value.ChannelId, value.ClientMessageId })
            .IsUnique().HasFilter("ClientMessageId IS NOT NULL");
        message.HasOne(value => value.Channel).WithMany(value => value.Messages)
            .HasForeignKey(value => new { value.CommunityId, value.ChannelId })
            .HasPrincipalKey(value => new { value.CommunityId, value.Id })
            .OnDelete(DeleteBehavior.Cascade);
        message.HasOne(value => value.AuthorAccount).WithMany(value => value.Messages)
            .HasForeignKey(value => value.AuthorAccountId)
            .OnDelete(DeleteBehavior.Restrict);
        message.HasOne(value => value.ReplyToMessage).WithMany(value => value.Replies)
            .HasForeignKey(value => value.ReplyToMessageId)
            .OnDelete(DeleteBehavior.SetNull);

        var mentionNotification = modelBuilder.Entity<CommunityMentionNotification>();
        mentionNotification.HasKey(value => new { value.MessageId, value.AccountId });
        mentionNotification.Property(value => value.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        mentionNotification.Property(value => value.ReadAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        mentionNotification.HasIndex(value => new { value.AccountId, value.CommunityId, value.ChannelId, value.ReadAt });
        mentionNotification.HasOne(value => value.Message).WithMany()
            .HasForeignKey(value => value.MessageId).OnDelete(DeleteBehavior.Cascade);
        mentionNotification.HasOne(value => value.Account).WithMany()
            .HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Cascade);

        var directConversation = modelBuilder.Entity<DirectConversation>();
        directConversation.HasKey(value => value.Id);
        directConversation.Property(value => value.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        directConversation.HasIndex(value => new { value.ParticipantAAccountId, value.ParticipantBAccountId }).IsUnique();
        directConversation.HasOne(value => value.ParticipantAAccount).WithMany()
            .HasForeignKey(value => value.ParticipantAAccountId).OnDelete(DeleteBehavior.Restrict);
        directConversation.HasOne(value => value.ParticipantBAccount).WithMany()
            .HasForeignKey(value => value.ParticipantBAccountId).OnDelete(DeleteBehavior.Restrict);

        var directState = modelBuilder.Entity<DirectConversationState>();
        directState.HasKey(value => new { value.ConversationId, value.AccountId });
        directState.Property(value => value.HiddenAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        directState.Property(value => value.LastReadAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        directState.HasOne(value => value.Conversation).WithMany(value => value.ParticipantStates)
            .HasForeignKey(value => value.ConversationId).OnDelete(DeleteBehavior.Cascade);
        directState.HasOne(value => value.Account).WithMany()
            .HasForeignKey(value => value.AccountId).OnDelete(DeleteBehavior.Cascade);

        var directMessage = modelBuilder.Entity<DirectMessage>();
        directMessage.HasKey(value => value.Id);
        directMessage.Property(value => value.Content);
        directMessage.Property(value => value.Kind).HasDefaultValue(Iridium.Protocol.MessageKind.User);
        directMessage.Property(value => value.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        directMessage.Property(value => value.EditedAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        directMessage.Property(value => value.DeletedAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        directMessage.HasIndex(value => new { value.ConversationId, value.CreatedAt, value.Id });
        directMessage.HasIndex(value => new { value.AuthorAccountId, value.ConversationId, value.ClientMessageId })
            .IsUnique().HasFilter("ClientMessageId IS NOT NULL");
        directMessage.HasIndex(value => new { value.RelatedCallId, value.Kind }).IsUnique()
            .HasFilter("RelatedCallId IS NOT NULL");
        directMessage.HasOne(value => value.Conversation).WithMany(value => value.Messages)
            .HasForeignKey(value => value.ConversationId).OnDelete(DeleteBehavior.Cascade);
        directMessage.HasOne(value => value.AuthorAccount).WithMany()
            .HasForeignKey(value => value.AuthorAccountId).OnDelete(DeleteBehavior.Restrict);
        directMessage.HasOne(value => value.ReplyToMessage).WithMany(value => value.Replies)
            .HasForeignKey(value => value.ReplyToMessageId).OnDelete(DeleteBehavior.SetNull);

        var attachment = modelBuilder.Entity<Attachment>();
        attachment.HasKey(value => value.Id);
        attachment.Property(value => value.OriginalFileName).HasMaxLength(255);
        attachment.Property(value => value.OriginalObjectKey).HasColumnName("StoredObjectKey").HasMaxLength(64);
        attachment.Property(value => value.PreviewObjectKey).HasMaxLength(64);
        attachment.Property(value => value.OriginalContentType).HasColumnName("ContentType").HasMaxLength(255);
        attachment.Property(value => value.PreviewContentType).HasMaxLength(255);
        attachment.Property(value => value.OriginalSizeBytes).HasColumnName("SizeBytes");
        attachment.Property(value => value.IsSpoiler).HasDefaultValue(false);
        attachment.Property(value => value.AverageColor).HasMaxLength(7);
        attachment.Property(value => value.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        attachment.HasIndex(value => value.OriginalObjectKey).IsUnique()
            .HasDatabaseName("IX_Attachments_StoredObjectKey");
        attachment.HasIndex(value => value.PreviewObjectKey).IsUnique()
            .HasFilter("PreviewObjectKey IS NOT NULL");
        attachment.HasIndex(value => value.ChannelMessageId);
        attachment.HasIndex(value => value.DirectMessageId);
        attachment.HasOne(value => value.UploaderAccount).WithMany()
            .HasForeignKey(value => value.UploaderAccountId).OnDelete(DeleteBehavior.Restrict);
        attachment.HasOne(value => value.ChannelMessage).WithMany(value => value.Attachments)
            .HasForeignKey(value => value.ChannelMessageId).OnDelete(DeleteBehavior.Cascade);
        attachment.HasOne(value => value.DirectMessage).WithMany(value => value.Attachments)
            .HasForeignKey(value => value.DirectMessageId).OnDelete(DeleteBehavior.Cascade);
    }
}
