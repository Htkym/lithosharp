# LithoSharp.Analyzers

### LSA1401

既定 severity は Info です。正確な metadata overload `LithoSharp.Content.StaticSiteManifest.GetUrl(string)` の引数0が直接の文字列リテラルで、検証済みの生成 static Manifest が fresh な Closed scope を持ち、同じ immutable catalog の一意な生成 PageRef と完全一致するときに報告します。位置は元の引数リテラル全体の UTF-16 span で、生成参照や文字列内部の位置には移しません。

別packageの `LithoSharp.CodeFixes` は引数だけを完全修飾した生成 `PageRef.Route.PublicPath` に置き換え、元の receiver、`Manifest.GetUrl(string)` overload、runtime guard、引数のtriviaを維持します。Pages 全体の canonical な初期化、catalog entry と PageId の一致、利用者の static 初期化による再入がないことを確認し、登録時と適用時に現在の compiler inputs を再検証します。Open/Stale/Unavailable、曖昧な参照、任意の式、利用者による型変換、getterや副作用、明示的な static constructor、古いworkspace snapshotには fix を提供しません。標準の severity、NoWarn、pragma、editorconfig を使います。FixAll と LSA1402/LSA1403 は未提供です。

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

### LSA1201

Error applies only to `Guides.Manifest.GetUrl(path)` when `Guides` explicitly opts into `EmitStaticSiteManifest = true`, the generated property is verified against its runtime catalog initializer and current AdditionalFiles routes, and its compiler fingerprint is fresh. Every bounded known candidate must be absent from that exact immutable lookup. Mixed present/missing candidates, unknown arguments, receiver aliases, custom factories, metadata-only manifests and ordinary catalogs are Deferred. This lookup uses ordinal exact public paths; queries, fragments and URL resolution are outside its contract. A collection catalog does not establish site publication coverage. Anchors and assets remain Open.

### LSA1205

Collections with an explicit static constructor are Unavailable, including an empty constructor or a constructor in another partial declaration. C# permits that constructor to reassign static get-only `Manifest` and `Catalog` auto-properties. The Analyzer does not infer immutable membership from their original initializer in that case.

Info identifies a generated Closed lookup whose fingerprint differs from current compiler inputs. Route absence is then Deferred. Fingerprints use original C# buffers and parse symbols, AdditionalText buffers and collection/id/route/site/variant metadata, project root/target framework/configuration/profile, and assembly identities/MVIDs/reference aliases. No disk scanning, network access or user code runs. Unreadable inputs, unsupported references or budgets (8,192 source/additional inputs; 8,388,608 framed text units) are Unavailable and defer absence. Other generators' source additions can conservatively make a lookup Stale. The known StaticContentGenerator output path is excluded from its own fingerprint; its marker is an origin convention, not a security attestation.

Normal Roslyn per-ID severity, editor configuration and original-source pragmas apply. LSG004 owns route collisions; LSG005/006 own YAML/schema diagnostics. These errors cause emitted lookup coverage to be Open and are not repeated as LSA1204/1301/1302. LSA1401 and its separate LithoSharp.CodeFixes action have the limited contract above. LSA1202/1203/1204, LSA130x, LSA1402/1403, LSA9001, whole-site publication coverage, FixAll and other CodeFix actions remain unimplemented.
