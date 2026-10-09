# LithoSharp.Analyzers

LithoSharp用のRoslyn analyzerです。netstandard2.0、Microsoft.CodeAnalysis 4.14.0を使います。Core runtime・Node・MSBuildWorkspace・CodeFixのWorkspacesを参照しません。packageは自身のDLL/XMLとYamlDotNetをanalyzers/dotnet/csへ配置し、Roslyn本体を同梱しません。実行時Coreへの推移依存もありません。

The registered sinks below use metadata symbols from the LithoSharp assembly, exact overloads and parameter ordinals. Source-defined lookalikes, failed binding and user-defined conversions are excluded. Named arguments are resolved by their parameter symbols; omitted valid defaults do not produce diagnostics. Generated code is excluded.

### LSA1001

`SiteUrl.FromAbsolute(string value)` uses the same pure guard as the runtime. Null, relative or malformed URLs, whitespace, control characters, backslashes and non-HTTP(S) schemes are errors when every known value is invalid.

### LSA1002

`SiteUrl.ForFile`, `SiteUrl.ForDirectory`, `SiteRoute.ForFile` and `SiteRoute.ForDirectoryIndex` validate the relative path and optional base URL independently, using the runtime SiteRoute source compiled privately into the analyzer. Traversal, reserved device names, invalid percent/UTF-8 encoding and invalid base URLs follow that source. Empty directory routes are valid.

### LSA1003

`SiteAssetOutput(string id, string relativeOutputPath)` validates only `relativeOutputPath` with the runtime relative output path guard. These are filesystem names: percent text is literal, unlike route URI segments. Containment can be proven for invalid static relative paths without filesystem access. Artifact ownership, registry membership and other output APIs remain outside this registered scope.

### LSA1004

`SiteQualityOptions(SiteDiagnosticSeverity failureThreshold, bool checkOrphans, ExternalLinkCheckOptions? externalLinks)` validates only an explicitly supplied `failureThreshold`. The enum definition and pure guard are shared with the runtime. Other options and TimeSpan ranges are not certified by this rule.

The finite flow contract is recorded in `static-value-contract.json`. Constants (including const fields), supported conversions, string concatenation, string-only unformatted interpolation, single local assignments and acyclic CFG branch joins may produce Known sets. Expressions are snapshotted at their evaluation point, so a later argument assignment cannot replace an earlier argument's value. Named arguments retain their source evaluation order; simple local assignment expressions return their snapshotted right-hand value. A set is capped at eight candidates. An Error requires **every** candidate to violate the selected argument guard. Mixed valid/invalid sets and Unknown branches do not produce an Error.

Properties, readonly/mutable fields, user calls, ToString/formatting, environment reads and ref aliases stay Unknown; no user code is executed. Calls/getters conservatively invalidate local and capture facts, including closures and ref/out escape. Any CFG back edge or exceptional region (including try/finally, using and lock) defers local/capture propagation; direct constants remain independently provable. Nested functions get independent states; outer captures stay Unknown. Unreachable CFG blocks are excluded. Budget exhaustion defers analysis without an additional coverage diagnostic. There is no global cross-compilation cache.

Locations use the original argument expression's UTF-16 span, including flow-derived values. No literal substring or origin offset is invented. Properties record Known, candidate count and the argument-span fallback. No diagnostic is a guarantee of runtime validity. Runtime guards still apply after suppression. Frontmatter/schema diagnostics remain owned by the Generator; this analyzer does not emit LSG001..006 or LSA1301..1302.

現在の開発候補は、Syntamark.Source [2.0.0-preview.3]のportable sourceを既定でコンパイルし、YamlDotNet [18.1.0]をpayloadへ同梱します。source-treeのビルドではDirectory.Build.propsが固定版を選び、SyntamarkSourceVersionの不一致を拒否します。canonical parserをrepoから直接Compileしたり、Syntamarkのruntime DLLをcompiler hostへ読み込ませたりしません。

### LSA1101

`Html.UnsafeRaw(string)` receives Info when its contents are Unknown to the bounded static flow. This reports unverified raw output without claiming a vulnerability. A known literal or supported known local is not reported. Ordinary strings and lookalike user APIs are excluded.

### LSA1102

A direct interpolated UnsafeRaw argument produces Error when a proven raw fragment is placed in a known quoted attribute. An arbitrary IHtmlContent variable is not proven to be a raw fragment. The sealed HtmlText and HtmlAttributeValue wrappers use the runtime's HTML encoding and are permitted in that context.

### LSA1103

Interpolation in a known tag name, attribute name or unquoted attribute value produces Error. A preceding unknown hole invalidates the following context and prevents a definite Error there. Add an appropriate quoted attribute or change the template manually; there is no automatic quote fix.

### LSA1104

HTML encoding in a known script/style body, quoted event handler or style attribute produces Warning. Html.Encode and the two sealed encoded wrappers do not establish JavaScript/CSS safety. Choose appropriate serialization manually.

The exact template scope, supported output symbols, budgets, original source spans and unsupported/Deferred cases are recorded in `html-template-context-spec.md`. Suppress individual rules using normal compiler/editor configuration or a documented pragma after reviewing the output contract. Arbitrary/concatenated templates remain outside the registered interpolation context scope.

### LSA1105

Direct normal/verbatim/raw HTML literals passed to `Html.UnsafeRaw(string)` use the same portable HTML bridge and pure LSQ001 URL checks as the runtime. Error requires a Complete parse, an exact original URL span, and a base-independent violation: an empty resource URL, malformed explicit absolute scheme, unsupported scheme or HTTP(S) userinfo. Entity decoding follows the bridge. Relative/protocol-relative references, site membership, canonical/base policy, srcset/CSS splitting and composite/dynamic inputs are Deferred. Partial/Failed parses never produce a definite literal URL Error. See `html-template-context-spec.md` for the exact contract and budgets.

このpreviewは未公開の開発候補です。全SDK/VS/C#拡張の認定やCodeFix出荷を示しません。
