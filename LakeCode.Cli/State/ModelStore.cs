using System.Collections.Frozen;
using System.ComponentModel;
using LakeCode.Core.Agent;
using LakeCode.Core.Providers;

namespace LakeCode.Cli.State;

/// <summary>Available UI models and the current selection; changes are made on the UI thread.</summary>
internal sealed class ModelStore : INotifyPropertyChanged
{
    public ProviderRegistry Providers { get; }
    public string SelectedProviderId { get; private set; }
    public string SelectedProviderName => Providers.Profiles.FirstOrDefault(profile => profile.Id == SelectedProviderId)?.Name ?? "Нет подключения";
    public bool HasSelection => Providers.Profiles.Any(profile => profile.Id == SelectedProviderId && profile.Models.Any(model => model.Id == SelectedModel.Id));
    public FrozenDictionary<string, ModelInfo> Models => Providers.Profiles.FirstOrDefault(profile => profile.Id == SelectedProviderId)?.Models
        .ToFrozenDictionary(model => model.Id, StringComparer.Ordinal) ?? FrozenDictionary<string, ModelInfo>.Empty;
    public ModelInfo SelectedModel { get; private set; }
    public string SelectedReasoningEffort { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ModelStore(ProviderRegistry providers)
    {
        Providers = providers;
        var profile = providers.Profiles.FirstOrDefault(profile => !profile.Models.IsEmpty);
        SelectedProviderId = profile?.Id ?? "";
        SelectedModel = profile?.Models.FirstOrDefault(model => model.Id == "gpt-6.1-sol")
            ?? profile?.Models.FirstOrDefault() ?? ModelCatalog.Models["gpt-6.1-sol"];
        SelectedReasoningEffort = SelectedModel.ReasoningEfforts.Contains("medium") ? "medium" : SelectedModel.ReasoningEfforts[0];
    }

    public void SelectModel(string providerId, string modelId)
    {
        var model = Providers.GetModel(providerId, modelId);
        SelectedProviderId = providerId;
        SelectedModel = model;
        if (!model.ReasoningEfforts.Contains(SelectedReasoningEffort)) SelectedReasoningEffort = model.ReasoningEfforts[0];
        NotifySelection();
    }

    public void Refresh()
    {
        if (HasSelection) SelectModel(SelectedProviderId, SelectedModel.Id);
        else if (Providers.Profiles.FirstOrDefault(profile => !profile.Models.IsEmpty) is { } profile)
            SelectModel(profile.Id, profile.Models[0].Id);
        else { SelectedProviderId = ""; NotifySelection(); }
    }

    private void NotifySelection()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProviderName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedProviderId)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedModel)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedReasoningEffort)));
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
