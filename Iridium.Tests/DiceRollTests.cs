using System.Runtime.CompilerServices;
using System.Text.Json;
using Iridium.Protocol;
using Iridium.Server.Api;
using Iridium.Server.Domain;
using Iridium.Server.Messages;
using Iridium.Server.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class DiceRollTests
{
    private static readonly string Root = FindRoot();

    [Theory]
    [InlineData("/roll 1d20", 1, 20)]
    [InlineData("/roll 20d20", 20, 20)]
    [InlineData("/roll 2D6", 2, 6)]
    [InlineData("  /ROLL   3d8  ", 3, 8)]
    [InlineData("/roll 100d1000000", 100, 1_000_000)]
    public void ParserAcceptsOnlyTheV1DiceNotation(string content, int diceCount, int sides)
    {
        var parsed = DiceRollCommandParser.Parse(content);

        Assert.True(parsed.IsCommand);
        Assert.True(parsed.IsValid);
        Assert.Equal(new DiceRollRequest(diceCount, sides), parsed.Request);
    }

    [Theory]
    [InlineData("/roll")]
    [InlineData("/roll d20")]
    [InlineData("/roll 2d")]
    [InlineData("/roll -1d20")]
    [InlineData("/roll 1.5d20")]
    [InlineData("/roll foo")]
    [InlineData("/roll 2d6 extra")]
    public void ParserRejectsMalformedCommandsWithoutTreatingThemAsMessages(string content)
    {
        var parsed = DiceRollCommandParser.Parse(content);

        Assert.True(parsed.IsCommand);
        Assert.False(parsed.IsValid);
        Assert.Equal(DiceRollCommandParser.UsageMessage, parsed.Error);
    }

    [Theory]
    [InlineData("/roll 0d20", "Roll at least one die.")]
    [InlineData("/roll 1d1", "Dice must have at least 2 sides.")]
    [InlineData("/roll 101d20", "You can roll at most 100 dice at once.")]
    [InlineData("/roll 1d1000001", "Dice may have at most 1,000,000 sides.")]
    public void ParserReturnsSpecificLimitErrors(string content, string error)
    {
        var parsed = DiceRollCommandParser.Parse(content);

        Assert.True(parsed.IsCommand);
        Assert.False(parsed.IsValid);
        Assert.Equal(error, parsed.Error);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("/roller 2d6")]
    [InlineData("text /roll 2d6")]
    public void ParserLeavesNormalMessageParsingAlone(string content) =>
        Assert.False(DiceRollCommandParser.Parse(content).IsCommand);

    [Fact]
    public void ServiceUsesInjectedRandomnessAndComputesCountRangeTotalAndExtremes()
    {
        var result = new DiceRollService(new SequenceRandomSource(4, 20, 11, 2, 17))
            .Roll(new(5, 20));

        Assert.Equal([4, 20, 11, 2, 17], result.Rolls);
        Assert.Equal(5, result.DiceCount);
        Assert.Equal(20, result.Sides);
        Assert.Equal(2, result.Minimum);
        Assert.Equal(20, result.Maximum);
        Assert.Equal(54, result.Total);
        Assert.All(result.Rolls, value => Assert.InRange(value, 1, result.Sides));
    }

    [Fact]
    public void OneDieAndAllEqualRollsHaveOneUnambiguousExtremeState()
    {
        var one = new DiceRollService(new SequenceRandomSource(17)).Roll(new(1, 20));
        var tied = new DiceRollService(new SequenceRandomSource(1, 1, 1)).Roll(new(3, 2));

        Assert.Equal(one.Minimum, one.Maximum);
        Assert.Equal(17, one.Total);
        Assert.Equal(tied.Minimum, tied.Maximum);
        Assert.Equal(3, tied.Total);
        Assert.All(tied.Rolls, value => Assert.Equal(tied.Minimum, value));
    }

    [Fact]
    public async Task StructuredRollMetadataSurvivesDatabaseReloadWithoutRerolling()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        var now = DateTimeOffset.UtcNow;
        var author = Account("roller", "Roller", now);
        var recipient = Account("witness", "Witness", now);
        var conversation = new DirectConversation
        {
            Id = Guid.NewGuid(), ParticipantAAccountId = author.Id, ParticipantBAccountId = recipient.Id,
            ParticipantAAccount = author, ParticipantBAccount = recipient, CreatedAt = now
        };
        var original = new DiceRollResultDto(2, 20, [4, 19], 4, 19, 23);
        var message = new DirectMessage
        {
            Id = Guid.NewGuid(), ConversationId = conversation.Id, Conversation = conversation,
            AuthorAccountId = author.Id, AuthorAccount = author, Kind = MessageKind.DiceRoll,
            DiceRollJson = JsonSerializer.Serialize(original), Content = DiceRollService.SearchableContent(original),
            CreatedAt = now
        };

        await using (var write = new IridiumDbContext(options))
        {
            await write.Database.EnsureCreatedAsync();
            write.AddRange(author, recipient, conversation, message);
            await write.SaveChangesAsync();
        }
        await using var read = new IridiumDbContext(options);
        var reloaded = await read.DirectMessages.Include(value => value.AuthorAccount)
            .SingleAsync(value => value.Id == message.Id);
        var dto = DirectMessageMapper.ToDto(reloaded);

        Assert.Equal(MessageKind.DiceRoll, dto.Kind);
        Assert.Equal(original.DiceCount, dto.DiceRoll!.DiceCount);
        Assert.Equal(original.Sides, dto.DiceRoll.Sides);
        Assert.Equal(original.Minimum, dto.DiceRoll.Minimum);
        Assert.Equal(original.Maximum, dto.DiceRoll.Maximum);
        Assert.Equal(original.Total, dto.DiceRoll.Total);
        Assert.Equal([4, 19], dto.DiceRoll!.Rolls);
        Assert.Contains("Rolled 2d20", dto.Content);
        Assert.DoesNotContain("/roll", dto.Content);
    }

    [Fact]
    public void UiRendersStructuredExtremesTotalWrappingAndNoRawCommandFallback()
    {
        var roll = Source("Iridium.Web", "Components", "DiceRollMessage.razor");
        var styles = Source("Iridium.Web", "Components", "DiceRollMessage.razor.css");
        var row = Source("Iridium.Web", "Components", "MessageRow.razor");

        Assert.Contains("value == Result.Minimum || value == Result.Maximum", roll);
        Assert.Contains("class=\"dice-roll-value @(IsExtreme(value) ? \"extreme\" : null)\"", roll);
        Assert.Contains("Total: <strong>@Result.Total</strong>", roll);
        Assert.Contains("flex-wrap:wrap", styles);
        Assert.Contains("max-width:100%", styles);
        Assert.Contains("overflow:hidden", styles);
        Assert.Contains("Message.Kind == MessageKind.DiceRoll && Message.DiceRoll", row);
        Assert.Contains("<DiceRollMessage Result=\"diceRoll\" />", row);
    }

    [Fact]
    public void ComposerValidatesBeforeClearingAndUsesNonOptimisticAuthoritativeSendPaths()
    {
        var composer = Source("Iridium.Web", "Components", "MessageComposer.razor");
        var channel = Source("Iridium.Web", "Components", "ChannelView.razor");
        var direct = Source("Iridium.Web", "Components", "DirectMessageView.razor");
        var session = Source("Iridium.Client.Core", "ChannelMessagingSession.cs");

        var submit = Slice(composer, "public async Task SubmitFromKeyboardAsync()", "private async Task CompleteOptimisticSubmissionCleanupAsync");
        Assert.True(submit.IndexOf("DiceRollCommandParser.Parse", StringComparison.Ordinal) <
                    submit.IndexOf("clearComposer", StringComparison.Ordinal));
        Assert.Contains("parsedRoll.IsCommand && !parsedRoll.IsValid", submit);
        Assert.Contains("var exclusiveSubmission = !OptimisticSubmission || parsedRoll.IsCommand", submit);
        Assert.Contains("Roll Dice", composer);
        Assert.Contains("Roll dice using XdY notation", composer);
        Assert.Contains("Example: /roll 2d20", composer);
        Assert.Contains("SupportsSlashCommands=\"false\"", Source("Iridium.Web", "Components", "ForumChannelView.razor"));
        Assert.Contains("Messaging.SendDiceRollAsync", channel);
        Assert.Contains("Messaging.SendDirectDiceRollAsync", direct);
        Assert.Contains("InvokeAsync<ChannelMessageDto>", Slice(session, "public async Task SendDiceRollAsync", "public async Task<ForwardMessagesResultDto>"));
        Assert.Contains("InvokeAsync<DirectMessageDto>", Slice(session, "public async Task SendDirectDiceRollAsync", "private OutgoingOperation BeginDirectMessage"));
    }

    [Fact]
    public void RollMessagesAreImmutableDeletableReplyableReactiveAndForwardedAsSnapshots()
    {
        var hub = Source("Iridium.Server", "Hubs", "ChatHub.cs");
        var list = Source("Iridium.Web", "Components", "MessageList.razor");
        var forwarded = Source("Iridium.Web", "Components", "ForwardedMessageBlock.razor");

        Assert.Contains("if (message.Kind != MessageKind.User) throw new HubException(\"Dice rolls cannot be edited.\")", hub);
        Assert.Contains("MessageKind.User or MessageKind.DiceRoll", list);
        Assert.Contains("MessageKind.User or MessageKind.DiceRoll", hub);
        Assert.Contains("Snapshot.Kind == MessageKind.DiceRoll && Snapshot.DiceRoll", forwarded);
        Assert.Contains("CanAddReactions=\"CanAddReactions\"", list);
        Assert.Contains("Kind = source.Kind", hub);
        Assert.Contains("DiceRollJson = source.DiceRollJson", hub);
        Assert.Contains("DiceRoll: message.DiceRoll", Source("Iridium.Web", "Components", "DirectMessageView.razor"));
    }

    [Fact]
    public async Task CompatibilityUpgradeAddsStructuredRollColumnsWithoutRebuildingMessages()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE ChannelMessages (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE DirectMessages (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE ForwardedMessageSnapshots (Id TEXT NOT NULL PRIMARY KEY);
                """;
            await command.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);

        await DatabaseCompatibility.EnsureDiceRollSchemaAsync(db);

        Assert.Contains("Kind", await ColumnsAsync(connection, "ChannelMessages"));
        Assert.Contains("DiceRollJson", await ColumnsAsync(connection, "ChannelMessages"));
        Assert.Contains("DiceRollJson", await ColumnsAsync(connection, "DirectMessages"));
        Assert.Contains("Kind", await ColumnsAsync(connection, "ForwardedMessageSnapshots"));
        Assert.Contains("DiceRollJson", await ColumnsAsync(connection, "ForwardedMessageSnapshots"));
    }

    private static NodeAccount Account(string username, string displayName, DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(), Username = username, DisplayName = displayName, PasswordHash = "test", CreatedAt = createdAt
    };

    private sealed class SequenceRandomSource(params int[] values) : IDiceRollRandomSource
    {
        private int _index;
        public int NextInclusive(int minimum, int maximum) => values[_index++];
    }

    private static async Task<IReadOnlyList<string>> ColumnsAsync(SqliteConnection connection, string table)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{table}');";
        await using var reader = await command.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync()) names.Add(reader.GetString(1));
        return names;
    }

    private static string Slice(string source, string start, string end)
    {
        var startIndex = source.IndexOf(start, StringComparison.Ordinal);
        var endIndex = source.IndexOf(end, startIndex, StringComparison.Ordinal);
        Assert.True(startIndex >= 0 && endIndex > startIndex, $"Could not slice {start} -> {end}.");
        return source[startIndex..endIndex];
    }

    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine([Root, .. parts]));
    private static string FindRoot([CallerFilePath] string sourceFile = "") =>
        Directory.GetParent(Path.GetDirectoryName(sourceFile)!)!.FullName;
}
