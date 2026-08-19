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
    public DbSet<CommunityCategory> CommunityCategories => Set<CommunityCategory>();
    public DbSet<CommunityChannel> CommunityChannels => Set<CommunityChannel>();
    public DbSet<ChannelMessage> ChannelMessages => Set<ChannelMessage>();
    public DbSet<DirectConversation> DirectConversations => Set<DirectConversation>();
    public DbSet<DirectConversationState> DirectConversationStates => Set<DirectConversationState>();
    public DbSet<DirectMessage> DirectMessages => Set<DirectMessage>();

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

        var category = modelBuilder.Entity<CommunityCategory>();
        category.HasKey(value => new { value.CommunityId, value.Id });
        category.Property(value => value.Name).HasMaxLength(100);
        category.HasIndex(value => new { value.CommunityId, value.Position });
        category.HasOne(value => value.Community).WithMany(value => value.Categories).HasForeignKey(value => value.CommunityId);

        var channel = modelBuilder.Entity<CommunityChannel>();
        channel.HasKey(value => new { value.CommunityId, value.Id });
        channel.Property(value => value.Name).HasMaxLength(100);
        channel.HasIndex(value => new { value.CommunityId, value.CategoryId, value.Position });
        channel.HasOne(value => value.Community).WithMany(value => value.Channels).HasForeignKey(value => value.CommunityId);
        channel.HasOne(value => value.Category).WithMany(value => value.Channels)
            .HasForeignKey(value => new { value.CommunityId, value.CategoryId })
            .HasPrincipalKey(value => new { value.CommunityId, value.Id })
            .OnDelete(DeleteBehavior.Restrict);

        var message = modelBuilder.Entity<ChannelMessage>();
        message.HasKey(value => value.Id);
        message.Property(value => value.Content).HasMaxLength(4000);
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
        message.HasIndex(value => new { value.CommunityId, value.ChannelId, value.CreatedAt });
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
        directMessage.Property(value => value.Content).HasMaxLength(4000);
        directMessage.Property(value => value.CreatedAt)
            .HasConversion(value => value.UtcTicks, value => new DateTimeOffset(value, TimeSpan.Zero));
        directMessage.Property(value => value.EditedAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        directMessage.Property(value => value.DeletedAt)
            .HasConversion(value => value.HasValue ? value.Value.UtcTicks : (long?)null,
                value => value.HasValue ? new DateTimeOffset(value.Value, TimeSpan.Zero) : null);
        directMessage.HasIndex(value => new { value.ConversationId, value.CreatedAt });
        directMessage.HasOne(value => value.Conversation).WithMany(value => value.Messages)
            .HasForeignKey(value => value.ConversationId).OnDelete(DeleteBehavior.Cascade);
        directMessage.HasOne(value => value.AuthorAccount).WithMany()
            .HasForeignKey(value => value.AuthorAccountId).OnDelete(DeleteBehavior.Restrict);
        directMessage.HasOne(value => value.ReplyToMessage).WithMany(value => value.Replies)
            .HasForeignKey(value => value.ReplyToMessageId).OnDelete(DeleteBehavior.SetNull);
    }
}
