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
