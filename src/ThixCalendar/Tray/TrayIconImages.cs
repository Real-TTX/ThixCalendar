using Avalonia.Controls;
using Avalonia.Platform;
using SkiaSharp;
using ThixCalendar.Core.Infrastructure;

namespace ThixCalendar.Tray;

/// <summary>What the badge of the tray icon says.</summary>
internal enum TrayBadge
{
    /// <summary>Reminders wait for an answer: a red dot.</summary>
    Alert,

    /// <summary>Reminders are paused: an amber pause sign.</summary>
    Paused,
}

/// <summary>
/// The tray icon with a badge at the bottom right, drawn at start (no second asset to keep in step). Windows: a coloured
/// badge. macOS: the menu bar colours a template image itself, so the badge is a solid circle (the pause bars cut out).
/// </summary>
internal static class TrayIconImages
{
    private const int Size = 64;

    public static WindowIcon? WithBadge(Uri asset, bool isTemplate, TrayBadge badge)
    {
        try
        {
            using var source = AssetLoader.Open(asset);
            return BadgePng(source, isTemplate, badge) is { } png ? new WindowIcon(new MemoryStream(png)) : null;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or ArgumentException)
        {
            Log.Warning($"Tray icon badge could not be drawn: {exception.Message}");
            return null;
        }
    }

    /// <summary>The icon with the badge as a 64 px PNG (null when the source cannot be decoded).</summary>
    internal static byte[]? BadgePng(Stream source, bool isTemplate, TrayBadge badge)
    {
        using var original = SKBitmap.Decode(source);
        if (original is null)
        {
            return null;
        }

        using var surface = SKSurface.Create(new SKImageInfo(Size, Size, SKColorType.Rgba8888, SKAlphaType.Premul));
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using (var image = SKImage.FromBitmap(original))
        {
            using var smooth = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true };
            canvas.DrawImage(image, new SKRect(0, 0, Size, Size), smooth);
        }

        var center = new SKPoint(Size * 0.72f, Size * 0.72f);
        var radius = Size * 0.27f;
        using var clear = new SKPaint { BlendMode = SKBlendMode.Clear, IsAntialias = true };
        canvas.DrawCircle(center, radius + Size * 0.06f, clear); // a gap around the badge

        var color = isTemplate ? SKColors.Black : badge == TrayBadge.Alert ? new SKColor(0xF0, 0x48, 0x3B) : new SKColor(0xF5, 0x9E, 0x0B);
        using var fill = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawCircle(center, radius, fill);

        if (badge == TrayBadge.Paused)
        {
            using var bars = isTemplate ? new SKPaint { BlendMode = SKBlendMode.Clear, IsAntialias = true } : new SKPaint { Color = SKColors.White, IsAntialias = true };
            var barWidth = radius * 0.32f;
            var barHeight = radius * 1.0f;
            var gap = radius * 0.26f;
            canvas.DrawRoundRect(new SKRect(center.X - gap / 2 - barWidth, center.Y - barHeight / 2, center.X - gap / 2, center.Y + barHeight / 2), 2, 2, bars);
            canvas.DrawRoundRect(new SKRect(center.X + gap / 2, center.Y - barHeight / 2, center.X + gap / 2 + barWidth, center.Y + barHeight / 2), 2, 2, bars);
        }

        using var snapshot = surface.Snapshot();
        using var png = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }
}
