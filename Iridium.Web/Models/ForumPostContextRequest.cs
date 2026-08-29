using Iridium.Protocol;

namespace Iridium.Web.Models;

public sealed record ForumPostContextRequest(
    CommunityForumPostDto Post,
    double X,
    double Y,
    bool IsOpenPost = false);
