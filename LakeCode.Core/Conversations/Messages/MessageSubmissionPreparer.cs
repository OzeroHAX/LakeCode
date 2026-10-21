using System.Collections.Immutable;
using LakeCode.Core.Agent;
using LakeCode.Core.Agent.Tools;
using LakeCode.Core.Attachments;
using LakeCode.Core.Providers;
using LakeCode.Core.Sessions;

namespace LakeCode.Core.Conversations.Messages;

/// <summary>Validates and captures an accepted message, without queueing, persistence or cancellation.</summary>
internal sealed class MessageSubmissionPreparer(
    string workspaceRoot, ProviderRegistry providers, Settings.AgentSettingsStorage? settings)
{
    internal sealed record Selection(ProviderProfile Provider, ModelInfo Model, Settings.ModelConfiguration? Subagent);

    // Resolution precedes the service's queue checks; attachment validation follows them.
    public Selection Resolve(SubmitMessage command)
    {
        var provider = providers.Get(command.ProviderId);
        var model = providers.GetModel(command.ProviderId, command.ModelId);
        var subagent = command.SubagentModel?.Resolve(providers);
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
            throw new InvalidOperationException("Укажите API-ключ через /providers; черновик сохранён.");
        if (!model.ReasoningEfforts.Contains(command.ReasoningEffort) || !Enum.IsDefined(command.Mode) || !Enum.IsDefined(command.AgentMode))
            throw new InvalidOperationException("Настройки сообщения больше не поддерживаются.");
        if (!command.Images.IsDefaultOrEmpty && !model.InputModalities.Contains(ModelModality.Image))
            throw new InvalidOperationException($"Модель {model.Name} не поддерживает изображения. Выберите другую модель или удалите вложения; черновик сохранён.");
        return new Selection(provider, model, subagent);
    }

    public SavedImage[] ValidateAttachments(SubmitMessage command, ModelInfo model, bool historyHasImages)
    {
        if (!model.InputModalities.Contains(ModelModality.Image) && historyHasImages)
            throw new InvalidOperationException("История содержит изображения. Выберите модель с image input или начните новую сессию; черновик сохранён.");
        var images = command.Images.IsDefault ? ImmutableArray<ImageData>.Empty : command.Images;
        var files = command.Files.IsDefault ? ImmutableArray<FileAttachment>.Empty : command.Files;
        var mentions = command.Mentions.IsDefault ? ImmutableArray<FileMention>.Empty : command.Mentions;
        FileAttachments.Validate(files, model.ContextWindowTokens);
        FileMention.Validate(command.Text, mentions, files);
        var workspace = new WorkspaceFiles(workspaceRoot);
        foreach (var file in files) workspace.Resolve(file.Path, allowMissing: true);
        ImageAttachments.ValidateTotal(images.Select(image => image.Data));
        // Own the bytes: later draft edits cannot affect an accepted message.
        var saved = images.Select(image => new SavedImage(image.Name, image.Data.ToArray())).ToArray();
        foreach (var image in saved) image.Decode();
        return saved;
    }

    public PendingMessage Capture(SubmitMessage command, Selection selection, SavedImage[] images, Guid? executingPlanId)
    {
        var files = command.Files.IsDefault ? ImmutableArray<FileAttachment>.Empty : command.Files;
        var mentions = command.Mentions.IsDefault ? ImmutableArray<FileMention>.Empty : command.Mentions;
        var imageGeneration = Settings.ImageGenerationConfiguration.Capture(settings?.Current.ImageGeneration, providers);
        return new PendingMessage(command.Text, images, selection.Model.Id, command.ReasoningEffort, command.Mode, selection.Provider.Id)
        {
            Connection = selection.Provider, Model = selection.Model, Files = files.ToArray(), Mentions = mentions.ToArray(),
            AgentMode = command.AgentMode, ExecutingPlanId = executingPlanId,
            ImageGeneration = imageGeneration.Selection, ImageGenerationConfiguration = imageGeneration,
            ClassifierConfiguration = (settings?.Current.Classifier ?? new ExecutionApproval.ClassifierSettings()).Capture(providers),
            SubagentModel = command.SubagentModel, SubagentConfiguration = selection.Subagent
        };
    }
}
