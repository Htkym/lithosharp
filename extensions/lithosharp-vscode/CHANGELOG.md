# Changelog

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
