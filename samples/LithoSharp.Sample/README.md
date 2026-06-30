# LithoSharp sample

A minimal console app that builds a static site with [LithoSharp](../../src/LithoSharp).
It depends only on the LithoSharp library, so it doubles as a check that the generator
works without any daily-update code.

## Run it

```powershell
dotnet run --project samples/LithoSharp.Sample -- --output _site
```

- `--output <dir>`: where the site is written (default: `./_site`).
- `--content <dir>`: Markdown source folder (default: the bundled `content/` next to the app).

Open `_site/index.html` in a browser to view the result.

## What it shows

- Reading Markdown posts with `MarkdownPostReader`.
- Validating posts with the default validator via `SiteGenerator.Validate`.
- Generating the site with `SiteGenerator.GenerateAsync` and a custom `SiteCustomization`
  (theme brand prefix, an `AdditionalCss` palette override, and opt-in `llms.txt`).

Favicon and social-image assets are optional. When no favicon directory is supplied,
LithoSharp skips those binaries and still produces a complete HTML site.
