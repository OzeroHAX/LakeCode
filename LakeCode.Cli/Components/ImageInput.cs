using LakeCode.Core.Attachments;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using SharpConsoleUI.Helpers;
using SharpConsoleUI.Imaging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace LakeCode.Cli.Components;

internal sealed record ImageAttachment(string Name, ReadOnlyMemory<byte> Data, string MediaType, int Width, int Height, PixelBuffer Preview);
internal sealed record ClipboardInput(ImageAttachment? Image, string? Text);

internal static class ImageInput
{
    public const int MaxImageBytes = ImageAttachments.MaxImageBytes;
    public const int MaxTotalBytes = ImageAttachments.MaxTotalBytes;
    public const int MaxAttachments = ImageAttachments.MaxAttachments;
    private static readonly string[] ImageTypes = ["image/png", "image/jpeg", "image/webp"];
    private static readonly string[] TextTypes = ["text/plain;charset=utf-8", "text/plain", "UTF8_STRING", "STRING"];

    public static async Task<ClipboardInput> ReadClipboardAsync(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
            return new ClipboardInput(null, ClipboardHelper.GetText());
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_CONNECTION")) ||
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SSH_TTY")))
            throw new InvalidOperationException("Через SSH локальный буфер недоступен. Вставьте текст средствами терминала или выберите файл на сервере.");

        bool wayland = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
        string tool = wayland ? "wl-paste" : "xclip";
        if (!wayland && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
            throw new InvalidOperationException("Нет доступа к графическому буферу. Вставьте текст средствами терминала или выберите файл.");

        var typesData = await RunAsync(tool, wayland ? ["--list-types"] :
            ["-selection", "clipboard", "-o", "-t", "TARGETS"], 8192, cancellationToken);
        var types = Encoding.UTF8.GetString(typesData.Span)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string? imageType = ImageTypes.FirstOrDefault(type => types.Contains(type, StringComparer.Ordinal));
        if (imageType is not null)
        {
            var data = await RunAsync(tool, wayland ? ["--type", imageType] :
                ["-selection", "clipboard", "-o", "-t", imageType], MaxImageBytes, cancellationToken);
            return new ClipboardInput(Decode(data, "Изображение из буфера"), null);
        }

        string? textType = TextTypes.FirstOrDefault(type => types.Contains(type, StringComparer.Ordinal));
        if (textType is null)
            throw new InvalidOperationException("В буфере нет текста или поддерживаемого изображения. Используйте PNG, JPEG или WebP.");
        var text = await RunAsync(tool, wayland ? ["--no-newline", "--type", textType] :
            ["-selection", "clipboard", "-o", "-t", textType], 1024 * 1024, cancellationToken);
        var encoding = !wayland && textType == "STRING" ? Encoding.Latin1 : Encoding.UTF8;
        return new ClipboardInput(null, encoding.GetString(text.Span));
    }

    public static async Task<ImageAttachment> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var data = await ReadBoundedAsync(file, MaxImageBytes, cancellationToken);
        return Decode(data, Path.GetFileName(path));
    }

    internal static ImageAttachment Decode(ReadOnlyMemory<byte> data, string name) =>
        CreatePreview(ImageAttachments.Decode(data, name));

    internal static ImageAttachment CreatePreview(ImageData imageData)
    {
        using var image = Image.Load<Rgb24>(ImageAttachments.CreateDecoderOptions(), imageData.Data.Span);
        var pixels = new byte[checked(image.Width * image.Height * 3)];
        image.CopyPixelDataTo(pixels);
        return new ImageAttachment(imageData.Name, imageData.Data, imageData.MediaType,
            imageData.Width, imageData.Height, PixelBuffer.FromRgb24Bytes(pixels, image.Width, image.Height));
    }

    private static async Task<ReadOnlyMemory<byte>> RunAsync(
        string tool, string[] arguments, int maxBytes, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        try
        {
            try { process.Start(); }
            catch (Win32Exception error)
            {
                throw new InvalidOperationException($"Не удалось запустить {tool}. Установите {(tool == "wl-paste" ? "wl-clipboard" : "xclip")} или выберите файл.", error);
            }
            process.StandardInput.Close();
            using var registration = timeout.Token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (Win32Exception) { }
            });
            var stdout = ReadBoundedAsync(process.StandardOutput.BaseStream, maxBytes, timeout.Token);
            var stderr = ReadBoundedAsync(process.StandardError.BaseStream, 8192, timeout.Token);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"{tool}: {Encoding.UTF8.GetString(stderr.Result.Span).Trim()}");
            return stdout.Result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Буфер обмена не ответил за 5 секунд. Попробуйте ещё раз или выберите файл.");
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }
    }

    private static async Task<ReadOnlyMemory<byte>> ReadBoundedAsync(
        Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + count > maxBytes)
                throw new InvalidOperationException($"Превышен лимит чтения ({maxBytes / 1024} KiB). Изображение должно быть не больше 8 MiB.");
            output.Write(buffer, 0, count);
        }
        return output.GetBuffer().AsMemory(0, checked((int)output.Length));
    }
}
