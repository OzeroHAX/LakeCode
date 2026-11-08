using System.Text.Json;

namespace LakeCode.Agent.Providers;

public static class ProviderConfig
{
    public static T Load<T>()
        where T : class
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".lakecode",
            "providers.json");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var name = typeof(T).Name;

        if (!document.RootElement.TryGetProperty(name, out var section))
        {
            throw new InvalidOperationException(
                $"Provider configuration '{name}' was not found.");
        }

        return JsonSerializer.Deserialize<T>(section.GetRawText())
            ?? throw new InvalidDataException(
                $"Provider configuration '{name}' is invalid.");
    }
}
