using LakeCode.Agent;
using StbImageSharp;
using StbImageWriteSharp;
using ColorComponents = StbImageSharp.ColorComponents;
using ImageWriteColorComponents = StbImageWriteSharp.ColorComponents;

namespace LakeCode.Agent.Tools.Files;

/// <summary>
/// PNG/JPEG decoded once, normalized for model input and re-encoded when needed. The result never
/// carries base64 text: the bytes travel as an <see cref="AgentImage"/>.
/// </summary>
public sealed record ImageContent(
    string MediaType,
    ReadOnlyMemory<byte> Data,
    int Width,
    int Height,
    int OriginalWidth,
    int OriginalHeight)
{
    public const int MaxImageBytes = 8 * 1024 * 1024;
    public const int MaxTotalBytes = 24 * 1024 * 1024;
    public const int MaxAttachments = 4;

    /// <summary>Largest side kept for model input.</summary>
    public const int MaxSide = 2048;

    /// <summary>Patch budget of the vision input: a patch covers 32x32 pixels.</summary>
    public const int MaxPatches = 2500;

    /// <summary>Payload budget for one normalized image.</summary>
    public const int MaxInputBytes = 1024 * 1024;

    private const int PatchSize = 32;
    private const int JpegQuality = 85;
    private const int ShrinkAttempts = 3;

    public bool Resized => Width != OriginalWidth || Height != OriginalHeight;

    public AgentImage ToAgentImage(string? sourcePath) => new(MediaType, Data, Width, Height, sourcePath);

    /// <summary>Validates PNG/JPEG by content and returns bytes sized for a vision model.</summary>
    public static ImageContent Decode(ReadOnlyMemory<byte> data)
    {
        if (data.IsEmpty)
        {
            throw new InvalidDataException("Image is empty.");
        }

        if (data.Length > MaxImageBytes)
        {
            throw new InvalidDataException("Image exceeds 8 MiB.");
        }

        // Reject other formats before invoking the decoder, regardless of the file name or MIME supplied by the caller.
        var (sourceMediaType, hasAlpha) = data.Span.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            ? ("image/png", true)
            : data.Span.StartsWith(new byte[] { 255, 216, 255 })
                ? ("image/jpeg", false)
                : throw new InvalidDataException("Unsupported image format. Use PNG or JPEG.");

        var bytes = data.ToArray();
        var header = ReadHeader(bytes);

        // The header is checked before the full decode so a small file cannot request a huge pixel buffer.
        if (header.Width > 8192 || header.Height > 8192 || (long)header.Width * header.Height > 16_000_000)
        {
            throw new InvalidDataException(
                "Image is too large: at most 8192 pixels per side and 16 megapixels.");
        }

        var components = hasAlpha ? ColorComponents.RedGreenBlueAlpha : ColorComponents.RedGreenBlue;
        var decoded = ImageResult.FromMemory(bytes, components);

        if (decoded.Width != header.Width || decoded.Height != header.Height)
        {
            throw new InvalidDataException("Decoded image dimensions do not match its header.");
        }

        var channels = hasAlpha ? 4 : 3;
        var expected = checked(decoded.Width * decoded.Height * channels);

        if (decoded.Data.Length != expected)
        {
            throw new InvalidDataException("Decoded image pixel count does not match its dimensions.");
        }

        var (width, height) = Fit(decoded.Width, decoded.Height);


        var pixels = width == decoded.Width && height == decoded.Height
            ? decoded.Data
            : Resize(decoded.Data, decoded.Width, decoded.Height, width, height, channels);
        var sourceWidth = width;
        var sourceHeight = height;

        for (var attempt = 0; ; attempt++)
        {
            var encoded = Encode(pixels, width, height, hasAlpha);

            if (encoded.Data.Length <= MaxInputBytes)
            {
                if (width == decoded.Width && height == decoded.Height && bytes.Length <= encoded.Data.Length)
                {
                    return new ImageContent(sourceMediaType, bytes, width, height, width, height);
                }

                return new ImageContent(
                    encoded.MediaType,
                    encoded.Data,
                    width,
                    height,
                    decoded.Width,
                    decoded.Height);
            }

            var nextWidth = Math.Max(1, width * 3 / 4);
            var nextHeight = Math.Max(1, height * 3 / 4);

            if (attempt == ShrinkAttempts - 1 || (nextWidth == width && nextHeight == height))
            {
                throw new InvalidDataException("Image could not be normalized within the 1 MiB payload budget.");
            }

            pixels = Resize(pixels, sourceWidth, sourceHeight, nextWidth, nextHeight, channels);
            width = sourceWidth = nextWidth;
            height = sourceHeight = nextHeight;
        }
    }

    private static (int Width, int Height) ReadHeader(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var info = ImageInfo.FromStream(stream) ?? throw new InvalidDataException("Could not read the image.");

        if (info.Width <= 0 || info.Height <= 0)
        {
            throw new InvalidDataException("Image dimensions are invalid.");
        }

        return (info.Width, info.Height);
    }

    /// <summary>Fits the image inside the side and patch budgets without enlarging it.</summary>
    private static (int Width, int Height) Fit(int width, int height)
    {
        var scale = Math.Min(1d, Math.Min((double)MaxSide / width, (double)MaxSide / height));
        scale = Math.Min(scale, Math.Sqrt((double)MaxPatches * PatchSize * PatchSize / ((double)width * height)));

        var fittedWidth = Math.Clamp((int)(width * scale), 1, width);
        var fittedHeight = Math.Clamp((int)(height * scale), 1, height);

        while (Patches(fittedWidth, fittedHeight) > MaxPatches && (fittedWidth > 1 || fittedHeight > 1))
        {
            fittedWidth = Math.Max(1, fittedWidth * 97 / 100);
            fittedHeight = Math.Max(1, fittedHeight * 97 / 100);
        }

        return (fittedWidth, fittedHeight);
    }

    private static int Patches(int width, int height) =>
        ((width + PatchSize - 1) / PatchSize) * ((height + PatchSize - 1) / PatchSize);

    /// <summary>Area average, so downscaled text stays readable instead of dissolving into noise.</summary>
    private static byte[] Resize(
        byte[] source,
        int sourceWidth,
        int sourceHeight,
        int width,
        int height,
        int channels)
    {
        var target = new byte[checked(width * height * channels)];
        Span<int> sums = stackalloc int[4];

        for (var y = 0; y < height; y++)
        {
            var startY = (long)y * sourceHeight / height;
            var endY = Math.Max(startY + 1, (long)(y + 1) * sourceHeight / height);

            for (var x = 0; x < width; x++)
            {
                var startX = (long)x * sourceWidth / width;
                var endX = Math.Max(startX + 1, (long)(x + 1) * sourceWidth / width);
                var count = (int)((endY - startY) * (endX - startX));
                sums.Clear();

                for (var sourceY = (int)startY; sourceY < endY; sourceY++)
                {
                    var row = sourceY * sourceWidth;

                    for (var sourceX = (int)startX; sourceX < endX; sourceX++)
                    {
                        var offset = (row + sourceX) * channels;

                        for (var channel = 0; channel < channels; channel++)
                        {
                            sums[channel] += source[offset + channel];
                        }
                    }
                }

                var targetOffset = (y * width + x) * channels;

                for (var channel = 0; channel < channels; channel++)
                {
                    target[targetOffset + channel] = (byte)(sums[channel] / count);
                }
            }
        }

        return target;
    }

    private static (string MediaType, byte[] Data) Encode(byte[] pixels, int width, int height, bool hasAlpha)
    {
        using var png = new MemoryStream();
        new ImageWriter().WritePng(
            pixels,
            width,
            height,
            hasAlpha ? ImageWriteColorComponents.RedGreenBlueAlpha : ImageWriteColorComponents.RedGreenBlue,
            png);

        if (png.Length <= MaxInputBytes)
        {
            return ("image/png", png.ToArray());
        }

        if (hasAlpha)
        {
            var rgb = new byte[checked(width * height * 3)];
            for (var pixel = 0; pixel < width * height; pixel++)
            {
                var alpha = pixels[pixel * 4 + 3];
                for (var channel = 0; channel < 3; channel++)
                {
                    rgb[pixel * 3 + channel] = (byte)(
                        (pixels[pixel * 4 + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
                }
            }
            pixels = rgb;
        }

        using var jpeg = new MemoryStream();
        new ImageWriter().WriteJpg(
            pixels,
            width,
            height,
            ImageWriteColorComponents.RedGreenBlue,
            jpeg,
            JpegQuality);

        return jpeg.Length <= MaxInputBytes ? ("image/jpeg", jpeg.ToArray()) : ("image/png", png.ToArray());
    }
}
