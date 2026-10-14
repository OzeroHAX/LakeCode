using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent;

/// <summary>Captures the last provider request and its partial response, below function invocation.</summary>
internal sealed class InterruptedHistoryChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    private List<ChatMessage>? _request;
    private readonly List<ChatResponseUpdate> _updates = [];
    public Action? BeforeRequest { get; set; }
    public Action<ChatResponse>? ResponseCompleted { get; set; }
    private SideQuestionContext? _sideContext;
    private string? _sideContextError;
    private LakeCode.Core.Providers.ProviderProfile? _provider;
    private ModelInfo? _model;
    private string _effort = "", _cacheSessionId = "";

    internal SideQuestionContext? SideContext => Volatile.Read(ref _sideContext);
    internal string? SideContextError => Volatile.Read(ref _sideContextError);

    internal void ConfigureSideContext(LakeCode.Core.Providers.ProviderProfile provider, ModelInfo model,
        string effort, string cacheSessionId)
    {
        _provider = provider;
        _model = model;
        _effort = effort;
        _cacheSessionId = cacheSessionId;
    }

    internal void ClearSideContext()
    {
        Volatile.Write(ref _sideContext, null);
        Volatile.Write(ref _sideContextError, null);
    }

    private void PublishSideContext(Func<SideQuestionContext> create)
    {
        try
        {
            Volatile.Write(ref _sideContext, create());
            Volatile.Write(ref _sideContextError, null);
        }
        catch (Exception)
        {
            // An unsupported snapshot must never break the main run or trigger a reconstructed fallback.
            Volatile.Write(ref _sideContext, null);
            Volatile.Write(ref _sideContextError, "Не удалось сохранить точный префикс /btw. Основная задача продолжается; побочный запрос не отправлен.");
        }
    }

    public void Reset()
    {
        _request = null;
        _updates.Clear();
    }

    public List<ChatMessage>? GetInterruptedMessages()
    {
        if (_request is null) return null;
        return [.. _request.Where(message => message.Role != ChatRole.System),
            .. _updates.ToChatResponse().Messages];
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _request = messages.Select(message => message.Clone()).ToList();
        _updates.Clear();
        // Capture completed tool results before a boundary stop, so recovery retains them.
        BeforeRequest?.Invoke();
        cancellationToken.ThrowIfCancellationRequested();
        if (_provider is not null && _model is not null && options is not null)
            PublishSideContext(() => new SideQuestionContext(_request, options, _provider, _model, _effort, _cacheSessionId));
        var requestContext = SideContext;
        await foreach (var update in base.GetStreamingResponseAsync(_request, options, cancellationToken))
        {
            _updates.Add(update.Clone());
            yield return update;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (ResponseCompleted is { } completed) completed(_updates.ToChatResponse());
        // A completed final answer is appended, never substituted for the cached request prefix.
        // Tool-call responses remain excluded until MAF supplies their results in the next request.
        if (requestContext is not null)
            PublishSideContext(() =>
            {
                var response = _updates.ToChatResponse();
                return response.Messages.SelectMany(message => message.Contents).OfType<FunctionCallContent>().Any()
                    ? requestContext : requestContext.WithResponse(response);
            });
    }
}
