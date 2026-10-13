using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace LakeCode.Core.Attachments;

/// <summary>Original image bytes and decoded metadata; no UI preview or control references.</summary>
public sealed record ImageData(string Name, ReadOnlyMemory<byte> Data, string MediaType, int Width, int Height);

public static class ImageAttachments
{
    public const int MaxImageBytes = 8 * 1024 * 1024;
    public const int MaxTotalBytes = 24 * 1024 * 1024;
    public const int MaxAttachments = 4;

    // Decode only supported formats, a single frame, without metadata. Original bytes are retained.
    public static DecoderOptions CreateDecoderOptions() => new()
    {
        Configuration = new Configuration(new PngConfigurationModule(), new JpegConfigurationModule(), new WebpConfigurationModule()),
        SkipMetadata = true,
        MaxFrames = 1,
        TargetSize = new Size(640, 400)
    };

    public static ImageData Decode(ReadOnlyMemory<byte> data, string name)
    {
        if (data.IsEmpty) throw new InvalidOperationException("Изображение пустое.");
        if (data.Length > MaxImageBytes)
            throw new InvalidDataException("Изображение превышает 8 MiB.");
        var options = CreateDecoderOptions();
        var info = Image.Identify(options, data.Span);
        if (info.Width > 8192 || info.Height > 8192 || (long)info.Width * info.Height > 16_000_000)
            throw new InvalidOperationException("Изображение слишком большое: максимум 8192 пикселя по стороне и 16 мегапикселей.");
        using var image = Image.Load<Rgb24>(options, data.Span);
        var mediaType = image.Metadata.DecodedImageFormat?.DefaultMimeType
            ?? throw new InvalidOperationException("Не удалось определить формат изображения.");
        return new ImageData(name, data, mediaType, info.Width, info.Height);
    }

    public static void ValidateTotal(IEnumerable<ReadOnlyMemory<byte>> images)
    {
        var sizes = images.Select(image => image.Length).ToArray();
        if (sizes.Length > MaxAttachments || sizes.Any(size => size > MaxImageBytes) ||
            sizes.Sum(size => (long)size) > MaxTotalBytes)
            throw new InvalidDataException("Вложения сообщения превышают лимиты.");
    }
}
