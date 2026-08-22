using Iridium.Client.Core;
using Iridium.Protocol;
using Iridium.Server.Storage;
using SkiaSharp;

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

    [Fact]
    public void BannerTransformUsesFiveToOneCropAndPansAtMinimumZoom()
    {
        var wide = new ProfileMediaEditState("banner", 3600, 600, ProfileBannerLimits.CropWidth,
            ProfileBannerLimits.CropHeight, 1200, 600);
        Assert.True(wide.MaximumOffsetX > 0);
        Assert.Equal(0, wide.MaximumOffsetY);
        var tall = new ProfileMediaEditState("banner", 800, 1600, ProfileBannerLimits.CropWidth,
            ProfileBannerLimits.CropHeight, 1200, 600);
        Assert.Equal(0, tall.MaximumOffsetX);
        Assert.True(tall.MaximumOffsetY > 0);
    }

    [Fact]
    public void StaticBannerProcessorProducesExactTwelveHundredByTwoFortyWebpDerivative()
    {
        using var bitmap = new SKBitmap(1600, 800);
        bitmap.Erase(SKColors.CornflowerBlue);
        using var sourceImage = SKImage.FromBitmap(bitmap);
        using var encoded = sourceImage.Encode(SKEncodedImageFormat.Png, 100);
        var source = encoded.ToArray();
        var processed = BannerImageProcessor.Process(
            new(source, "image/png", 1600, 800, false), .4, -.3, 1.6)!;
        using var data = SKData.CreateCopy(processed.Content);
        using var codec = SKCodec.Create(data);
        Assert.Equal("image/webp", processed.ContentType);
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        Assert.Equal(ProfileBannerLimits.ProcessedWidth, codec.Info.Width);
        Assert.Equal(ProfileBannerLimits.ProcessedHeight, codec.Info.Height);
    }

    [Fact]
    public void CommunityBannerUsesTheSameTwoToOneGeometryForTransformAndDerivative()
    {
        var editor = new ProfileMediaEditState("community-banner", 1800, 1200,
            CommunityBannerLimits.CropWidth, CommunityBannerLimits.CropHeight, 1200, 900, 1.8, .6, -.35);
        Assert.Equal(CommunityBannerLimits.AspectRatio, editor.CropWidth / editor.CropHeight);
        using var bitmap = new SKBitmap(1800, 1200);
        bitmap.Erase(SKColors.OrangeRed);
        using var sourceImage = SKImage.FromBitmap(bitmap);
        using var encoded = sourceImage.Encode(SKEncodedImageFormat.Png, 100);
        var processed = BannerImageProcessor.Process(new(encoded.ToArray(), "image/png", 1800, 1200, false),
            editor.NormalizedOffsetX, editor.NormalizedOffsetY, editor.Zoom,
            CommunityBannerLimits.CropWidth, CommunityBannerLimits.CropHeight,
            CommunityBannerLimits.ProcessedWidth, CommunityBannerLimits.ProcessedHeight)!;
        using var data = SKData.CreateCopy(processed.Content);
        using var codec = SKCodec.Create(data);
        Assert.Equal(SKEncodedImageFormat.Webp, codec.EncodedFormat);
        Assert.Equal(CommunityBannerLimits.ProcessedWidth, codec.Info.Width);
        Assert.Equal(CommunityBannerLimits.ProcessedHeight, codec.Info.Height);
        Assert.Equal(CommunityBannerLimits.AspectRatio, codec.Info.Width / (double)codec.Info.Height);
    }

    private static AvatarEditState State(int width, int height) =>
        new("avatar", width, height, 760, 1000, 1000);
}
