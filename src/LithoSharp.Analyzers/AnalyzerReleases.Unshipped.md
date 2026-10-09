### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|------
LSA1001 | LithoSharp | Error | All known values violate SiteUrl.FromAbsolute runtime guard.
LSA1002 | LithoSharp | Error | All known values violate a registered SiteRoute/SiteUrl route argument guard.
LSA1003 | LithoSharp | Error | All known values violate SiteAssetOutput relative output path guard.
LSA1004 | LithoSharp | Error | All known values violate SiteQualityOptions failureThreshold guard.
LSA1101 | LithoSharp | Info | Unverified contents passed to the exact Html.UnsafeRaw(string) sink.
LSA1102 | LithoSharp | Error | Proven raw HTML fragment in a known quoted attribute interpolation.
LSA1103 | LithoSharp | Error | Interpolation in a known tag name, attribute name or unquoted attribute position.
LSA1104 | LithoSharp | Warning | HTML encoded interpolation in a known script, style or event handler context.
LSA1105 | LithoSharp | Error | Complete direct literal HTML has a base-independent URL violation of shared LSQ001 rules.
LSA1201 | LithoSharp | Error | Every known public path is absent from a fresh Closed explicit manifest route lookup.
LSA1205 | LithoSharp | Info | Generated manifest provenance differs from current compiler inputs; route absence is Deferred.
