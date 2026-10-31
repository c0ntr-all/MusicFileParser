using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace MusicFileParser.Services;

public static class ImageResizer
{
    public const int TargetSize = 500;
    private const long JpegQuality = 90L;
    private const int ExifOrientationId = 0x0112;

    public static byte[] ToSquareJpeg(byte[] imageBytes)
    {
        using var input = new MemoryStream(imageBytes);
        using var source = Image.FromStream(input, useEmbeddedColorManagement: false, validateImageData: true);
        NormalizeOrientation(source);

        using Bitmap square = source.Width > TargetSize && source.Height > TargetSize
            ? CropCover(source)
            : PadToSquare(source);

        return EncodeJpeg(square);
    }

    private static Bitmap CropCover(Image source)
    {
        double scale = Math.Max(TargetSize / (double)source.Width, TargetSize / (double)source.Height);
        int scaledWidth = Math.Max(TargetSize, (int)Math.Round(source.Width * scale));
        int scaledHeight = Math.Max(TargetSize, (int)Math.Round(source.Height * scale));

        using var scaled = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(scaled))
        {
            Prepare(graphics);
            graphics.DrawImage(source, 0, 0, scaledWidth, scaledHeight);
        }

        int x = (scaled.Width - TargetSize) / 2;
        int y = (scaled.Height - TargetSize) / 2;
        var square = new Bitmap(TargetSize, TargetSize, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(square))
        {
            graphics.Clear(Color.Black);
            graphics.DrawImage(
                scaled,
                new Rectangle(0, 0, TargetSize, TargetSize),
                new Rectangle(x, y, TargetSize, TargetSize),
                GraphicsUnit.Pixel);
        }

        return square;
    }

    private static Bitmap PadToSquare(Image source)
    {
        double scale = 1d;
        if (source.Width > TargetSize || source.Height > TargetSize)
            scale = Math.Min(TargetSize / (double)source.Width, TargetSize / (double)source.Height);

        int width = Math.Clamp((int)Math.Round(source.Width * scale), 1, TargetSize);
        int height = Math.Clamp((int)Math.Round(source.Height * scale), 1, TargetSize);

        var canvas = new Bitmap(TargetSize, TargetSize, PixelFormat.Format24bppRgb);
        using var graphics = Graphics.FromImage(canvas);
        Prepare(graphics);
        graphics.Clear(Color.Black);
        int x = (TargetSize - width) / 2;
        int y = (TargetSize - height) / 2;
        graphics.DrawImage(source, x, y, width, height);
        return canvas;
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
