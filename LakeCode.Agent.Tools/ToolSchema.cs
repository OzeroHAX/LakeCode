using System.Text.Json;
using System.Text.Json.Nodes;

namespace LakeCode.Agent.Tools;

/// <summary>
/// Builds the JSON Schema a model sees for one tool. Kept dependency-free so LakeCode.Agent.Tools does
/// not pull a provider or MAF package just to describe parameters.
/// </summary>
public sealed class ToolSchema
{
    private readonly JsonObject _properties = new();
    private readonly JsonArray _required = [];

    private ToolSchema()
    {
    }

    public static JsonElement Object(Action<ToolSchema> define)
    {
        ArgumentNullException.ThrowIfNull(define);

        var schema = new ToolSchema();
        define(schema);

        var root = new JsonObject { ["type"] = "object", ["properties"] = schema._properties };

        if (schema._required.Count > 0)
        {
            root["required"] = schema._required;
        }

        // Parse into a detached element: the node tree stays mutable after a tool exposes its schema.
        using var document = JsonDocument.Parse(root.ToJsonString());
        return document.RootElement.Clone();
    }

    public ToolSchema Text(string name, string description, string? defaultValue = null, bool required = true)
    {
        var property = new JsonObject { ["type"] = "string", ["description"] = description };

        if (defaultValue is not null)
        {
            property["default"] = defaultValue;
        }

        return Add(name, property, required);
    }

    public ToolSchema Integer(
        string name,
        string description,
        int? minimum = null,
        int? maximum = null,
        int? defaultValue = null,
        bool required = false)
    {
        var property = new JsonObject { ["type"] = "integer", ["description"] = description };

        if (minimum is not null)
        {
            property["minimum"] = minimum.Value;
        }

        if (maximum is not null)
        {
            property["maximum"] = maximum.Value;
        }

        if (defaultValue is not null)
        {
            property["default"] = defaultValue.Value;
        }

        return Add(name, property, required);
    }

    public ToolSchema Boolean(string name, string description, bool? defaultValue = null, bool required = false)
    {
        var property = new JsonObject { ["type"] = "boolean", ["description"] = description };

        if (defaultValue is not null)
        {
            property["default"] = defaultValue.Value;
        }

        return Add(name, property, required);
    }

    public ToolSchema TextArray(
        string name,
        string description,
        int? maxItems = null,
        bool required = false)
    {
        var property = new JsonObject
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = new JsonObject { ["type"] = "string" },
        };

        if (maxItems is not null)
        {
            property["maxItems"] = maxItems.Value;
        }

        return Add(name, property, required);
    }

    /// <summary>Array of objects defined by their own schema, used for structured payloads.</summary>
    public ToolSchema ObjectArray(
        string name,
        string description,
        Action<ToolSchema> items,
        int? maxItems = null,
        int? minItems = null,
        bool required = true)
    {
        ArgumentNullException.ThrowIfNull(items);

        var itemSchema = new ToolSchema();
        items(itemSchema);

        var itemRoot = new JsonObject { ["type"] = "object", ["properties"] = itemSchema._properties };

        if (itemSchema._required.Count > 0)
        {
            itemRoot["required"] = itemSchema._required;
        }

        var property = new JsonObject
        {
            ["type"] = "array",
            ["description"] = description,
            ["items"] = itemRoot,
        };

        if (maxItems is not null)
        {
            property["maxItems"] = maxItems.Value;
        }

        if (minItems is not null)
        {
            property["minItems"] = minItems.Value;
        }

        return Add(name, property, required);
    }

    private ToolSchema Add(string name, JsonObject property, bool required)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        _properties[name] = property;

        if (required)
        {
            _required.Add(name);
        }

        return this;
    }
}
