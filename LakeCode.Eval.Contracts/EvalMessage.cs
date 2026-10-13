using System.Text.Json;

namespace LakeCode.Eval.Contracts;

public sealed record EvalMessage(string Kind, string? Code = null, string? Name = null,
    JsonElement? Arguments = null, string? Output = null, bool IsError = false,
    string? ErrorKind = null, string? StateStatus = null, string? Generation = null, long Cell = 0);

public sealed record EvalToolResult(string Output, bool IsError);
