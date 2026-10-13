using System.Text.Json;
using LakeCode.Eval.Contracts;
using LakeCode.Eval.Worker.Output;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace LakeCode.Eval.Worker;

internal sealed class EvalSession(EvalGlobals globals, BoundedOutput output)
{
    private readonly ScriptOptions _options = ScriptOptions.Default
        .AddReferences(typeof(JsonSerializer).Assembly, typeof(EvalGlobals).Assembly,
            typeof(Enumerable).Assembly, typeof(System.Text.Json.Nodes.JsonNode).Assembly)
        .AddImports("System", "System.IO", "System.Linq", "System.Collections.Generic",
            "System.Threading.Tasks", "System.Text.Json", "System.Text.Json.Nodes");
    private readonly string _generation = Guid.NewGuid().ToString("N");
    private ScriptState<object>? _state;
    private long _cell;

    public async Task<EvalMessage> ExecuteAsync(string code)
    {
        output.Clear();
        _cell++;
        string? errorKind = null;
        var stateStatus = "preserved";
        try
        {
            // Capture runtime exceptions in ScriptState so the executed submission remains
            // in the continuation chain, including declarations made before the exception.
            _state = _state is null
                ? await CSharpScript.Create<object>(code, _options, typeof(EvalGlobals))
                    .RunAsync(globals, catchException: _ => true)
                : await _state.ContinueWithAsync(code, _options, catchException: _ => true);
            if (_state.Exception is { } runtimeError)
            {
                errorKind = "runtime";
                stateStatus = "partially-mutated";
                output.WriteLine($"{runtimeError.GetType().Name}: {runtimeError.Message}");
            }
            else if (_state.ReturnValue is { } value) globals.Display(value);
        }
        catch (CompilationErrorException error)
        {
            errorKind = "compilation";
            output.WriteLine(string.Join(Environment.NewLine, error.Diagnostics));
        }
        catch (Exception error)
        {
            errorKind = "runtime";
            stateStatus = "partially-mutated";
            output.WriteLine($"{error.GetType().Name}: {error.Message}");
        }

        return new EvalMessage("result", Output: output.ToString(),
            IsError: errorKind is not null, ErrorKind: errorKind, StateStatus: stateStatus,
            Generation: _generation, Cell: _cell);
    }
}
