using System.ClientModel;
using System.ClientModel.Primitives;
using LakeCode.Core.Providers;

namespace LakeCode.Core.Providers.Custom;

/// <summary>Observes Codex response headers for provider-specific routing diagnostics.</summary>
internal sealed class CodexResponseHeadersPolicy(
    string requestedModelId, Action<ProviderResponseRouting> responseReceived) : PipelinePolicy
{
    private readonly string _requestedModelId = requestedModelId;
    private readonly Action<ProviderResponseRouting> _responseReceived = responseReceived;

    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
        ObserveResponse(message);
    }

    public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        ObserveResponse(message);
    }

    private void ObserveResponse(PipelineMessage message)
    {
        if (message.Response is null) return;
        var enabledValue = HeaderValue(message, "x-codex-safety-buffering-enabled");
        var bufferingEnabled = IsEnabled(enabledValue);
        var fasterModel = bufferingEnabled ? HeaderValue(message, "x-codex-safety-buffering-faster-model") : null;
        if (!ProviderModel.ValidId(fasterModel)) fasterModel = null;
        var isFallback = fasterModel is not null &&
            !string.Equals(fasterModel, _requestedModelId, StringComparison.Ordinal);
        _responseReceived(new ProviderResponseRouting(_requestedModelId, fasterModel, isFallback));
    }

    private static string? HeaderValue(PipelineMessage message, string name) =>
        message.Response?.Headers.FirstOrDefault(header =>
            string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static bool IsEnabled(string? value)
    {
        if (value is null) return false;
        value = value.Trim();
        return value.Length > 0 && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase) && value != "0";
    }
}
