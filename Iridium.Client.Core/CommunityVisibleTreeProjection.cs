using Iridium.Protocol;

namespace Iridium.Client.Core;

public sealed record CommunityVisibleTreeRow(
    Guid ItemId,
    CommunitySidebarItemType ItemType,
    Guid? ParentCategoryId,
    int Depth,
    int PositionWithinParent,
    int FlatVisibleIndex,
    int SubtreeEndIndex,
    int SubtreeHeight);

public static class CommunityVisibleTreeProjection
{
    public static IReadOnlyList<CommunityVisibleTreeRow> Build(
        IReadOnlyList<CommunityCategoryDto> categories,
        IReadOnlyList<CommunityChannelDto> channels,
        IReadOnlySet<Guid> collapsed)
    {
        var rows = new List<CommunityVisibleTreeRow>();
        AddScope(null, 0);
        return rows;

        void AddScope(Guid? parentCategoryId, int depth)
        {
            var siblings = categories.Where(value => value.ParentCategoryId == parentCategoryId)
                .Select(value => new ProjectionItem(value.Id, CommunitySidebarItemType.Category, value.Position))
                .Concat(channels.Where(value => value.CategoryId == parentCategoryId)
                    .Select(value => new ProjectionItem(value.Id, CommunitySidebarItemType.Channel, value.Position)))
                .OrderBy(value => value.Position).ThenBy(value => value.ItemType).ThenBy(value => value.ItemId).ToArray();
            for (var siblingIndex = 0; siblingIndex < siblings.Length; siblingIndex++)
            {
                var item = siblings[siblingIndex];
                var rowIndex = rows.Count;
                var subtreeHeight = item.ItemType == CommunitySidebarItemType.Channel ? 1 : CategorySubtreeHeight(item.ItemId, []);
                rows.Add(new(item.ItemId, item.ItemType, parentCategoryId, depth, siblingIndex,
                    rowIndex, rowIndex, subtreeHeight));
                if (item.ItemType == CommunitySidebarItemType.Category && !collapsed.Contains(item.ItemId))
                    AddScope(item.ItemId, depth + 1);
                rows[rowIndex] = rows[rowIndex] with { SubtreeEndIndex = rows.Count - 1 };
            }
        }

        int CategorySubtreeHeight(Guid categoryId, HashSet<Guid> visited)
        {
            if (!visited.Add(categoryId)) return int.MaxValue;
            var children = categories.Where(value => value.ParentCategoryId == categoryId).ToArray();
            if (children.Length == 0) return 1;
            var height = 1 + children.Max(value => CategorySubtreeHeight(value.Id, new HashSet<Guid>(visited)));
            return height;
        }
    }

    private sealed record ProjectionItem(Guid ItemId, CommunitySidebarItemType ItemType, int Position);
}
