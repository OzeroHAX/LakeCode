using System.Text.Json;
using LakeCode.Core.Providers;
using Microsoft.Extensions.AI;

namespace LakeCode.Core.Agent;

/// <summary>A private, frozen copy of a prepared MAF request, not a reconstruction of the transcript.</summary>
internal sealed class SideQuestionContext
{
    private readonly ChatMessage[] _messages;
    private readonly ChatOptions _options;
    internal ProviderProfile Provider { get; }
    internal ModelInfo Model { get; }
    internal string ReasoningEffort { get; }
    internal string CacheSessionId { get; }
    internal long? InputTokens { get; }

    internal SideQuestionContext(IEnumerable<ChatMessage> messages, ChatOptions options,
        ProviderProfile provider, ModelInfo model, string effort, string cacheSessionId, long? inputTokens = null)
    {
        // Never attach an aside to provider-managed conversation state or a background continuation.
        if (!string.IsNullOrEmpty(options.ConversationId) || options.ContinuationToken is not null || options.AllowBackgroundResponses == true)
            throw new InvalidOperationException("/btw недоступен для серверного продолжения беседы.");
        // Only local function tools are safe to advertise without invoking. Hosted tools can execute remotely.
        if (options.Tools?.Any(tool => tool is not AIFunctionDeclaration) == true)
            throw new InvalidOperationException("/btw недоступен: запрос содержит серверные инструменты.");
        _messages = messages.Select(FreezeMessage).ToArray();
        _options = options.Clone();
        _options.AdditionalProperties = FreezeProperties(options.AdditionalProperties);
        Provider = provider;
        Model = model;
        ReasoningEffort = effort;
        CacheSessionId = cacheSessionId;
        InputTokens = inputTokens;
    }

    internal List<ChatMessage> CopyMessages() => _messages.Select(FreezeMessage).ToList();
    internal ChatOptions CopyOptions()
    {
        var copy = _options.Clone();
        copy.AdditionalProperties = FreezeProperties(_options.AdditionalProperties);
        return copy;
    }

    internal SideQuestionContext WithResponse(ChatResponse response)
    {
        // The appended answer consumes input space too; output includes reasoning and is conservative here.
        long? inputTokens = response.Usage is { InputTokenCount: { } input, OutputTokenCount: { } output }
            ? checked(input + output) : null;
        return new SideQuestionContext(_messages.Concat(response.Messages), _options, Provider, Model,
            ReasoningEffort, CacheSessionId, inputTokens);
    }

    private static ChatMessage FreezeMessage(ChatMessage source)
    {
        var copy = source.Clone();
        copy.Contents = source.Contents.Select(FreezeContent).ToList();
        copy.AdditionalProperties = FreezeProperties(source.AdditionalProperties);
        return copy;
    }

    private static AIContent FreezeContent(AIContent source)
    {
        // Preserve typed content (including encrypted reasoning) and private adapter representations.
        // JSON deep-copies mutable arguments/results; raw SDK representations are never modified here.
        var serialized = JsonSerializer.Serialize<AIContent>(source);
        var copy = JsonSerializer.Deserialize<AIContent>(serialized)
            ?? throw new InvalidDataException("Не удалось зафиксировать контекст /btw.");
        if (copy.GetType() != source.GetType())
            throw new InvalidDataException("Неподдерживаемое содержимое контекста /btw.");
        copy.RawRepresentation = source.RawRepresentation;
        copy.AdditionalProperties = FreezeProperties(source.AdditionalProperties);
        // Untyped JSON deserialization turns even a string result into JsonElement. Some adapters
        // serialize that differently, so retain the original CLR type as well as its value.
        if (source is FunctionResultContent result && copy is FunctionResultContent resultCopy)
            resultCopy.Result = FreezeValue(result.Result);
        if (source is FunctionCallContent call && copy is FunctionCallContent callCopy)
            callCopy.Arguments = call.Arguments?.ToDictionary(pair => pair.Key, pair => FreezeValue(pair.Value));
        if (!string.Equals(serialized, JsonSerializer.Serialize<AIContent>(copy), StringComparison.Ordinal))
            throw new InvalidDataException("Копирование изменило содержимое префикса /btw; запрос не будет отправлен.");
        return copy;
    }

    private static AdditionalPropertiesDictionary? FreezeProperties(AdditionalPropertiesDictionary? source)
    {
        if (source is null) return null;
        var copy = new AdditionalPropertiesDictionary();
        foreach (var pair in source) copy.Add(pair.Key, FreezeValue(pair.Value));
        return copy;
    }

    private static object? FreezeValue(object? value)
    {
        if (value is null || value is string || value.GetType().IsPrimitive || value is decimal) return value;
        if (value is JsonElement json) return json.Clone();
        // Preserve runtime types rather than replacing provider-specific metadata with untyped JSON.
        var type = value.GetType();
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(value, type), type)
            ?? throw new InvalidDataException("Не удалось скопировать метаданные контекста /btw.");
    }
}
