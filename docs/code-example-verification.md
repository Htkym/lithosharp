# Verify documented code examples

Map a stable `snippet` ID on a fenced block to an explicitly trusted test project.
The verification API runs that project's existing tests. It does not compile or
execute Markdown fence contents, evaluate OKF computations, or start tests during
ordinary site generation or editor inspection.

````markdown
```csharp snippet="addition" source="examples/Arithmetic.cs" region="Addition"
```
````

Use the existing `source` and `region` inclusion rules to show code that the test
project actually compiles. A fence without `source` may also declare a snippet ID;
its mapping says which existing tests check it, without claiming that copied fence
text is automatically compiled. IDs must be unique within each document.

```csharp
using LithoSharp.Content;
using LithoSharp.Documentation;

var snippets = new[]
{
    new DocumentSnippet("addition", "guide.md", "examples/Examples.csproj",
        testFilter: "/*/*/ArithmeticTests/AdditionWorks")
};
var inputs = new[]
{
    ContentDependency.FromFile("examples/Arithmetic.cs"),
    ContentDependency.FromFile("examples/ArithmeticTests.cs"),
    ContentDependency.FromFile("examples/packages.lock.json"),
    ContentDependency.FromFile("global.json"),
    ContentDependency.FromValue("assertion-contract", "addition/1")
};
var results = await DocumentationVerification.VerifySnippetsAsync(
    trustedRoot, snippets, inputs, "Release", cancellationToken);
```

Declare every relevant source, referenced project, package lock, SDK configuration,
build property file and test setting in `inputs`, including dependencies outside
the selected project directory. Paths are relative to `trustedRoot`. Only regular
files contained in that root are accepted. File bytes are read again for every
inspection; value dependencies must change when their represented rule changes.
This explicit input manifest is a caller contract, not an inferred MSBuild graph.

The source document, selected project, included source, effective `dotnet --version`,
configuration, selected test filter and parser identity also enter the fingerprint.
The test command uses `dotnet test --project ... --no-restore --configuration ...`.
The optional filter uses TUnit's `--treenode-filter` syntax. Restore dependencies
before verification. The API has no implicit time limit and observes cancellation.

Each result names its original document range. Included code also supplies the
known original source region as a related location. A failed command retains
bounded output in its message for diagnosis. Tests sharing a project and filter
run once and attach that execution outcome to every explicitly mapped snippet.
This mapping does not establish coverage of each statement in the fence.

`Success` and `Failure` describe an explicit test execution. `NotRun` describes an
inspection or a result invalidated by inputs changing during execution. `Skipped`
requires an explicit reason. `OutOfScope` identifies an unavailable or ambiguous
fence or input. Results contain an execution timestamp only when a command ran.

Call `InspectSnippetsAsync` with optional previous results to check current inputs
without running a project. Changed code or settings invalidate previous evidence.
An inspection always reports `NotRun` for an eligible unskipped snippet; it never
turns a previous success into a newly executed test. No result updates OKF
`verified` metadata or published output.

## Find examples affected by API changes

Declare the exact API variants used by a mapped snippet with `ApiReferences`.
The existing test mapping stays explicit; this declaration does not establish
test coverage or extract API uses from C#.

```csharp
var api = new DocumentKey("examples", "v1", "en",
    "M:Examples.Arithmetic.Add(System.Int32,System.Int32)");
var snippets = new[]
{
    new DocumentSnippet("addition", "guide.md", "examples/Examples.csproj",
        testFilter: "/*/*/ArithmeticTests/AdditionWorks")
    {
        ApiReferences = [api]
    }
};
var selectedChanges = ApiReferenceSite.Compare(previous, current)
    .Where(change => change.Id == api.Id).ToArray();
var report = references.FindApiImpacts(
    new Dictionary<DocumentKey, IReadOnlyList<ApiChange>> { [api] = selectedChanges },
    snippets, cancellationToken);
```

Here, `previous` and `current` are the selected API member snapshots, and
`references` is an acquired `DocumentReferenceSnapshot` with captured Markdown
buffers and an exact semantic catalog. Its document `ProjectPath` must match the
snippet's relative `DocumentPath`. The selected key fixes the collection, version,
locale and full XML member ID, including overload parameters. To find guide links
to a removed API, retain a reference/catalog snapshot from before removal, in which
that key still resolves. An explicit snippet association can still match an absent
API; the unavailable catalog target is reported in `ManualScopes`.

`report.Impacts` joins the selected changes with native parsed links (`xref:`,
relative or public paths) resolved in the acquired exact catalog, and declared
snippet API references. Images and unused reference definitions are not uses.
Each result carries its exact `Api` key,
`Changes`, captured `Document`, UTF-16 `Location` range and `SnippetId` (null for
a guide link). Compare `Document.Version` and `Document.TextHash` with the current
buffer before using offsets; the result is evidence about that immutable capture.

Review `ManualScopes` for unavailable or ambiguous references, dynamic inputs and
undeclared uses. The query reads captured buffers without executing code. It does
not extract uses from C# or prose and cannot guarantee that every use was found,
so `IsComplete` remains false.
