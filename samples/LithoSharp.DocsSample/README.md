# LithoSharp Docs sample

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
