# LithoSharp Docs sample

![Generated sample site](../../docs/images/docs-light.png)

Theme switching is enabled by default. Set `SiteThemeOptions.EnableThemeSwitching = false`
to disable it; see the [configuration and fixed dark example](../../docs/layout-css-contract.md#theme-switching--テーマ切り替え).

A minimal console app that builds documentation with
[LithoSharp](../../src/LithoSharp). It uses the default `DocsSiteTemplate`.

## Run it

```powershell
dotnet run --project samples/LithoSharp.DocsSample -- --output _site
```

- `--output <dir>`: where the site is written (default: `./_site`).
- `--content <dir>`: Markdown source folder (default: the bundled `content/` next to the app).

Open `_site/index.html` in a browser. Directories create sidebar sections, and
`sidebar_position` and `sidebar_label` in Markdown front matter control page order
and labels.

## What it shows

- The default Docs template.
- Stone-inspired typography and light/dark palettes. The header toggle saves the
  reader's choice; otherwise the site follows the operating system's preference.
- Nested content folders for the sidebar hierarchy.
- `sidebar_position` and `sidebar_label` front matter.
- The page table of contents and previous/next document links.

For the legacy blog layout, see
[`samples/LithoSharp.Sample`](../LithoSharp.Sample).

The sample exports `DocsSampleFactory : ISiteFactory`. The CLI can build the same
Markdown and custom C# article layout with:

```sh
lithosharp build samples/LithoSharp.DocsSample -o artifacts/docs-cli
lithosharp serve samples/LithoSharp.DocsSample
```

The normal `dotnet run` entry point calls the same factory. Its existing
`--content`, `--output`, `--check`, `--redirect-demo`, and `--asset-demo` switches
remain available. The factory uses its project context for source paths.

このサンプルは `DocsSampleFactory` を公開しています。CLI と通常の C# 実行が
同じサイト定義と記事レイアウトを使います。既存のサンプル用オプションも利用できます。

## Testing

Use SiteTestHost with the sample factory and SiteTestDocument for DOM assertions. The host isolates output and caches and does not use the network unless external link checks are configured.

```csharp
using LithoSharp;
using LithoSharp.Testing;

await using var host = await SiteTestHost.CreateAsync(
    new DocsSampleFactory { AssetDemo = true },
    new SiteFactoryContext(Path.GetFullPath("samples/LithoSharp.DocsSample")));
host.AssertSucceeded();
host.AssertRoute("/index.html", "index.html");
using var page = await host.OpenPageAsync("/index.html");
page.AssertElement("main");
```

Reference this sample project and `LithoSharp.Testing` from your test project, and
run the example from the repository root. See the [testing guide](../../docs/testing.md)
and [Japanese sample notes](README.ja.md) for diagnostics and error conditions.
