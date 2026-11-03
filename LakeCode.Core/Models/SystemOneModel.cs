using System.Net.Http.Json;
using System.Text.Json;
using LakeCode.Core.Providers;

namespace LakeCode.Core.Models;

/// <summary>System One choice protocol, independent of any application policy.</summary>
internal sealed class SystemOneModel(ProviderProfile profile, string id, Uri endpoint) : DecisionModel(id)
{
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
        { Timeout = Timeout.InfiniteTimeSpan };
    private const int MaxResponseBytes = 65536;

    public override async Task<DecisionResult> EvaluateAsync(object state,
        IReadOnlyDictionary<string, DecisionQuestion> questions, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        profile.Authorize(request);
        request.Content = JsonContent.Create(new
        {
            model = Id,
            state,
            questions = questions.ToDictionary(item => item.Key, item => new
            {
                type = "choice", instructions = item.Value.Instructions, criteria = item.Value.Choices
            })
        });
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException($"Модель решений недоступна (HTTP {(int)response.StatusCode}).");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int count;
        while ((count = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > MaxResponseBytes) throw new InvalidDataException("Ответ модели решений превышает лимит.");
            buffer.Write(chunk, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        var root = document.RootElement;
        var model = root.GetProperty("model").GetString() ?? throw new JsonException();
        var answers = root.GetProperty("answers");
        if (answers.EnumerateObject().Count() != questions.Count) throw new JsonException();
        var result = new Dictionary<string, DecisionAnswer>(StringComparer.Ordinal);
        foreach (var (key, question) in questions)
        {
            var answer = answers.GetProperty(key);
            if (answer.GetProperty("type").GetString() != "choice") throw new JsonException();
            var choice = answer.GetProperty("choice").GetString() ?? throw new JsonException();
            var probabilities = answer.GetProperty("probabilities");
            if (probabilities.EnumerateObject().Count() != question.Choices.Count || !question.Choices.ContainsKey(choice)) throw new JsonException();
            var values = question.Choices.Keys.ToDictionary(option => option,
                option => Probability(probabilities.GetProperty(option)), StringComparer.Ordinal);
            var confidence = Probability(answer.GetProperty("confidence"));
            if (Math.Abs(values.Values.Sum() - 1) > 0.001 || values[choice] + 0.000001 < values.Values.Max()) throw new JsonException();
            result.Add(key, new DecisionAnswer(choice, values, confidence));
        }
        return new DecisionResult(model, result);
    }

    private static double Probability(JsonElement value)
    {
        var number = value.GetDouble();
        if (!double.IsFinite(number) || number is < 0 or > 1) throw new JsonException();
        return number;
    }
}
