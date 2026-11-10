using System.Text.Json;

namespace LakeCode.Agent.Tools;

/// <summary>Malformed or missing tool argument supplied by the model.</summary>
public sealed class ToolArgumentException(string message) : Exception(message);

/// <summary>
/// Strict reader for model-supplied JSON arguments. A value of the wrong type fails instead of falling
/// back to a default, so a malformed call never silently becomes a different operation.
/// </summary>
public readonly struct ToolArguments
{
    private readonly JsonElement _value;

    public ToolArguments(JsonElement value) => _value = value;

    /// <summary>Present means a non-null JSON property, so explicit null is treated as absent.</summary>
    public bool TryGet(string name, out JsonElement value)
    {
        value = default;
        return _value.ValueKind == JsonValueKind.Object && _value.TryGetProperty(name, out value) &&
               value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
    }

    public string RequireString(string name, bool allowEmpty = false)
    {
        if (!TryGet(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            throw new ToolArgumentException($"'{name}' is required and must be a string.");
        }

        var text = value.GetString() ?? string.Empty;
        if (!allowEmpty && string.IsNullOrWhiteSpace(text))
        {
            throw new ToolArgumentException($"'{name}' must not be empty.");
        }

        return text;
    }

    public string String(string name, string fallback)
    {
        if (!TryGet(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ToolArgumentException($"'{name}' must be a string.");
        }

        return value.GetString() ?? fallback;
    }

    public string? OptionalString(string name)
    {
        if (!TryGet(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new ToolArgumentException($"'{name}' must be a string.");
        }

        return value.GetString();
    }

    public int Integer(string name, int fallback)
    {
        if (!TryGet(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            throw new ToolArgumentException($"'{name}' must be an integer.");
        }

        return number;
    }

    public bool Boolean(string name, bool fallback)
    {
        if (!TryGet(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new ToolArgumentException($"'{name}' must be a boolean.");
        }

        return value.GetBoolean();
    }

    public string[]? OptionalStringArray(string name)
    {
        if (!TryGet(name, out var value))
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new ToolArgumentException($"'{name}' must be an array of strings.");
        }

        var items = new List<string>();

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                throw new ToolArgumentException($"'{name}' must contain only strings.");
            }

            items.Add(item.GetString() ?? string.Empty);
        }

        return [.. items];
    }
}
