using System.Collections.Immutable;
using System.Text;
using LakeCode.Core.Agent;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Conversations.SideQuestions;

/// <summary>Aside transcript and response processing. Mutable state is accessed under the service lock.</summary>
internal sealed class SideQuestionConversation
{
    private const int HistoryCharacters = 128_000;
    private readonly List<SideQuestionExchange> _exchanges = [];
    private long _revision;
    public Guid? ActiveId { get; private set; }

    internal sealed record Request(Guid Id, string Question, SideQuestionContext Context,
        List<(string Question, string Answer)> History);
    internal sealed record Progress(Guid Id, string Answer, string Status, string? Error, RequestStatistics? Statistics);

    public static void ValidateQuestion(string question)
    {
        if (string.IsNullOrWhiteSpace(question)) throw new ArgumentException("Используйте /btw вопрос.");
        if (question.Length > 16_000)
            throw new ArgumentException("Попутный вопрос слишком длинный (максимум 16 000 символов); черновик сохранён.");
    }

    public Request Prepare(SideQuestionContext context, string question)
    {
        var history = _exchanges.Where(exchange => exchange.Status == "completed")
            .Select(exchange => (exchange.Question, exchange.Answer)).ToList();
        // Drop old asides, never alter the exact inherited prefix.
        if (context.InputTokens is { } input)
        {
            var available = Math.Min(context.Model.MaxInputTokens,
                context.Model.ContextWindowTokens - context.Model.MaxOutputTokens) - input;
            long TailSize() => Encoding.UTF8.GetByteCount(SideQuestionRunner.Boundary + question) + 64L +
                history.Sum(exchange => (long)Encoding.UTF8.GetByteCount(exchange.Question + exchange.Answer) + 32);
            while (history.Count > 0 && TailSize() > available) history.RemoveAt(0);
            if (TailSize() > available)
                throw new InvalidDataException("В контексте недостаточно места для /btw. Основная история не обрезана, запрос не отправлен; черновик сохранён.");
        }
        return new Request(Guid.NewGuid(), question, context, history);
    }

    public void Begin(Request request)
    {
        ActiveId = request.Id;
        _exchanges.Add(new SideQuestionExchange(request.Id, request.Question, "", request.Context.Provider.Name,
            request.Context.Model, request.Context.ReasoningEffort, "in_progress", null, null));
        Trim();
    }

    public void Clear()
    {
        ActiveId = null;
        _exchanges.Clear();
    }

    public bool Apply(Progress progress)
    {
        var index = _exchanges.FindIndex(exchange => exchange.RequestId == progress.Id);
        if (index < 0 || (progress.Status == "in_progress" && ActiveId != progress.Id)) return false;
        _exchanges[index] = _exchanges[index] with
        { Answer = progress.Answer, Status = progress.Status, Error = progress.Error, Statistics = progress.Statistics };
        Trim();
        return true;
    }

    public bool Complete(Progress progress)
    {
        if (ActiveId != progress.Id) return false;
        ActiveId = null;
        return Apply(progress);
    }

    public void AdvanceRevision() => _revision++;
    public SideQuestionState State(bool hasOperation) => new(_revision, ActiveId is not null && hasOperation, _exchanges.ToImmutableArray());

    private void Trim()
    {
        while (_exchanges.Count > 1 && (_exchanges.Count > 20 ||
               _exchanges.Sum(exchange => (long)exchange.Question.Length + exchange.Answer.Length) > HistoryCharacters))
            _exchanges.RemoveAt(0);
    }

    public static async Task<Progress> RunAsync(Request request, Action<Progress> report,
        CancellationToken cancellationToken, CancellationToken shutdown)
    {
        var answer = new StringBuilder();
        RequestStatistics? statistics = null;
        var status = "completed";
        string? error = null;
        var attemptedTool = false;
        try
        {
            await foreach (var update in SideQuestionRunner.RunAsync(request.Context, request.History,
                               request.Question, cancellationToken).ConfigureAwait(false))
            {
                var changed = false;
                foreach (var content in update.Contents)
                {
                    if (content is FunctionCallContent) attemptedTool = true;
                    else if (content is TextContent text && !string.IsNullOrEmpty(text.Text))
                    {
                        if ((long)answer.Length + text.Text.Length + request.Question.Length > HistoryCharacters)
                            throw new InvalidDataException("Ответ /btw превысил лимит 128 000 символов. Полученный текст сохранён.");
                        answer.Append(text.Text);
                        changed = true;
                    }
                    else if (content is UsageContent usage)
                    {
                        var details = usage.Details;
                        statistics = new RequestStatistics(request.Context.Model,
                            details.InputTokenCount ?? statistics?.InputTokens,
                            details.OutputTokenCount ?? statistics?.OutputTokens,
                            details.TotalTokenCount ?? statistics?.TotalTokens,
                            details.CachedInputTokenCount ?? statistics?.CachedInputTokens,
                            details.ReasoningTokenCount ?? statistics?.ReasoningTokens);
                        changed = true;
                    }
                }
                if (changed) report(new Progress(request.Id, answer.ToString(), "in_progress", null, statistics));
            }
            if (attemptedTool)
            {
                status = "failed";
                error = "Модель запросила инструмент. Ничего не выполнено: /btw работает только с имеющимся контекстом.";
            }
            else if (answer.Length == 0)
            {
                status = "failed";
                error = "Модель завершила попутный запрос без текстового ответа.";
            }
        }
        catch (OperationCanceledException)
        {
            status = "cancelled";
            error = shutdown.IsCancellationRequested ? "Попутный запрос отменён при выходе." :
                "Попутный запрос отменён или превышено время ожидания (2 минуты). Основная задача не остановлена.";
        }
        catch (Exception failure) { status = "failed"; error = failure.Message; }
        return new Progress(request.Id, answer.ToString(), status, error, statistics);
    }
}
