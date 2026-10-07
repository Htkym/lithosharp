using LithoSharp.Content;
using LithoSharp.Routing;

// A small consumer of long-stable 1.0 APIs. The compatibility harness compiles it
// against the published 1.0.0 package, then runs the same binary against the
// candidate assembly without recompiling, and finally recompiles it against the
// candidate package. Output must be identical in every case.

var directory = SiteRoute.ForDirectoryIndex("docs/guide/index.md", "https://example.test");
var file = SiteRoute.ForFile("feed.xml", "https://example.test");

Console.WriteLine($"directory.publicPath={directory.PublicPath}");
Console.WriteLine($"directory.output={directory.RelativeOutputPath}");
Console.WriteLine($"file.publicPath={file.PublicPath}");
Console.WriteLine($"file.output={file.RelativeOutputPath}");
Console.WriteLine($"slug={SlugHelper.ToSlug("Docs / Getting Started")}");
