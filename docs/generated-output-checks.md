# Inspect existing generated output

[日本語](generated-output-checks.ja.md)

Use the commands according to the artifact you want to check:

| Command | Target | Work performed |
| --- | --- | --- |
| `check [project]` | A freshly generated temporary site | Compiles the project, runs the factory, generates, and validates. |
| `check-output --output <directory> --base-url <url>` | The existing distribution directory | Reads current files and validates them without generation. |

```powershell
lithosharp check-output --output dist --base-url https://example.com/guide/ --format json
```

The base URL must be the actual published HTTP or HTTPS URL, including its base
path, without credentials, query, or fragment. Relative output paths are resolved
from the command's working directory. Formats are `text`, `json`, and `sarif`;
machine reports go to stdout and the inspection banner goes to stderr. Error
diagnostics produce exit code 1, invalid usage 2, and cancellation 130.

The command does not locate or compile a project, activate its factory, generate
files, repair output, write reports to disk, or read/write persistent caches. It
does not request external URLs. Library callers can use:

```csharp
var report = await LithoSharp.Quality.GeneratedSiteInspector.InspectAsync(
    outputDirectory, "https://example.com/guide/", cancellationToken);
```

The inspector inventories actual files instead of trusting a generation manifest.
It reads HTML and CSS as strict UTF-8, with or without a UTF-8 BOM. Malformed
UTF-8 and UTF-16/UTF-32 BOM payloads are rejected; encoding is never autodetected.
It reuses the normal local URL, fragment, canonical, SEO, duplicate-title, and
page-reachability rules. Binary resources are checked for existence, not decoded. Native immediate `0;url=` meta redirects are
checked for local targets, missing targets, cycles, chains, and canonical agreement.
Unsupported refresh forms report an error. JavaScript redirects are outside this
inspection.

An existing directory has no trusted build graph or source snapshot. Inspection
therefore does not prove that output matches current sources/configuration, that
all intended pages were emitted, or that an asset was registered or is unused
(`LSQ008`). It does not infer transitive asset ownership or inspect external HTTP
responses. Use generation-time quality checks when these build relationships matter.

The selected tree must be quiescent during inspection. Links and reparse points
in the tree, selected root, or its ancestors are rejected, as are route collisions
and ambiguous paths, including literal backslashes in Unix file names. Only
regular files and directories enter the inventory. Unix file types are checked
before opening files, so FIFOs, sockets, and devices are rejected. This requires
Linux `statx` or macOS x64/arm64 `lstat`; unavailable native checks fail the
inspection. Contained file handles are verified before text reads. File
inventory and inspected text hashes are checked again before returning. This is
change detection, not an atomic snapshot against concurrent adversarial mutation.

To avoid regenerating solely to inspect a distribution, enable
`SiteGenerationOptions.Quality` during its build, or run `check-output` on that
build's output. Every `check-output` invocation reads current bytes; an earlier
successful check is never reused after inputs or output change. If source or
configuration changes, build the distribution again before judging its freshness.
