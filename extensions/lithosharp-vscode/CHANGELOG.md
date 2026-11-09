# Changelog

## 0.4.0

- Show separate document-analysis, project-context, schema-scope and explicit-operation states. Unavailable or custom build-only checks stay unverified.
- Validate CLI schema versions, required fields, diagnostic locations and exit-code agreement before reporting a successful build or output check.
- Invalidate saved context and results when project selection or observed inputs change; reject late results from earlier generations.
- Link diagnostics to the official English/Japanese reference without changing their locations or inventing source ranges.
- Bundle the matching 2.2.0 language server. Optional context fields remain compatible with older protocol consumers. Check the Marketplace for publication status.

## 0.3.0

- Use Markdown section hierarchy and subtree ranges for Outline symbols while retaining original heading navigation positions. MDX keeps its separate heading path.
- Report unavailable Markdown analysis separately when no reliable source range exists, and discard stale document/context results.
- Document project-context boundaries, compiler-only custom schema checks, analysis limits and the typed-content walkthrough in English and Japanese.
- Bundle the current language server with the verified Syntamark 1.2.0 pair. Core packages use their separate 2.1.3 release channel.

## 0.2.0 — 2026-10-18

- Display the extension as **LithoSharp for VS Code** and use the shared LithoSharp icon.
- Rewrite the English and Japanese guides around installation, commands, settings, requirements and troubleshooting.
- Keep the extension identity `htkym.lithosharp` and the existing runtime behavior.

## 0.1.0 — 2026-10-07

- Add project selection, workspace trust, CLI build and server commands, Markdown/MDX diagnostics, heading navigation and saved-site preview.
- Bundle the .NET language server and locked MDX worker source. Markdown diagnostics do not require Node.js; MDX analysis requires an explicit worker restore.
