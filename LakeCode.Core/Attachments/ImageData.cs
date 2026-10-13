using StbImageSharp;

namespace LakeCode.Core.Attachments;

/// <summary>Original image bytes and decoded metadata; no UI preview or control references.</summary>
public sealed record ImageData(string Name, ReadOnlyMemory<byte> Data, string MediaType, int Width, int Height);

public static class ImageAttachments
{
    public const int MaxImageBytes = 8 * 1024 * 1024;
    public const int MaxTotalBytes = 24 * 1024 * 1024;
    public const int MaxAttachments = 4;

    public static ImageData Decode(ReadOnlyMemory<byte> data, string name) => DecodePixels(data, name).Image;

    /// <summary>Validates PNG/JPEG and returns RGB pixels alongside the unchanged original bytes.</summary>
    public static (ImageData Image, byte[] Pixels) DecodePixels(ReadOnlyMemory<byte> data, string name)
    {
        if (data.IsEmpty) throw new InvalidOperationException("Изображение пустое.");
        if (data.Length > MaxImageBytes)
            throw new InvalidDataException("Изображение превышает 8 MiB.");

        // Reject other formats before invoking the decoder, regardless of the file name or MIME supplied by clipboard.
        string mediaType = data.Span.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? "image/png"
            : data.Span.StartsWith(new byte[] { 255, 216, 255 }) ? "image/jpeg"
            : throw new InvalidDataException("Неподдерживаемый формат изображения. Используйте PNG или JPEG.");
        var bytes = data.ToArray();
        using var stream = new MemoryStream(bytes, writable: false);
        var info = ImageInfo.FromStream(stream)
            ?? throw new InvalidDataException("Не удалось прочитать изображение.");
        if (info.Width <= 0 || info.Height <= 0)
            throw new InvalidDataException("Изображение имеет некорректные размеры.");
        if (info.Width > 8192 || info.Height > 8192 || (long)info.Width * info.Height > 16_000_000)
            throw new InvalidOperationException("Изображение слишком большое: максимум 8192 пикселя по стороне и 16 мегапикселей.");

        var image = ImageResult.FromMemory(bytes, ColorComponents.RedGreenBlue);
        if (image.Width != info.Width || image.Height != info.Height ||
            image.Data.Length != checked(info.Width * info.Height * 3))
            throw new InvalidDataException("Размеры декодированного изображения не совпадают с заголовком.");
        return (new ImageData(name, data, mediaType, info.Width, info.Height), image.Data);
    }

    public static void ValidateTotal(IEnumerable<ReadOnlyMemory<byte>> images)
    {
        var sizes = images.Select(image => image.Length).ToArray();
        if (sizes.Length > MaxAttachments || sizes.Any(size => size > MaxImageBytes) ||
            sizes.Sum(size => (long)size) > MaxTotalBytes)
            throw new InvalidDataException("Вложения сообщения превышают лимиты.");
    }
}
