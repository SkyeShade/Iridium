using Iridium.Client.Core;

namespace Iridium.Tests;

public sealed class AvatarEditStateTests
{
    [Fact]
    public void WideImageCanPanHorizontallyAtMinimumZoom()
    {
        var state = State(1600, 800);
        Assert.True(state.MaximumOffsetX > 0);
        Assert.Equal(0, state.MaximumOffsetY);
        state.Pan(10_000, 10_000);
        Assert.Equal(state.MaximumOffsetX, state.OffsetX);
        Assert.Equal(0, state.OffsetY);
    }

    [Fact]
    public void TallImageCanPanVerticallyAtMinimumZoom()
    {
        var state = State(800, 1600);
        Assert.Equal(0, state.MaximumOffsetX);
        Assert.True(state.MaximumOffsetY > 0);
        state.Pan(-10_000, -10_000);
        Assert.Equal(0, state.OffsetX);
        Assert.Equal(-state.MaximumOffsetY, state.OffsetY);
    }

    [Fact]
    public void SquareImageCanPanBothAxesAfterZoom()
    {
        var state = State(1000, 1000);
        Assert.Equal(0, state.MaximumOffsetX);
        Assert.Equal(0, state.MaximumOffsetY);
        state.SetZoom(2);
        state.Pan(100, -120);
        Assert.Equal(100, state.OffsetX);
        Assert.Equal(-120, state.OffsetY);
    }

    [Fact]
    public void ZoomPreservesSourceCropCenterAndClampsToNewBounds()
    {
        var state = State(1600, 900);
        state.Pan(175, 0);
        var sourceCenterBefore = state.OffsetX / state.RenderedScale;
        state.SetZoom(2.25);
        Assert.Equal(sourceCenterBefore, state.OffsetX / state.RenderedScale, 8);
        state.SetZoom(1);
        Assert.InRange(state.OffsetX, -state.MaximumOffsetX, state.MaximumOffsetX);
    }

    [Fact]
    public void PersistedNormalizedTransformRecreatesExactCroppedPreview()
    {
        var editor = State(1440, 900);
        editor.SetZoom(1.73);
        editor.Pan(183.25, -74.5);
        var persisted = new AvatarEditState("avatar", 1440, 900, 1000, 1000, 1000,
            editor.Zoom, editor.NormalizedOffsetX, editor.NormalizedOffsetY);
        Assert.Equal(editor.CroppedImageStyle, persisted.CroppedImageStyle);
        Assert.Equal(editor.SourceCrop, persisted.SourceCrop);
    }

    private static AvatarEditState State(int width, int height) =>
        new("avatar", width, height, 760, 1000, 1000);
}
