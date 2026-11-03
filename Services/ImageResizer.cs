using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace MusicFileParser.Services;

public enum CoverAdjustment
{
    Keep,
    ScaleProportionally
}

public sealed class PreparedCover
{
    public required byte[] Data { get; init; }

    public required string MimeType { get; init; }

    public required int SourceWidth { get; init; }

    public required int SourceHeight { get; init; }

    public required int Width { get; init; }

    public required int Height { get; init; }

    public required CoverAdjustment Adjustment { get; init; }
}

public static class ImageResizer
{
    public const int TargetSize = 500;

    private const long JpegQuality = 90L;
    private const int ExifOrientationId = 0x0112;

    public static PreparedCover Prepare(byte[] imageBytes, string? sourceMimeType = null)
    {
        using var input = new MemoryStream(imageBytes);
        using var source = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
        NormalizeOrientation(source);

        int width = source.Width;
        int height = source.Height;
        if (width <= TargetSize && height <= TargetSize)
        {
            return new PreparedCover
            {
                Data = imageBytes,
                MimeType = ResolveMime(source, sourceMimeType),
                SourceWidth = width,
                SourceHeight = height,
                Width = width,
                Height = height,
                Adjustment = CoverAdjustment.Keep
            };
        }

        using Bitmap scaled = ScaleToFit(source);
        return Encoded(scaled, width, height, CoverAdjustment.ScaleProportionally);
    }

    public static (int Width, int Height) Measure(byte[] imageBytes)
    {
        using var input = new MemoryStream(imageBytes);
        using var source = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
        NormalizeOrientation(source);
        return (source.Width, source.Height);
    }

    public static byte[] ToJpeg(byte[] imageBytes)
    {
        using var input = new MemoryStream(imageBytes);
        using var source = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
        NormalizeOrientation(source);
        return EncodeJpeg(source);
    }

    private static PreparedCover Encoded(
        Bitmap bitmap,
        int sourceWidth,
        int sourceHeight,
        CoverAdjustment adjustment) =>
        new()
        {
            Data = EncodeJpeg(bitmap),
            MimeType = "image/jpeg",
            SourceWidth = sourceWidth,
            SourceHeight = sourceHeight,
            Width = bitmap.Width,
            Height = bitmap.Height,
            Adjustment = adjustment
        };

    private static Bitmap ScaleToFit(Image source)
    {
        double scale = Math.Min(TargetSize / (double)source.Width, TargetSize / (double)source.Height);
        int width = Math.Clamp((int)Math.Round(source.Width * scale), 1, TargetSize);
        int height = Math.Clamp((int)Math.Round(source.Height * scale), 1, TargetSize);

        var bitmap = new Bitmap(width, height, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(bitmap);
        Prepare(graphics);
        graphics.Clear(Color.Black);
        graphics.DrawImage(source, 0, 0, width, height);
        return bitmap;
    }

    private static void Prepare(Graphics graphics)
    {
        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.CompositingQuality = CompositingQuality.HighQuality;
        graphics.SmoothingMode = SmoothingMode.HighQuality;
    }

    private static void NormalizeOrientation(Image image)
    {
        if (!image.PropertyIdList.Contains(ExifOrientationId))
            return;

        PropertyItem? item = image.GetPropertyItem(ExifOrientationId);
        if (item?.Value is not { Length: >= 2 } value)
            return;

        short orientation = BitConverter.ToInt16(value, 0);
        RotateFlipType rotateFlip = orientation switch
        {
            2 => RotateFlipType.RotateNoneFlipX,
            3 => RotateFlipType.Rotate180FlipNone,
            4 => RotateFlipType.Rotate180FlipX,
            5 => RotateFlipType.Rotate90FlipX,
            6 => RotateFlipType.Rotate90FlipNone,
            7 => RotateFlipType.Rotate270FlipX,
            8 => RotateFlipType.Rotate270FlipNone,
            _ => RotateFlipType.RotateNoneFlipNone
        };

        if (rotateFlip != RotateFlipType.RotateNoneFlipNone)
            image.RotateFlip(rotateFlip);

        image.RemovePropertyItem(ExifOrientationId);
    }

    private static string ResolveMime(Image image, string? sourceMimeType)
    {
        if (image.RawFormat.Equals(ImageFormat.Jpeg))
            return "image/jpeg";
        if (image.RawFormat.Equals(ImageFormat.Png))
            return "image/png";
        if (image.RawFormat.Equals(ImageFormat.Gif))
            return "image/gif";
        if (image.RawFormat.Equals(ImageFormat.Bmp))
            return "image/bmp";
        if (image.RawFormat.Equals(ImageFormat.Tiff))
            return "image/tiff";
        if (!string.IsNullOrWhiteSpace(sourceMimeType))
            return sourceMimeType;

        return "image/jpeg";
    }

    private static byte[] EncodeJpeg(Image image)
    {
        ImageCodecInfo? encoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
        if (encoder is null)
            throw new InvalidOperationException("JPEG-кодек недоступен.");

        using var output = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
        image.Save(output, encoder, parameters);
        return output.ToArray();
    }
}
