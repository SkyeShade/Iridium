using System.Text.Json;
using Iridium.Protocol;
using Iridium.Server.Api;
using Iridium.Server.Domain;
using Iridium.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class HistoricalMessageSnapshotTests
{
    [Fact]
    public void MapperUsesHistoricalSnapshotsButLegacyRowsUseCurrentDefaultAccountProfile()
    {
        var account = Account("Current Name");
        var original = Message(account, "old", "Aria", "snapshot-object");
        var reply = Message(account, "reply", "GM Skye", "new-object");
        reply.ReplyToMessageId = original.Id;
        reply.ReplyToMessage = original;

        var mapped = ChannelMessageMapper.ToDto(reply);
        Assert.Equal("GM Skye", mapped.Author.DisplayName);
        Assert.True(mapped.Author.HasHistoricalSnapshot);
        Assert.Equal(reply.Id, mapped.Author.AvatarSnapshotMessageId);
        Assert.Equal("Aria", mapped.ReplyTo?.AuthorDisplayName);
        Assert.True(mapped.ReplyTo?.HasHistoricalSnapshot);
        Assert.Equal(original.Id, mapped.ReplyTo?.AvatarSnapshotMessageId);

        var legacy = ChannelMessageMapper.ToDto(Message(account, "legacy"));
        Assert.Equal("Current Name", legacy.Author.DisplayName);
        Assert.Equal(account.AvatarRevision, legacy.Author.AvatarRevision);
        Assert.False(legacy.Author.HasHistoricalSnapshot);
        Assert.Null(legacy.Author.AvatarSnapshotMessageId);

        var legacyOriginal = Message(account, "legacy original");
        var legacyReply = Message(account, "legacy reply");
        legacyReply.ReplyToMessageId = legacyOriginal.Id;
        legacyReply.ReplyToMessage = legacyOriginal;
        var mappedLegacyReply = ChannelMessageMapper.ToDto(legacyReply);
        Assert.Equal("Current Name", mappedLegacyReply.ReplyTo?.AuthorDisplayName);
        Assert.Equal(account.AvatarRevision, mappedLegacyReply.ReplyTo?.AvatarRevision);
        Assert.False(mappedLegacyReply.ReplyTo?.HasHistoricalSnapshot);

        account.DisplayName = "Changed Default";
        account.AvatarRevision = 17;
        var remappedLegacy = ChannelMessageMapper.ToDto(Message(account, "legacy after profile change"));
        Assert.Equal("Changed Default", remappedLegacy.Author.DisplayName);
        Assert.Equal(17, remappedLegacy.Author.AvatarRevision);

        var remappedSnapshot = ChannelMessageMapper.ToDto(reply);
        Assert.Equal("GM Skye", remappedSnapshot.Author.DisplayName);
        Assert.Equal(42, remappedSnapshot.Author.AvatarRevision);
    }

    [Fact]
    public void SnapshotFieldsRoundTripThroughMessageDtoJson()
    {
        var message = ChannelMessageMapper.ToDto(Message(Account("Current"), "cached", "Aria", "object-key"));
        var roundTrip = JsonSerializer.Deserialize<ChannelMessageDto>(JsonSerializer.Serialize(message));
        Assert.NotNull(roundTrip);
        Assert.Equal("Aria", roundTrip.Author.DisplayName);
        Assert.True(roundTrip.Author.HasHistoricalSnapshot);
        Assert.Equal(message.Id, roundTrip.Author.AvatarSnapshotMessageId);
    }

    [Fact]
    public void ClientRenderingAndSearchDoNotOverlayCurrentCommunityAvatarOnLegacyMessages()
    {
        var root = FindWorkspaceRoot();
        var list = File.ReadAllText(Path.Combine(root, "Iridium.Web", "Components", "MessageList.razor"));
        var search = File.ReadAllText(Path.Combine(root, "Iridium.Server", "Api", "MessageEndpoints.cs"));
        var cache = File.ReadAllText(Path.Combine(root, "Iridium.Web", "wwwroot", "js", "messageHistoryCache.js"));

        Assert.Contains("=> MessageTimeline.Visible(Messages);", list);
        Assert.DoesNotContain("author.DisplayName", list);
        Assert.DoesNotContain("ResolveSearchProfilesAsync", search);
        Assert.Contains("const schemaVersion = 3;", cache);
        Assert.Contains("event.oldVersion > 0 && event.oldVersion < 2", cache);
    }

    [Fact]
    public async Task LegacyChannelMessageSchemaGainsNullableSnapshotColumns()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE ChannelMessages (
                Id TEXT NOT NULL PRIMARY KEY,
                CommunityId TEXT NOT NULL,
                ChannelId TEXT NOT NULL,
                AuthorAccountId TEXT NOT NULL,
                Content TEXT NOT NULL,
                CreatedAt INTEGER NOT NULL,
                EditedAt INTEGER NULL,
                IsDeleted INTEGER NOT NULL DEFAULT 0,
                DeletedAt INTEGER NULL,
                ReplyToMessageId TEXT NULL,
                MentionsJson TEXT NULL
            );
            """);

        await DatabaseCompatibility.EnsureChannelMessagesTableAsync(db);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info('ChannelMessages');";
        await using var reader = await command.ExecuteReaderAsync();
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
        Assert.Contains("AuthorDisplayNameSnapshot", columns);
        Assert.Contains("AuthorAvatarObjectKeySnapshot", columns);
        Assert.Contains("AuthorAvatarRevisionSnapshot", columns);
    }

    private static NodeAccount Account(string displayName) => new()
    {
        Id = Guid.NewGuid(), Username = "author", DisplayName = displayName,
        AvatarRevision = 6, PasswordHash = "hash", CreatedAt = DateTimeOffset.UtcNow
    };

    private static ChannelMessage Message(NodeAccount account, string content,
        string? snapshotName = null, string? snapshotObjectKey = null) => new()
    {
        Id = Guid.NewGuid(), CommunityId = Guid.NewGuid(), ChannelId = Guid.NewGuid(),
        AuthorAccountId = account.Id, AuthorAccount = account, Channel = null!, Content = content,
        CreatedAt = DateTimeOffset.UtcNow, AuthorDisplayNameSnapshot = snapshotName,
        AuthorAvatarObjectKeySnapshot = snapshotObjectKey, AuthorAvatarContentTypeSnapshot = "image/webp",
        AuthorAvatarWidthSnapshot = 128, AuthorAvatarHeightSnapshot = 128,
        AuthorAvatarCropXSnapshot = .1, AuthorAvatarCropYSnapshot = -.1,
        AuthorAvatarZoomSnapshot = 1.25,
        AuthorAvatarRevisionSnapshot = snapshotName is not null || snapshotObjectKey is not null ? 42 : null
    };

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Iridium.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate Iridium.sln.");
    }
}
