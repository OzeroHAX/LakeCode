using System.Collections.Frozen;
using System.ComponentModel;
using LakeCode.Cli.Agent;

namespace LakeCode.Cli.State;

/// <summary>Available UI models and the current selection; changes are made on the UI thread.</summary>
internal sealed class ModelStore : INotifyPropertyChanged
{
    public FrozenDictionary<string, ModelInfo> Models { get; }
    public ModelInfo SelectedModel { get; private set; }
    public string SelectedReasoningEffort { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ModelStore(FrozenDictionary<string, ModelInfo> models, string selectedModelId)
    {
        Models = models;
        SelectedModel = Models[selectedModelId];
        SelectedReasoningEffort = SelectedModel.ReasoningEfforts.Contains("medium")
            ? "medium"
            : SelectedModel.ReasoningEfforts[0];
    }

    public void SelectModel(string modelId)
    {
        var model = Models[modelId];
        if (SelectedModel == model)
            return;

        SelectedModel = model;
        if (!model.ReasoningEfforts.Contains(SelectedReasoningEffort))
        {
            SelectedReasoningEffort = model.ReasoningEfforts[0];
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedReasoningEffort)));
        }
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedModel)));
    }

    public void SelectReasoningEffort(string effort)
    {
        if (!SelectedModel.ReasoningEfforts.Contains(effort))
            throw new ArgumentException("Модель не поддерживает сохранённый уровень рассуждения.", nameof(effort));
        SelectedReasoningEffort = effort;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedReasoningEffort)));
    }

    public void CycleReasoningEffort()
    {
        var efforts = SelectedModel.ReasoningEfforts;
        var index = efforts.IndexOf(SelectedReasoningEffort);
        SelectedReasoningEffort = efforts[(index + 1) % efforts.Length];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedReasoningEffort)));
    }
}
