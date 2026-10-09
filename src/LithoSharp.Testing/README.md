# LithoSharp.Testing

Generate sites inside ordinary .NET tests, with no server or external process.
Use any test framework: assertion failures throw `SiteTestException`.

```csharp
using LithoSharp;
using LithoSharp.Configuration;
using LithoSharp.Testing;

await using var host = await SiteTestHost.CreateAsync(
    new SiteDefinition(new SiteSettings { BaseUrl = "https://example.com/" }, []));
host.AssertSucceeded();
host.AssertRoute("/index.html", "index.html");
host.AssertArtifact("index.html");
using var page = await host.OpenPageAsync("/index.html");
page.AssertElement("main");
```

Output, ownership files and enabled caches are isolated in a temporary directory.
Disposal removes that tree. Source files are read in place. Explicit timestamps and
quality settings are honored; otherwise the host uses Unix epoch and enables local
quality validation. Network validation requires explicit external-link options.
The definition's output/cache paths are never used for writes by the host.
Custom factories, renderers and transforms are trusted application code and can
perform their own I/O; the host is not a sandbox.

Route, build-plan and quality validation failures return `Succeeded == false`,
`Result == null`, and diagnostics suitable for `AssertDiagnostic`. Other exceptions,
including cancellation and I/O failures, propagate. Call `AssertSucceeded` before
interpreting a successful diagnostics assertion as a successful build.

`SiteTestDocument.RenderComponent` and `RenderLayout` accept the existing rendering
contexts. `Parse` supports standalone HTML, including empty output, and rejects incomplete
tree coverage. `Snapshot.Query` and `QueryAll` expose read-only element views using
the declared bounded selector grammar; no scripts or network run.
`AssertText` and `AssertAttribute` compare the first matching element exactly.
`AssertMeta`, `AssertLink` and `AssertImage` match decoded attribute values exactly;
URL resolution and broken-target validation are performed by host quality checks.
Invalid arguments throw standard argument exceptions; unsafe filesystem paths are
rejected, and assertions on disposed hosts/documents throw `ObjectDisposedException`.

Disposal also invalidates retained snapshot and element views. Malformed selectors
throw `HtmlSelectorSyntaxException`; unsupported features or query limits throw
`HtmlSelectorUnsupportedException`. The v2 major version removes `Document`.
See [the snapshot/query contract and migration examples](https://github.com/Htkym/lithosharp/blob/feature/2.0.0/docs/Testing-v2-api.md).

See [the testing guide](https://github.com/Htkym/lithosharp/blob/main/docs/testing.md)
and [Japanese guide](https://github.com/Htkym/lithosharp/blob/main/docs/testing.ja.md).
