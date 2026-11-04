# LakeCode.Search

Standalone .NET 10 library for index-free, structure-aware code retrieval. No dependency on LakeCode.Core, CLI, MAF, an LSP server, ripgrep executable, embeddings, or an agent framework. No automatic network access, package provisioning, cache or project index at runtime.

## Public API

- `GrepAsync(GrepRequest, CancellationToken)` — exact literal or per-line .NET regex search, grouped by file, with enclosing syntax units and file outlines. Every matching line is retained within configured budgets.
- `FindAsync(FindRequest, CancellationToken)` — ranked file discovery by normalized basename/path terms. Exact basename, basename substring and path substring have distinct, explained weights. Returns bounded top-k candidates with outlines. Not typo correction or semantic search.
- `OutlineAsync(path, language, CancellationToken)` — named syntax structure of a known file; optional explicit grammar ID. Unknown extensions return `Language = null` and an empty structure; an explicitly invalid grammar throws. `HasSyntaxErrors` reports parser recovery, not a compilation diagnostic.
- `TraceAsync(TraceRequest, CancellationToken)` — lexical investigation using a subject, typed relation and support terms. Ranks files, extracts enclosing small units or context snippets, merges overlapping/adjacent regions, and explains scores. All normalized subject terms must occur on the same line. Relations add local lexical clues, not proof of a resolved call or implementation.
- `FindDuplicatesAsync(DuplicateRequest, CancellationToken)` — index-free structural duplicate detection for generic code units. It supports callable, type, declaration, block and any units across the registered grammars, with exact or whitespace-normalized comparison. Cross-language grouping is opt-in and explicitly heuristic; this operation does not claim semantic equivalence.
- `SearchEngine.Grammars` — full grammar/extension inventory.

Results are ordinary public records, suitable for `System.Text.Json`. This project intentionally does not register agent tools or choose tool names/prompts. The host can wrap each operation with its framework's tool adapter.

## Hosting and access policy

`ISearchSource` is the access boundary. The host implements:

1. `EnumerateAsync(token)` — yield unique relative logical paths with `/` separators (not absolute filesystem paths). Enumeration itself must enforce directory access, ignore rules and traversal budgets as appropriate. The engine applies additional include/exclude globs to these identifiers.
2. `OpenReadAsync(path, token)` — authorize and return a readable stream. The operation owns and disposes this stream. This method must independently validate direct outline requests, not assume paths came from enumeration.

There is no fallback to direct disk reads when a host denies access. Implementations must honor cancellation and must not return duplicate paths. When sharing an engine between concurrent calls, the source and language selector must support concurrent calls. Do not mutate request collections during submission.

`DirectorySearchSource` is an optional disk implementation. It requires an authorization callback for every directory enumeration and every file opening. The callback receives an absolute path and access type and must throw on denial. This adapter confines access to its root and rejects symbolic links in path components. It skips links encountered during enumeration. It does not interpret `.gitignore` or exclude hidden/build directories automatically: supply `SearchFilter` globs, deny directory access in host policy, or implement a different source. A directory denial or enumeration error fails the operation rather than pretending traversal was complete.

The disk adapter is not a sandbox against concurrent filesystem replacement: path validation and opening have a TOCTOU window, as with ordinary managed path checks. For hostile mutable filesystems, provide a host source with OS-level secure opening semantics. Other policies, external permissions, virtual files or link following belong in a custom source.

## Example

Reference the project (or package it and use a PackageReference); this preserves transitive managed and native assets:

```xml
<ProjectReference Include="../LakeCode.Search/LakeCode.Search.csproj" />
```

The following host explicitly allows reading everything inside the selected root under the adapter's no-links policy:

```csharp
using LakeCode.Search;
using System.Text.Json;

var source = new DirectorySearchSource("/path/to/repository",
    (absolutePath, access, token) =>
    {
        token.ThrowIfCancellationRequested();
        // This host intentionally authorizes its entire selected root.
        // Replace this callback with the application's actual permission policy.
        return ValueTask.CompletedTask;
    });
var engine = new SearchEngine(source);
var filter = new SearchFilter
{
    Include = ["**/*.cs"],
    Exclude = ["**/bin/**", "**/obj/**"]
};

var grep = await engine.GrepAsync(new("SearchEngine") { Filter = filter });
var find = await engine.FindAsync(new("search engine") { Filter = filter, Top = 5 });
var outline = await engine.OutlineAsync("src/Example.cs", language: "c-sharp");
var trace = await engine.TraceAsync(new("SearchEngine", TraceRelation.Implementation)
{
    Filter = filter,
    Support = ["source"],
    Top = 5
});
var toolOutput = JsonSerializer.Serialize(trace);
```

