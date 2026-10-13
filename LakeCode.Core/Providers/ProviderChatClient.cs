using System.Runtime.CompilerServices;
using LakeCode.Core.Agent;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Providers;

/// <summary>Changes transport only between runs; the MAF agent, tools and workspace permissions stay alive.</summary>
internal sealed class ProviderChatClient : IChatClient
{
    private IChatClient? _client;
    private ProviderProfile? _profile;
    private string? _modelId;

    public bool Configure(ProviderProfile profile, ModelInfo model, string sessionId)
    {
        if (ReferenceEquals(profile, _profile) && model.Id == _modelId) return false;
        var next = ProviderClientFactory.Create(profile, model, sessionId);
        _client?.Dispose();
        _client = next;
        _profile = profile;
        _modelId = model.Id;
        return true;
    }

    private IChatClient Client => _client ?? throw new InvalidOperationException("Подключение ещё не выбрано.");
    private Exception Sanitize(Exception error) => new InvalidOperationException(
        string.IsNullOrEmpty(_profile?.ApiKey) ? error.Message : error.Message.Replace(_profile.ApiKey, "[redacted]", StringComparison.Ordinal));

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        try { return await Client.GetResponseAsync(messages, options, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { throw Sanitize(error); }
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var stream = Client.GetStreamingResponseAsync(messages, options, cancellationToken).GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool hasNext;
            try { hasNext = await stream.MoveNextAsync(); }
            catch (OperationCanceledException) { throw; }
            catch (Exception error) { throw Sanitize(error); }
            if (!hasNext) yield break;
            yield return stream.Current;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType == typeof(IChatClient) ? this : _client?.GetService(serviceType, serviceKey);
    public void Dispose() { _client?.Dispose(); _client = null; _profile = null; }
}
