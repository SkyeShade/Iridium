using Iridium.Server.Domain;
using Microsoft.EntityFrameworkCore;

namespace Iridium.Server.Api;

internal static class MessageForwardingQuery
{
    public static IQueryable<ChannelMessage> IncludeForwardedSnapshot(this IQueryable<ChannelMessage> query) =>
        query.Include(value => value.ForwardedMessageSnapshot)
            .ThenInclude(value => value!.Attachments)
            .ThenInclude(value => value.Attachment);

    public static IQueryable<DirectMessage> IncludeForwardedSnapshot(this IQueryable<DirectMessage> query) =>
        query.Include(value => value.ForwardedMessageSnapshot)
            .ThenInclude(value => value!.Attachments)
            .ThenInclude(value => value.Attachment);
}
