using System.ComponentModel;
using System.Text;
using LakeCode.Core.Attachments;
using LakeCode.Core.Providers;
using LakeCode.Core.Settings;

namespace LakeCode.Core.Agent.Tools.ImageGeneration;

internal sealed class ImageGenerationTool(WorkspaceFiles files, Func<ImageGenerationConfiguration?> configuration, string sessionId)
{
    public Microsoft.Extensions.AI.AIFunction AgentTool() =>
        Microsoft.Extensions.AI.AIFunctionFactory.Create(GenerateAsync, name: "generate_image");

    [Description("Generate or edit one image using the user's fixed image-model assignment (independent of the chat model). Paid external request: sends only prompt and the specified original PNG/JPEG reference files. Save a new PNG inside the workspace; never overwrite. Optional input_paths selects editing. Describe each reference's role in prompt. Models/providers cannot be selected by this tool. Report model warnings to the user. Do not automatically retry a failed/uncertain request or bypass an unavailable assignment through other tools.")]
    public async Task<string> GenerateAsync(
        [Description("Detailed image description or editing instructions. Include desired composition and shape; exact dimensions are not guaranteed.")] string prompt,
        [Description("New workspace-relative .png file path. Existing files cannot be overwritten.")] string output_path,
        [Description("Optional original PNG/JPEG reference paths, in prompt order. External paths require read permission. Up to 4 images, 8 MiB each and 24 MiB total.")] string[]? input_paths = null,
        CancellationToken cancellationToken = default)
    {
        var config = configuration();
        if (config?.Provider is null || config.Selection is null || config.Error is not null)
            return "Error: " + (config?.Error ?? "Генерация изображений не настроена. Выберите модели в /models → Изображения.");
        try
        {
            if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > WorkspaceFiles.MaxOutputCharacters)
                throw new ArgumentException("prompt must contain 1–32000 characters.");
            files.ValidateNewImagePath(output_path);
            input_paths ??= [];
            if (input_paths.Length > ImageAttachments.MaxAttachments)
                throw new ArgumentException("Too many reference images.");
            var inputs = new List<ImageData>();
            foreach (var path in input_paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = await files.ReadImageBytesAsync(path, cancellationToken);
                inputs.Add(ImageAttachments.Decode(bytes, Path.GetFileName(path)));
                ImageAttachments.ValidateTotal(inputs.Select(i => i.Data));
            }
            files.ValidateNewImagePath(output_path);
            var result = await ResponsesImageGenerationClient.GenerateAsync(config, prompt, inputs, sessionId, cancellationToken);
            try
            {
                await files.WriteImageAsync(output_path, result.Image.Data, cancellationToken);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                return $"Error: Сервер сгенерировал изображение, но файл не сохранён: {error.Message}\nНе повторять генерацию автоматически: запрос уже мог потратить квоту.";
            }
            var selection = config.Selection;
            var text = new StringBuilder()
                .AppendLine($"Saved: {files.DisplayPath(output_path)}")
                .AppendLine($"Format: {result.Image.MediaType}; dimensions: {result.Image.Width} × {result.Image.Height}; bytes: {result.Image.Data.Length}")
                .AppendLine($"Provider: {config.Provider.Name}")
                .AppendLine($"Carrier: {selection.CarrierModelId}")
                .AppendLine($"Requested image model: {selection.ImageModelId}")
                .AppendLine($"Server-reported image model: {result.ReportedModel ?? "not reported"}");
            if (result.ReportedModel is null)
                text.AppendLine("Warning: сервер не подтвердил image-модель. Изображение сохранено.");
            else if (!string.Equals(result.ReportedModel, selection.ImageModelId, StringComparison.Ordinal))
                text.AppendLine("Warning: сервер сообщил другую image-модель вместо выбранной пользователем. Изображение сохранено; выбранная модель не подтверждена.");
            return text.ToString();
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return "Error: " + error.Message;
        }
    }
}
