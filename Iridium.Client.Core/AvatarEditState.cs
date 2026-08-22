using System.Globalization;

namespace Iridium.Client.Core;

public sealed record AvatarSourceCrop(double X, double Y, double Width, double Height);

/// <summary>Canonical source-to-crop transform used by the editor and every cropped preview.</summary>
public sealed class AvatarEditState
{
    public const double MinimumZoom = 1;
    public const double MaximumZoom = 3;
    public string SourceUrl { get; }
    public int SourceWidth { get; }
    public int SourceHeight { get; }
    public double CropDiameter { get; }
    public double ViewportWidth { get; }
    public double ViewportHeight { get; }
    public double Zoom { get; private set; }
    public double OffsetX { get; private set; }
    public double OffsetY { get; private set; }

    public double MinimumScale => Math.Max(CropDiameter / SourceWidth, CropDiameter / SourceHeight);
    public double RenderedScale => MinimumScale * Zoom;
    public double RenderedWidth => SourceWidth * RenderedScale;
    public double RenderedHeight => SourceHeight * RenderedScale;
    public double MaximumOffsetX => Math.Max(0, (RenderedWidth - CropDiameter) / 2);
    public double MaximumOffsetY => Math.Max(0, (RenderedHeight - CropDiameter) / 2);
    public double NormalizedOffsetX => MaximumOffsetX <= 0 ? 0 : OffsetX / MaximumOffsetX;
    public double NormalizedOffsetY => MaximumOffsetY <= 0 ? 0 : OffsetY / MaximumOffsetY;
    public AvatarSourceCrop SourceCrop
    {
        get
        {
            var width = CropDiameter / RenderedScale;
            var height = width;
            var centerX = SourceWidth / 2d - OffsetX / RenderedScale;
            var centerY = SourceHeight / 2d - OffsetY / RenderedScale;
            return new(centerX - width / 2, centerY - height / 2, width, height);
        }
    }

    public AvatarEditState(string sourceUrl, int sourceWidth, int sourceHeight, double cropDiameter,
        double viewportWidth, double viewportHeight, double zoom = 1,
        double normalizedOffsetX = 0, double normalizedOffsetY = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceUrl);
        if (sourceWidth <= 0 || sourceHeight <= 0 || cropDiameter <= 0 || viewportWidth <= 0 || viewportHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(sourceWidth), "Avatar geometry dimensions must be positive.");
        SourceUrl = sourceUrl;
        SourceWidth = sourceWidth;
        SourceHeight = sourceHeight;
        CropDiameter = cropDiameter;
        ViewportWidth = viewportWidth;
        ViewportHeight = viewportHeight;
        Zoom = Math.Clamp(zoom, MinimumZoom, MaximumZoom);
        OffsetX = Math.Clamp(normalizedOffsetX, -1, 1) * MaximumOffsetX;
        OffsetY = Math.Clamp(normalizedOffsetY, -1, 1) * MaximumOffsetY;
    }

    public void Pan(double deltaX, double deltaY)
    {
        OffsetX = Math.Clamp(OffsetX + deltaX, -MaximumOffsetX, MaximumOffsetX);
        OffsetY = Math.Clamp(OffsetY + deltaY, -MaximumOffsetY, MaximumOffsetY);
    }

    public void SetZoom(double zoom)
    {
        var oldScale = RenderedScale;
        Zoom = Math.Clamp(zoom, MinimumZoom, MaximumZoom);
        var scaleRatio = oldScale <= 0 ? 1 : RenderedScale / oldScale;
        OffsetX = Math.Clamp(OffsetX * scaleRatio, -MaximumOffsetX, MaximumOffsetX);
        OffsetY = Math.Clamp(OffsetY * scaleRatio, -MaximumOffsetY, MaximumOffsetY);
    }

    public string EditorImageStyle => ImageStyle(ViewportWidth, ViewportHeight);
    public string CroppedImageStyle => ImageStyle(CropDiameter, CropDiameter);
    public string ZoomFillStyle => FormattableString.Invariant($"--zoom-fill:{(Zoom - MinimumZoom) / (MaximumZoom - MinimumZoom) * 100:0.###}%");

    private string ImageStyle(double targetWidth, double targetHeight)
    {
        var width = RenderedWidth / targetWidth * 100;
        var height = RenderedHeight / targetHeight * 100;
        var x = 50 + OffsetX / targetWidth * 100;
        var y = 50 + OffsetY / targetHeight * 100;
        return string.Create(CultureInfo.InvariantCulture,
            $"width:{width:0.########}%;height:{height:0.########}%;left:{x:0.########}%;top:{y:0.########}%");
    }
}
