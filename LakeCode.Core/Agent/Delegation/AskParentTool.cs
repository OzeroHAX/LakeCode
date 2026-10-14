using System.ComponentModel;

namespace LakeCode.Core.Agent.Delegation;

internal sealed class AskParentTool
{
    private int _questionCount;
    public ParentQuestion? Pending { get; private set; }
    public string? Failure { get; private set; }

    [Description("Request a missing decision or context from the parent agent. Ends this run and returns control to the parent; continuation will contain its answer. Use only for information you cannot obtain by reading the project. Call this tool alone, not in a batch.")]
    public string Ask(
        [Description("One specific question for the parent agent.")] string question,
        [Description("Why the answer is needed to proceed.")] string reason)
    {
        if (Pending is not null) return "Error: A parent question is already pending.";
        if (string.IsNullOrWhiteSpace(question) || string.IsNullOrWhiteSpace(reason))
            return "Error: question and reason must be nonempty.";
        if (question.Length > 4000 || reason.Length > 4000)
            return "Error: question and reason must each be at most 4000 characters.";
        if (_questionCount >= 3)
        {
            Failure = "The delegation exhausted its three parent questions.";
            return "Error: " + Failure;
        }
        _questionCount++;
        Pending = new ParentQuestion(Guid.NewGuid().ToString("N"), question, reason);
        return "Control returned to the parent. Do not continue until the next message supplies its answer. Question ID: " + Pending.Id;
    }

    public void AcceptAnswer() => Pending = null;
}
