using System.Collections.Immutable;
using LakeCode.Core.Agent.Tools.Ask;

namespace LakeCode.Core.Conversations.Interactions;

/// <summary>Question registrations. Access is serialized by ConversationService; waits happen outside its lock.</summary>
internal sealed class ConversationQuestions
{
    private readonly Dictionary<Guid, PendingInteraction<string[][]>> _pending = [];
    public int Count => _pending.Count;

    public QuestionRequested Register(PendingInteraction<string[][]> pending, AskQuestion[] questions)
    {
        _pending.Add(pending.Id, pending);
        return new QuestionRequested(pending.Id,
            questions.Select(question => question with { Options = question.Options.ToArray() }).ToImmutableArray());
    }

    public bool IsPending(Guid id) => _pending.TryGetValue(id, out var pending) && !pending.IsCompleted;
    public void Remove(Guid id) => _pending.Remove(id);

    public bool Answer(AnswerQuestion answer)
    {
        if (!_pending.TryGetValue(answer.InteractionId, out var pending) || pending.IsCompleted) return false;
        return answer.Answers is null ? pending.Cancel()
            : pending.Resolve(answer.Answers.Select(values => values.ToArray()).ToArray());
    }

    public bool Fail(Guid id, Exception error) => _pending.TryGetValue(id, out var pending) && pending.Fail(error);
}
