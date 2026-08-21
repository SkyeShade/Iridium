using Iridium.Client.Core;
using Iridium.Protocol;

namespace Iridium.Tests;

public sealed class CommunityVisibleTreeProjectionTests
{
    [Fact]
    public void RecursiveProjectionCarriesCanonicalDepthParentAndSubtreeGaps()
    {
        var communityId = Guid.NewGuid();
        var a = Category("A", null, 0); var b = Category("B", a.Id, 0);
        var c = Category("C", b.Id, 0); var d = Category("D", b.Id, 1);
        var e = Category("E", a.Id, 1); var f = Category("F", null, 1);
        var categories = new[] { a, b, c, d, e, f };
        var channels = new[]
        {
            Channel("c1", c.Id, 0), Channel("c2", c.Id, 1),
            Channel("d1", d.Id, 0), Channel("e1", e.Id, 0)
        };

        var rows = CommunityVisibleTreeProjection.Build(categories, channels, new HashSet<Guid>());

        Assert.Equal([a.Id, b.Id, c.Id, channels[0].Id, channels[1].Id, d.Id, channels[2].Id,
            e.Id, channels[3].Id, f.Id], rows.Select(value => value.ItemId));
        AssertRow(c.Id, b.Id, 2, 0, 2, 4);
        AssertRow(d.Id, b.Id, 2, 1, 5, 6);
        AssertRow(e.Id, a.Id, 1, 1, 7, 8);
        AssertRow(f.Id, null, 0, 1, 9, 9);
        Assert.Equal(e.Id, rows[rows.Single(value => value.ItemId == b.Id).SubtreeEndIndex + 1].ItemId);
        Assert.Equal(d.Id, rows[rows.Single(value => value.ItemId == d.Id).FlatVisibleIndex].ItemId);
        return;

        CommunityCategoryDto Category(string name, Guid? parentId, int position) =>
            new(Guid.NewGuid(), communityId, name, position, parentId);
        CommunityChannelDto Channel(string name, Guid? parentId, int position) =>
            new(Guid.NewGuid(), communityId, parentId, name, position, DateTimeOffset.UtcNow);
        void AssertRow(Guid id, Guid? parentId, int depth, int siblingPosition, int flatIndex, int subtreeEnd)
        {
            var row = rows.Single(value => value.ItemId == id);
            Assert.Equal(parentId, row.ParentCategoryId);
            Assert.Equal(depth, row.Depth);
            Assert.Equal(siblingPosition, row.PositionWithinParent);
            Assert.Equal(flatIndex, row.FlatVisibleIndex);
            Assert.Equal(subtreeEnd, row.SubtreeEndIndex);
        }
    }

    [Fact]
    public void CollapsedCategoryEndsAtItsOwnVisibleRowButRetainsFullSubtreeHeight()
    {
        var communityId = Guid.NewGuid();
        var a = new CommunityCategoryDto(Guid.NewGuid(), communityId, "A", 0, null);
        var b = new CommunityCategoryDto(Guid.NewGuid(), communityId, "B", 0, a.Id);
        var c = new CommunityCategoryDto(Guid.NewGuid(), communityId, "C", 0, b.Id);

        var rows = CommunityVisibleTreeProjection.Build([a, b, c], [], new HashSet<Guid> { b.Id });

        Assert.Equal([a.Id, b.Id], rows.Select(value => value.ItemId));
        var collapsed = rows.Single(value => value.ItemId == b.Id);
        Assert.Equal(collapsed.FlatVisibleIndex, collapsed.SubtreeEndIndex);
        Assert.Equal(2, collapsed.SubtreeHeight);
    }
}
