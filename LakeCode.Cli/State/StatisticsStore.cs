using LakeCode.Core.Conversations;
using System.ComponentModel;
using LakeCode.Core.Agent;

namespace LakeCode.Cli.State;

/// <summary>Latest reported request usage, retained during generation; not cumulative session usage. Updated on the UI thread.</summary>
internal sealed class StatisticsStore(ModelInfo model) : INotifyPropertyChanged
{
    public ModelInfo Model => Current?.Model ?? model;
    public RequestStatistics? Current { get; private set; }
    public double? TokensPerSecond { get; private set; }
    public bool IsGenerating { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(ConversationState state)
    {
        model = state.StatisticsModel;
        SetCurrent(state.Statistics);
        if (TokensPerSecond != state.TokensPerSecond)
        {
            TokensPerSecond = state.TokensPerSecond;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TokensPerSecond)));
        }
        SetGenerating(state.IsGenerating);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Model)));
    }

    public void SetGenerating(bool isGenerating)
    {
        if (IsGenerating == isGenerating)
            return;

        IsGenerating = isGenerating;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsGenerating)));
    }

    private void SetCurrent(RequestStatistics? statistics)
    {
        if (Current == statistics)
            return;

        Current = statistics;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Current)));
    }
}
