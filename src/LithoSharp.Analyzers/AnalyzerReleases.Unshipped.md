### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
LSA9001 | LithoSharp | Disabled | Explicit opt-in static coverage policy; Error when enabled, with ordinary Roslyn severity and suppression.
LSA1401 | LithoSharp | Info | Unique equivalent generated PageRef path for a direct literal in a fresh immutable manifest.
LSA1402 | LithoSharp | Error | Existing entry ID and valid route contradict the same uniquely selected fresh Closed generated catalog.
LSA1403 | LithoSharp | Info | Explicitly selected registered documentation producer route contributions cannot be enumerated statically; count unique selected instances only.
LSA1001 | LithoSharp | Error | All known values violate SiteUrl.FromAbsolute runtime guard.
LSA1002 | LithoSharp | Error | All known values violate a registered SiteRoute/SiteUrl route or ContentRef.GetUrl base URL argument guard.
LSA1003 | LithoSharp | Error | All known values violate a SiteAsset relative input/output or SiteAssetOutput relative output path guard.
LSA1004 | LithoSharp | Error | All known values violate SiteQualityOptions failureThreshold guard.
LSA1101 | LithoSharp | Info | Unverified contents passed to the exact Html.UnsafeRaw(string) sink.
LSA1102 | LithoSharp | Error | Proven raw HTML fragment in a known quoted attribute interpolation.
LSA1103 | LithoSharp | Error | Interpolation in a known tag name, attribute name or unquoted attribute position.
LSA1104 | LithoSharp | Warning | HTML encoded interpolation in a known script, style or event handler context.
LSA1105 | LithoSharp | Error | Complete direct literal HTML has a base-independent URL violation of shared LSQ001 rules.
LSA1201 | LithoSharp | Error | Every known public path is absent from a fresh Closed explicit manifest route lookup.
LSA1203 | LithoSharp | Error | Every known asset/output instance is absent from the same inline transform handler's independently copied input/output declarations.
LSA1205 | LithoSharp | Info | Generated manifest provenance differs from current compiler inputs; route absence is Deferred.
LSA1501 | Migration | Warning | Bound legacy Testing Document DOM getter requires manual migration to the v2 snapshot API.