Each call runs on a worker task so synchronous parsing and disk enumeration do not execute on a caller's UI thread. Cancellation tokens should always be propagated from the agent tool call. Hosts remain responsible for bounding serialized tool output to their own context budget.

## Grammar coverage

All 27 native grammars shipped in TreeSitter.DotNet 1.3.0 are registered:

`agda`, `bash`, `c`, `cpp`, `c-sharp`, `css`, `embedded-template`, `go`, `haskell`, `html`, `java`, `javascript`, `jsdoc`, `json`, `julia`, `ocaml`, `ocaml-type`, `php`, `python`, `ql`, `razor`, `ruby`, `rust`, `scala`, `tsx`, `typescript`, `verilog`.

JSdoc and other ambiguous inputs can be selected explicitly. A host can also pass `languageSelector: path => ...` to the engine constructor for all operations. Returning null uses extension detection. Header `.h` defaults to C; override it for C++. Embedded languages are not automatically injected (e.g. JavaScript inside HTML). Each parser handles its own grammar.

Structure is syntax-based, not a language-server symbol table: named top-level constructs plus declaration/definition/item and common structural node kinds are returned with grammar node kinds, labels, nesting depths and 1-based inclusive line ranges. This generic traversal works with every registered grammar, including data and markup, but does not claim an equally refined symbol taxonomy for each language. Non-code files remain searchable as UTF-8 text.

## Limits, errors and cancellation

`SearchLimits` is immutable configuration captured by the engine. Defaults:

- 1 MiB per file; 64 MiB cumulative reads per operation.
- 100,000 enumerated file identifiers; 100,000 visited syntax nodes per parsed file.
- 1,000 grep matching lines / returned trace regions; top-k must also fit this cap.
- 32,000 UTF-16 characters per input line; 30 second cooperative deadline.
- Regex matching has an independent 250 ms per-line timeout.

`DirectorySearchSource` also bounds visited directory entries (default 100,000), including directories that never yield files. A custom source must bound its own internal traversal, buffering and blocking operations.

A budget breach throws `SearchLimitException`, never an empty result or silently truncated success. Narrow the query or change the host's limits. Regex timeout, native loader/ABI errors, invalid queries, enumeration errors and explicit outline read failures propagate. Source read IO/permission/UTF-8/binary failures in multi-file searches are returned as `SearchIssue` entries with `Complete = false`. The host should not suppress or expose sensitive details in those messages without its own error policy. Find selects top-k paths before reading them; unreadable selected paths are reported and not backfilled with lower-ranked files.

Caller cancellation throws `OperationCanceledException`; deadline expiry throws `SearchLimitException`. Cancellation is cooperative: TreeSitter.DotNet 1.3.0 does not expose cancellable native parsing, so the engine checks before and after `Parse` and bounds input size. Blocked OS calls and a source that ignores cancellation cannot be preempted. No background work survives an awaited operation; a native call must finish before disposal/completion. There is no OS-level memory/time sandbox.

## Dependencies and distribution

Pinned packages:

- TreeSitter.DotNet 1.3.0 — managed binding plus platform-specific native parsers. Binding declares MIT; bundled grammars have their own upstream notices/licenses that must be checked and preserved for distribution.
- Microsoft.Extensions.FileSystemGlobbing 10.0.0 — include/exclude path matching; not gitignore semantics.

.NET's built-in literal matching and regex are used because compatibility with ripgrep was not requested. Scout/FuzzMatch are not added speculatively. Their behavior and dependency cost are unnecessary for the implemented literal/regex and identifier-normalized ranking contracts.

The main assembly is `bin/Release/net10.0/LakeCode.Search.dll` after a Release build. **It is not a single-file distribution:** preserve managed dependencies and native runtime assets. Prefer ProjectReference/PackageReference rather than copying only the main DLL; a custom AssemblyLoadContext also needs a native dependency resolver. Native parser availability is platform-specific.

Dependency advisory scans, full transitive license/hook audit and native runtime behavior have not been verified. Successful compilation is not a security or runtime certification. No tests or application runs are performed under this repository's current verification rules.

## Build

```sh
dotnet build LakeCode.Search/LakeCode.Search.csproj -c Release
```
