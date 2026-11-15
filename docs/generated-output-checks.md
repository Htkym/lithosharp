# Inspect existing generated output

[日本語](generated-output-checks.ja.md)

Use the commands according to the artifact you want to check:

| Command | Target | Work performed |
| --- | --- | --- |
| `check [project]` | A freshly generated temporary site | Compiles the project, runs the factory, generates, and validates. |
| `check-output --output <directory> --base-url <url>` | The existing distribution directory | Reads current files and validates them without generation. |

Use this after the C# schema, typed-reference and content-loading stages; those
stages cannot prove what a custom layout or later file edit actually emitted.
The [Docs sample](../samples/LithoSharp.DocsSample/README.md#from-a-c-model-to-checked-output)
shows a normal checked build and a separate broken-output fixture that produces
`LSQ001`. The [site-quality guide](site-quality.md) lists the diagnostic rules.

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
does not request external URLs unless `--http` is explicitly supplied. Library callers can use:

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
(`LSQ008`). The local inspection stage does not infer transitive asset ownership
or request external HTTP responses. The optional `--http` stage below observes
deployed responses separately. Use generation-time quality checks when these build
relationships matter.

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

## Explicit post-publication HTTP inspection

After publishing, use the existing command's opt-in HTTP stage:

```powershell
lithosharp check-output --output dist --base-url https://example.com/guide/ --http --format json
```

Without `--http`, this command and ordinary builds do not perform these public
HTTP requests. The HTTP stage first requires a verified current-file inventory
from the existing publication inspector. It does not generate output, execute a
site project, write a cache, or create/promote a publication baseline. Optional
`--baseline` comparison remains a separate check. Combining explicit baseline
initialization with `--http` creates the baseline only if both inspection stages
succeed.

This stage uses real GET requests to inspect:

- Current HTML URLs and directory-index aliases with `text/html` responses.
  The explicit 404.html artifact may return 200 or 404; the unknown-URL probe
  separately requires an actual HTTP 404.
- Markdown URLs with `text/markdown; charset=utf-8`, valid UTF-8 bytes, and a body
  hash matching the inspected local artifact.
- The current llms.txt and sitemap.xml MIME types when those artifacts exist.
- HTTP redirects. Artifact redirects must remain on the selected origin and
  follow declared aliases or native redirect targets in the inspected inventory.
- A unique unknown URL under the site's base path. Its own response must be HTTP
  404; a successful fallback page or redirect is not counted as an HTTP 404.
- The actual origin's `/robots.txt`, even for a subpath site. A subpath
  `project/robots.txt` is never treated as origin-root management.

If the selected root site's inspected output includes robots.txt, the public
response must match that artifact's UTF-8 bytes. If origin-root robots is managed
elsewhere or disabled, its observed response is reported separately. A 404 is
reported as absent external management, not as proof of a generated robots file.
The inspector does not change existing crawler policies or host settings.

Requests use the existing public-address-only transport, DNS checks, no cookies
or proxy credentials, a 500 ms minimum interval, ten-second per-probe timeout,
and bounded redirect handling. Cross-origin or private-address redirects are
rejected. Localhost and private-network servers are deliberately outside this
public checker; use the local serve workflow to verify those environments.
Decoded Markdown body inspection is limited to 16 MiB and robots to 512 KiB. No prior HTTP
verdict is cached.

Reports use `LSPHTTP001` for artifact HTTP/MIME/body failures, `LSPHTTP002` for
observed redirects, `LSPHTTP003` for unknown-URL HTTP 404 failures, and
`LSPHTTP004` for origin-root robots observations or failures. `LSPHTTP000` records
completion of the explicitly requested inspection scope. Error diagnostics retain
exit code 1; a completion message is not a claim that all checks passed.

These are HTTP observations, not browser execution. JavaScript and HTML
meta-refresh navigation, host-specific Link/Vary headers, content negotiation,
rich-result eligibility, and external scanner scores are not certified by this
stage. Inspect those separately when required. Successful generated-output checks
alone never claim that this HTTP stage ran.