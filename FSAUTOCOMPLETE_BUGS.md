# FSAutocomplete method-chain investigation

## Measured results

The F# probe in `tests/FsAutocompleteTests.fs` drives an actual FSAutocomplete
process over Content-Length framed JSON-RPC. It restores a `net10.0` project
with HtmlAgilityPack 1.11.54, confirms an intentional type error, removes it,
then checks three edited revisions, hover on each chain, and completion.
Diagnostics must correspond to a completed `fsharp/documentAnalyzed` revision;
queued diagnostics from earlier checks are not accepted as current results.
Requests have a 30-second deadline; project diagnostics have 60 seconds.

Tested with .NET SDK 10.0.401:

| FSAutocomplete | Result | First hover | Subsequent hovers |
|---|---|---|---|
| 0.83.0, development-shell executable | Passed | 3.805 s | 0.001–0.037 s |
| 0.80.1, installed managed DLL launched with current `dotnet` | Passed | 3.970 s | 0.002–0.062 s |

All four original expressions were present unchanged:

```fsharp
DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff")
Path.GetExtension(fileName).TrimStart('.')
row.SelectSingleNode(".//td[4]").InnerText
idMatch.Groups.[1].Value
```

No method-chain hang reproduced in this minimal project. This does **not** prove
that the reported Neovim freeze is fixed: this probe excludes Neovim/ionide-vim,
user LSP hooks, and the full plugin workspace. The first hover has measurable
cold-start latency, not an infinite loop.

The installed 0.80.1 executable wrapper cannot load the new SDK's `libhostfxr.so`
in this development shell: its old native closure reports
`GLIBC_ABI_GNU2_TLS not found`. Launching its managed DLL using the current
`dotnet` avoids that native-library mismatch. This is a separate environment
failure, not evidence of a type-inference hang.

Run the current server:

```console
nix develop -c just test-lsp
```

To test another server, pass its executable or managed DLL:

```console
nix develop -c dotnet run --project Jellyfin.Plugin.BulgarianSubs.Tests.fsproj -- --lsp /path/to/fsautocomplete.dll
```

Probe implementation notes: FSAC only publishes diagnostics when the client
advertises `textDocument.publishDiagnostics`. Omitting that capability caused
an initial probe timeout even though project checking completed. Multiple
compiler/analyzer diagnostic publications also required revision-aware waiting.

## Historical report

The previous report attributed freezes to the four expressions above and
suggested lambda pipelines or intermediate bindings. It labelled all patterns
"FIXED" without recording an LSP reproduction. Its references to `Logger.fs`
and old `Providers.fs`/`Parsing.fs` line numbers no longer match this workspace.
Those labels are not verification of a fix. Existing null checks remain useful
for parser correctness independently of FSAutocomplete.
