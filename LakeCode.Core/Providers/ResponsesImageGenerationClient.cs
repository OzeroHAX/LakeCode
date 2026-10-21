using System.Net;
using System.Net.Http.Headers;
using System.Net.ServerSentEvents;
using System.Text;
using System.Text.Json;
using LakeCode.Core.Attachments;
using LakeCode.Core.Settings;

namespace LakeCode.Core.Providers;

internal sealed record ImageGenerationResult(ImageData Image, string? ReportedModel);

/// <summary>One isolated Responses request. No agent history, routing fallback or automatic retries.</summary>
internal static class ResponsesImageGenerationClient
{
    private const int MaxResponseBytes = 48 * 1024 * 1024;
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    }) { Timeout = Timeout.InfiniteTimeSpan };

    public static async Task<ImageGenerationResult> GenerateAsync(ImageGenerationConfiguration configuration,
        string prompt, IReadOnlyList<ImageData> inputs, string sessionId, CancellationToken cancellationToken)
    {
        var selection = configuration.Selection ?? throw new InvalidOperationException("Генерация не настроена.");
        var provider = configuration.Provider ?? throw new InvalidOperationException("Подключение изображений недоступно.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            var content = new List<object> { new { type = "input_text", text = prompt } };
            foreach (var image in inputs)
                content.Add(new { type = "input_image", detail = "auto", image_url = $"data:{image.MediaType};base64,{Convert.ToBase64String(image.Data.Span)}" });
            var body = new
            {
                model = selection.CarrierModelId,
                instructions = "Generate or edit the image requested by the user using the image_generation tool.",
                input = new[] { new { role = "user", content } },
                tools = new[] { new { type = "image_generation", model = selection.ImageModelId,
                    action = inputs.Count == 0 ? "generate" : "edit", output_format = "png" } },
                tool_choice = new { type = "image_generation" }, store = false, stream = true
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, provider.BaseUrl.TrimEnd('/') + "/responses");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", provider.ApiKey);
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Add("session-id", sessionId);
            request.Headers.Add("thread-id", sessionId);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (!response.IsSuccessStatusCode)
                throw new IOException($"Image API: HTTP {(int)response.StatusCode}. Запрос не повторялся; проверьте подключение и поддержку image_generation.");
            if (response.Content.Headers.ContentLength > MaxResponseBytes)
                throw new IOException("Ответ генерации превышает допустимый размер.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            await using var limited = new LimitedReadStream(stream);
            JsonElement? completed = null;
            var doneImages = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (response.Content.Headers.ContentType?.MediaType == "text/event-stream")
            {
                await foreach (var item in SseParser.Create(limited).EnumerateAsync(deadline.Token))
                {
                    if (string.IsNullOrWhiteSpace(item.Data)) continue;
                    if (item.Data.Trim() == "[DONE]") break;
                    using var document = JsonDocument.Parse(item.Data);
                    var root = document.RootElement;
                    switch (Text(root, "type"))
                    {
                        case "error":
                        case "response.failed":
                        case "response.incomplete":
                            throw new IOException("Сервер сообщил ошибку или незавершённую генерацию. Запрос не повторялся.");
                        case "response.output_item.done":
                            if (root.TryGetProperty("item", out var output) && Text(output, "type") == "image_generation_call")
                            {
                                var id = Text(output, "id") ?? throw new InvalidDataException("В ответе отсутствует ID изображения.");
                                if (doneImages.Count >= 2 && !doneImages.ContainsKey(id))
                                    throw new InvalidDataException("Сервер вернул несколько изображений вместо одного.");
                                doneImages[id] = output.Clone();
                            }
                            break;
                        case "response.completed":
                        case "response.done":
                            if (!root.TryGetProperty("response", out var value))
                                throw new InvalidDataException("Некорректное завершение Responses.");
                            completed = value.Clone();
                            break;
                    }
                }
            }
            else if (response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                using var document = await JsonDocument.ParseAsync(limited, cancellationToken: deadline.Token);
                completed = document.RootElement.Clone();
            }
            else throw new InvalidDataException("Неподдерживаемый формат ответа Image API.");

            if (completed is not { } result || Text(result, "status") != "completed" ||
                (result.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null))
                throw new IOException("Генерация не подтверждена завершённым ответом. Запрос мог потратить квоту; автоматически не повторять.");
            // Final output is authoritative. Item-done is used only when the final envelope omits output.
            var hasOutput = result.TryGetProperty("output", out var outputs);
            if (hasOutput && outputs.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Некорректный output в завершённом ответе Image API.");
            var images = hasOutput
                ? outputs.EnumerateArray().Where(o => Text(o, "type") == "image_generation_call").ToArray()
                : doneImages.Values.ToArray();
            if (images.Length == 0)
            {
                var refused = outputs.ValueKind == JsonValueKind.Array && outputs.EnumerateArray().Any(o =>
                    o.TryGetProperty("content", out var parts) && parts.ValueKind == JsonValueKind.Array &&
                    parts.EnumerateArray().Any(part => Text(part, "type") == "refusal"));
                throw new IOException(refused ? "Сервер отказал в генерации изображения." : "Сервер завершил ответ без изображения.");
            }
            if (images.Length != 1) throw new InvalidDataException("Сервер вернул несколько изображений вместо одного; файл не сохранён.");
            if (Text(images[0], "status") != "completed") throw new InvalidDataException("Изображение не завершено.");
            var encoded = Text(images[0], "result") ?? throw new InvalidDataException("В ответе отсутствуют байты изображения.");
            if (encoded.Length > ((ImageAttachments.MaxImageBytes + 2) / 3) * 4)
                throw new InvalidDataException("Изображение превышает допустимый размер.");
            var bytes = Convert.FromBase64String(encoded);
            deadline.Token.ThrowIfCancellationRequested();
            if (!bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                throw new InvalidDataException("Сервер не вернул запрошенный PNG. Файл не сохранён.");
            var imageData = ImageAttachments.Decode(bytes, "generated.png");
            deadline.Token.ThrowIfCancellationRequested();
            string? reported = null;
            if (result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
                reported = tools.EnumerateArray().Where(t => Text(t, "type") == "image_generation")
                    .Select(t => Text(t, "model")).FirstOrDefault();
            // Only a bounded public model identifier is exposed, never the raw response.
            if (!ProviderProfile.ValidModelId(reported) ||
                reported!.Contains(provider.ApiKey, StringComparison.Ordinal)) reported = null;
            return new(imageData, reported);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Таймаут генерации. Сервер мог выполнить запрос; автоматического повтора нет.");
        }
        catch (HttpRequestException)
        {
            throw new IOException("Сетевая ошибка Image API. Сервер мог выполнить запрос; автоматического повтора нет.");
        }
        catch (Exception error) when (error is JsonException or FormatException)
        {
            throw new InvalidDataException("Некорректный JSON или base64 в ответе Image API. Запрос не повторялся.");
        }
    }

    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    // Bounds decompressed bytes before the SSE parser can allocate an unbounded event.
    private sealed class LimitedReadStream(Stream inner) : Stream
    {
        private long _read;
        private int Count(int count)
        {
            _read += count;
            if (_read > MaxResponseBytes) throw new IOException("Ответ генерации превышает допустимый размер.");
            return count;
        }
        private int Allowance(int count) => (int)Math.Min(count, MaxResponseBytes - _read + 1);
        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, Allowance(count)));
        public override int Read(Span<byte> buffer) => Count(inner.Read(buffer[..Allowance(buffer.Length)]));
        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Count(await inner.ReadAsync(buffer.AsMemory(offset, Allowance(count)), cancellationToken));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer[..Allowance(buffer.Length)], cancellationToken));
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
