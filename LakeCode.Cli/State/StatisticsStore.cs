using System.ComponentModel;
using LakeCode.Cli.Agent;
using Microsoft.Extensions.AI;

namespace LakeCode.Cli.State;

internal sealed record RequestStatistics(
    ModelInfo Model,
    long? InputTokens,
    long? OutputTokens,
    long? TotalTokens,
    long? CachedInputTokens,
    long? ReasoningTokens);

/// <summary>Latest reported request usage, retained during generation; not cumulative session usage. Updated on the UI thread.</summary>
internal sealed class StatisticsStore(ModelInfo model) : INotifyPropertyChanged
{
    public ModelInfo Model => Current?.Model ?? model;
    public RequestStatistics? Current { get; private set; }
    public double? TokensPerSecond { get; private set; }
    public bool IsGenerating { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetGenerating(bool isGenerating)
    {
        if (IsGenerating == isGenerating)
            return;

        IsGenerating = isGenerating;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGenerating)));
    }

    public void Reset(ModelInfo sessionModel)
    {
        model = sessionModel;
        SetCurrent(null);
        TokensPerSecond = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TokensPerSecond)));
        SetGenerating(false);
    }

    public void Restore(RequestStatistics statistics, double? tokensPerSecond)
    {
        SetCurrent(statistics);
        TokensPerSecond = tokensPerSecond;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TokensPerSecond)));
    }

    public void Update(UsageDetails usage, ModelInfo requestModel)
    {
        SetCurrent(new RequestStatistics(
            requestModel,
            usage.InputTokenCount,
            usage.OutputTokenCount,
            usage.TotalTokenCount,
            usage.CachedInputTokenCount,
            usage.ReasoningTokenCount));
    }

    public void CompleteRequest(long? outputTokens, TimeSpan generationTime)
    {
        TokensPerSecond = outputTokens is { } tokens && generationTime > TimeSpan.Zero
            ? tokens / generationTime.TotalSeconds
            : null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TokensPerSecond)));
    }

    private void SetCurrent(RequestStatistics? statistics)
    {
        if (Current == statistics)
            return;

        Current = statistics;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
    }
}
