using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Iridium.Client.Core;
using Iridium.Protocol;
using Iridium.Server.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Tests;

public sealed class MessageForwardingTests
{
    [Fact(Timeout = 45_000)]
    public async Task ForwardingCreatesOneImmutableSnapshotAndReusesAttachmentBytes()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var project = Path.Combine(root, "Iridium.Server", "Iridium.Server.csproj");
        var temp = Path.Combine(Path.GetTempPath(), $"iridium-forwarding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temp);
        var database = Path.Combine(temp, "forwarding.db");
        var objects = Path.Combine(temp, "objects");
        var address = new Uri($"http://127.0.0.1:{FreePort()}/");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name ?? "Debug";
        using var server = StartServer(project, address, database, objects, configuration);
        var output = server.StandardOutput.ReadToEndAsync();
        var error = server.StandardError.ReadToEndAsync();
        try
        {
            await WaitForServerAsync(address, server, output, error);
            var owner = new NodeClient(address);
            var ownerAuth = await owner.RegisterAsync(new("forward-owner", "Forwarder", "test-password"));
            var member = new NodeClient(address);
            var memberAuth = await member.RegisterAsync(new("forward-member", "Recipient", "test-password"));
            var intruder = new NodeClient(address);
            var intruderAuth = await intruder.RegisterAsync(new("forward-intruder", "Intruder", "test-password"));
            var community = await owner.CreateCommunityAsync(new("Forwarding", null));
            var channel = Assert.Single((await owner.GetCommunityStructureAsync(community.Id)).Channels);
            var otherCommunity = await owner.CreateCommunityAsync(new("Elsewhere", null));
            var otherChannel = Assert.Single((await owner.GetCommunityStructureAsync(otherCommunity.Id)).Channels);
            var invite = await owner.CreateCommunityInviteAsync(community.Id, new(null, null));
            await member.JoinCommunityInviteAsync(CommunityInviteLink.Find(invite.InviteUrl!)!.Token);
            var otherInvite = await owner.CreateCommunityInviteAsync(otherCommunity.Id, new(null, null));
            await member.JoinCommunityInviteAsync(CommunityInviteLink.Find(otherInvite.InviteUrl!)!.Token);
            var conversation = await owner.OpenDirectConversationAsync(memberAuth.Account.Id);
            var bytes = "one stored file"u8.ToArray();
            var upload = await owner.UploadAttachmentAsync(new MemoryStream(bytes), "proof.txt", "text/plain");

            await using var ownerHub = Connection(address, ownerAuth.AccessToken);
            await using var memberHub = Connection(address, memberAuth.AccessToken);
            await using var intruderHub = Connection(address, intruderAuth.AccessToken);
            await Task.WhenAll(ownerHub.StartAsync(), memberHub.StartAsync(), intruderHub.StartAsync());
            var source = await ownerHub.InvokeAsync<ChannelMessageDto>(ChatHubContract.SendMessage,
                community.Id, channel.Id, new SendChannelMessageRequest("hello at forward time", null,
                    ClientMessageId: Guid.NewGuid(), AttachmentIds: [upload.Id]));

            var request = new ForwardMessageRequest(
                new(MessageLocationKind.CommunityChannel, source.Id, community.Id, channel.Id),
                [
                    new(MessageLocationKind.DirectConversation, ConversationId: conversation.Id),
                    new(MessageLocationKind.CommunityChannel, community.Id, channel.Id)
                ], "look at this");
            var forwarded = await ownerHub.InvokeAsync<ForwardMessagesResultDto>(ChatHubContract.ForwardMessage, request);
            var channelCopy = Assert.Single(forwarded.ChannelMessages);
            var directCopy = Assert.Single(forwarded.DirectMessages);
            Assert.Equal(ownerAuth.Account.Id, channelCopy.Author.AccountId);
            Assert.Equal(ownerAuth.Account.Id, directCopy.Author.AccountId);
            Assert.Equal("look at this", channelCopy.Content);
            Assert.Equal("hello at forward time", channelCopy.Forwarded!.Content);
            Assert.Equal(channelCopy.Forwarded.Id, directCopy.Forwarded!.Id);
            Assert.Equal(upload.Id, Assert.Single(channelCopy.Forwarded.Attachments).Id);
            Assert.Equal(new ForwardSourceReferenceDto(community.Id, channel.Id, source.Id), channelCopy.Forwarded.Source);
            var crossCommunity = await ownerHub.InvokeAsync<ForwardMessagesResultDto>(ChatHubContract.ForwardMessage,
                new ForwardMessageRequest(new(MessageLocationKind.CommunityChannel, source.Id, community.Id, channel.Id),
                    [new(MessageLocationKind.CommunityChannel, otherCommunity.Id, otherChannel.Id)]));
            Assert.Equal(otherCommunity.Id, Assert.Single(crossCommunity.ChannelMessages).CommunityId);

            await ownerHub.InvokeAsync<ChannelMessageDto>(ChatHubContract.EditMessage, community.Id, channel.Id,
                source.Id, new EditChannelMessageRequest("edited source"));
            await ownerHub.InvokeAsync(ChatHubContract.DeleteMessage, community.Id, channel.Id, source.Id);
            var directHistory = await owner.GetDirectMessagePageAsync(conversation.Id);
            var persisted = Assert.Single(directHistory.Messages, value => value.Id == directCopy.Id);
            Assert.Equal("hello at forward time", persisted.Forwarded!.Content);
            Assert.Equal(bytes, await member.DownloadAttachmentAsync(upload.Id));
            Assert.Single(Directory.GetFiles(objects));

            var editedCopy = await ownerHub.InvokeAsync<DirectMessageDto>(DirectMessageHubContract.EditMessage,
                conversation.Id, directCopy.Id, new EditDirectMessageRequest("updated note"));
            Assert.Equal("updated note", editedCopy.Content);
            Assert.Equal("hello at forward time", editedCopy.Forwarded!.Content);

            var flattened = await ownerHub.InvokeAsync<ForwardMessagesResultDto>(ChatHubContract.ForwardMessage,
                new ForwardMessageRequest(new(MessageLocationKind.DirectConversation, directCopy.Id,
                        ConversationId: conversation.Id),
                    [new(MessageLocationKind.CommunityChannel, community.Id, channel.Id)]));
            Assert.Equal(channelCopy.Forwarded.Id, Assert.Single(flattened.ChannelMessages).Forwarded!.Id);

            var directSource = await ownerHub.InvokeAsync<DirectMessageDto>(DirectMessageHubContract.SendMessage,
                conversation.Id, new SendDirectMessageRequest("from a DM", null, Guid.NewGuid()));
            var directToCommunity = await ownerHub.InvokeAsync<ForwardMessagesResultDto>(ChatHubContract.ForwardMessage,
                new ForwardMessageRequest(new(MessageLocationKind.DirectConversation, directSource.Id,
                        ConversationId: conversation.Id),
                    [new(MessageLocationKind.CommunityChannel, community.Id, channel.Id)]));
            var directSourceCopy = Assert.Single(directToCommunity.ChannelMessages);
            Assert.Equal("from a DM", directSourceCopy.Forwarded!.Content);
            Assert.Null(directSourceCopy.Forwarded.Source);

            var six = Enumerable.Range(0, 6).Select(_ =>
                new ForwardDestinationSelectionDto(MessageLocationKind.DirectConversation,
                    ConversationId: conversation.Id)).ToArray();
            await Assert.ThrowsAsync<HubException>(() => ownerHub.InvokeAsync<ForwardMessagesResultDto>(
                ChatHubContract.ForwardMessage,
                new ForwardMessageRequest(new(MessageLocationKind.CommunityChannel, channelCopy.Id,
                    community.Id, channel.Id), six)));
            await Assert.ThrowsAsync<HubException>(() => intruderHub.InvokeAsync<ForwardMessagesResultDto>(
                ChatHubContract.ForwardMessage, request));
            await owner.SetPermissionOverwriteAsync(otherCommunity.Id, PermissionOverwriteScopeType.Channel,
                otherChannel.Id, new(PermissionOverwriteTargetType.Member, memberAuth.Account.Id,
                    CommunityPermission.None, CommunityPermission.SendMessages));
            await Assert.ThrowsAsync<HubException>(() => memberHub.InvokeAsync<ForwardMessagesResultDto>(
                ChatHubContract.ForwardMessage,
                new ForwardMessageRequest(new(MessageLocationKind.CommunityChannel, channelCopy.Id,
                        community.Id, channel.Id),
                    [new(MessageLocationKind.CommunityChannel, otherCommunity.Id, otherChannel.Id)])));

            await using (var connection = new SqliteConnection($"Data Source={database}"))
            {
                await connection.OpenAsync();
                Assert.Equal(1L, await ScalarAsync(connection, "SELECT COUNT(*) FROM Attachments;"));
                Assert.Equal(2L, await ScalarAsync(connection, "SELECT COUNT(*) FROM ForwardedMessageAttachments;"));
            }
        }
        finally
        {
            if (!server.HasExited) server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
            SqliteConnection.ClearAllPools();
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try { Directory.Delete(temp, true); break; }
                catch (IOException) when (attempt < 19) { await Task.Delay(100); }
            }
        }
    }

    [Fact]
    public void ForwardMetadataRoundTripsWithoutImageBytesOrOriginalAuthorIdentity()
    {
        var attachment = new AttachmentDto(Guid.NewGuid(), "photo.png", "image/png", 1234,
            "api/attachments/id", 32, 32, "#112233");
        var snapshot = new ForwardedMessageSnapshotDto(Guid.NewGuid(), "@Skye hello", [], [attachment]);
        var message = new ChannelMessageDto(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new(Guid.NewGuid(), "forwarder", "Forwarder"), string.Empty, DateTimeOffset.UtcNow,
            null, false, null, Forwarded: snapshot);
        var json = JsonSerializer.Serialize(message);
        var restored = JsonSerializer.Deserialize<ChannelMessageDto>(json);

        Assert.Equal(snapshot.Id, restored!.Forwarded!.Id);
        Assert.Equal("@Skye hello", restored.Forwarded.Content);
        Assert.DoesNotContain("base64", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OriginalAuthor", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ForwardingUiUsesExistingMessageMenuRenderersAndBoundedLocalDestinationSearch()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        string Source(params string[] parts) => File.ReadAllText(Path.Combine([root, .. parts]));
        var row = Source("Iridium.Web", "Components", "MessageRow.razor");
        var modal = Source("Iridium.Web", "Components", "ForwardMessageModal.razor");
        var modalCss = Source("Iridium.Web", "Components", "ForwardMessageModal.razor.css");
        var block = Source("Iridium.Web", "Components", "ForwardedMessageBlock.razor");
        var blockCss = Source("Iridium.Web", "Components", "ForwardedMessageBlock.razor.css");
        var cache = Source("Iridium.Web", "wwwroot", "js", "messageHistoryCache.js");

        Assert.Contains("<Icon Name=\"forward\" /> Forward", row);
        Assert.DoesNotContain("message-actions", row[row.IndexOf("ForwardFromMenu", StringComparison.Ordinal)..]);
        Assert.Contains("Session.DirectConversations.OrderByDescending(value => value.LastMessageAt)", modal);
        Assert.Contains("CommunityPermission.SendMessages", modal);
        Assert.Contains("MessageForwardingLimits.MaximumDestinations", modal);
        Assert.Contains("MatchRank", modal);
        Assert.Contains("Add an optional message", modal);
        Assert.True(modal.IndexOf("class=\"destination-copy\"", StringComparison.Ordinal) <
                    modal.IndexOf("class=\"destination-selection\"", StringComparison.Ordinal));
        Assert.Contains("class=\"destination-checkbox\" type=\"checkbox\"", modal);
        Assert.Contains("grid-template-columns: 2rem minmax(0, 1fr) 2.75rem", modalCss);
        Assert.Contains(".destination-checkbox:checked + .destination-check", modalCss);
        Assert.Contains(".destination-checkbox:focus-visible + .destination-check", modalCss);
        Assert.Contains("width: 2.75rem", modalCss);
        Assert.Contains("<MessageAttachments Attachments=\"Snapshot.Attachments\"", block);
        Assert.Contains("<MessageExternalEmbeds Content=\"@Snapshot.Content\"", block);
        Assert.Contains(".forwarded-block::before", blockCss);
        Assert.Contains("message.forwarded?.attachments", cache);
        Assert.DoesNotContain("arrayBuffer", cache, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CompatibilityUpgradeAddsForwardMetadataWithoutRebuildingMessageTables()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE ChannelMessages (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE DirectMessages (Id TEXT NOT NULL PRIMARY KEY);
                CREATE TABLE Attachments (Id TEXT NOT NULL PRIMARY KEY);
                """;
            await command.ExecuteNonQueryAsync();
        }
        var options = new DbContextOptionsBuilder<IridiumDbContext>().UseSqlite(connection).Options;
        await using var db = new IridiumDbContext(options);

        await DatabaseCompatibility.EnsureMessageForwardingSchemaAsync(db);
        await DatabaseCompatibility.EnsureMessageForwardingSchemaAsync(db);

        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_info('ChannelMessages') WHERE name = 'ForwardedMessageSnapshotId';"));
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM pragma_table_info('DirectMessages') WHERE name = 'ForwardedMessageSnapshotId';"));
        Assert.Equal(1L, await ScalarAsync(connection,
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'ForwardedMessageSnapshots';"));
    }

    private static async Task<long> ScalarAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static HubConnection Connection(Uri address, string token) => new HubConnectionBuilder()
        .WithUrl(new Uri(address, "hubs/chat"), options => options.AccessTokenProvider = () => Task.FromResult<string?>(token)).Build();

    private static Process StartServer(string project, Uri address, string database, string storage, string configuration)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in new[] { "run", "--project", project, "--no-build", "--configuration", configuration, "--no-launch-profile" }) start.ArgumentList.Add(argument);
        start.Environment["ASPNETCORE_URLS"] = address.ToString().TrimEnd('/');
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ConnectionStrings__Iridium"] = $"Data Source={database}";
        start.Environment["Node__AttachmentStoragePath"] = storage;
        return Process.Start(start) ?? throw new InvalidOperationException("Could not start the Iridium test node.");
    }

    private static async Task WaitForServerAsync(Uri address, Process server, Task<string> output, Task<string> error)
    {
        using var http = new HttpClient { BaseAddress = address };
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (server.HasExited) throw new InvalidOperationException($"The test node stopped early.\n{await output}\n{await error}");
            try { if ((await http.GetAsync("api/server-info")).IsSuccessStatusCode) return; }
            catch (HttpRequestException) { }
            await Task.Delay(100);
        }
        throw new TimeoutException("The test node did not become ready.");
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
