namespace LakeCode.Core.Models;

/// <summary>A model that returns structured choices for named questions about a state.</summary>
public abstract class DecisionModel(string id)
{
    public string Id { get; } = id;
    public abstract Task<DecisionResult> EvaluateAsync(object state,
        IReadOnlyDictionary<string, DecisionQuestion> questions, CancellationToken cancellationToken);
}

public sealed record DecisionQuestion(string Instructions, IReadOnlyDictionary<string, string> Choices);
public sealed record DecisionAnswer(string Choice, IReadOnlyDictionary<string, double> Probabilities, double Confidence);
public sealed record DecisionResult(string Model, IReadOnlyDictionary<string, DecisionAnswer> Answers);
public sealed record DecisionModelInfo(string Id, bool IsSelectable);
