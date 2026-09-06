# LithoSharp Blog sample

A minimal console app that builds a blog with [LithoSharp](../../src/LithoSharp).
It depends only on the LithoSharp library, so it doubles as a check that the generator
works without any daily-update code.

## Run it

```powershell
dotnet run --project samples/LithoSharp.Sample -- --output _site
```

- `--output <dir>`: where the site is written (default: `./_site`).
- `--content <dir>`: Markdown source folder (default: the bundled `content/` next to the app).

Open `_site/index.html` in a browser to view the result. This sample explicitly uses
`BlogSiteTemplate`, preserving the legacy listing, archive, tags, and post layout.

## What it shows

- Reading Markdown posts with `MarkdownPostReader`.
- Validating posts with the default validator via `SiteGenerator.Validate`.
- Generating the site with `SiteGenerator.GenerateAsync` and a custom `SiteCustomization`
  (theme brand prefix, an `AdditionalCss` palette override, and opt-in `llms.txt`).
- Explicit selection of `BlogSiteTemplate`.

For the default documentation layout, see
[`samples/LithoSharp.DocsSample`](../LithoSharp.DocsSample).

Favicon and social-image assets are optional. When no favicon directory is supplied,
LithoSharp skips those binaries and still produces a complete HTML site.

The sample exports `BlogSampleFactory : ISiteFactory`; `lithosharp build
samples/LithoSharp.Sample -o artifacts/blog-cli` and the normal `dotnet run` entry
point use the same definition. `--content`, `--output`, `--check`, and
`--redirect-demo` remain available in the sample program.

このサンプルは `BlogSampleFactory` を公開しています。CLI と通常の C# 実行が
同じサイト定義を使い、既存のサンプル用オプションも利用できます。
