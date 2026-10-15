# LithoSharp.Analyzers

### LSA1401

既定 severity は Info です。正確な metadata overload `LithoSharp.Content.StaticSiteManifest.GetUrl(string)` の引数0が直接の文字列リテラルで、検証済みの生成 static Manifest が fresh な Closed scope を持ち、同じ immutable catalog の一意な生成 PageRef と完全一致するときに報告します。位置は元の引数リテラル全体の UTF-16 span で、生成参照や文字列内部の位置には移しません。

別packageの `LithoSharp.CodeFixes` は引数だけを完全修飾した生成 `PageRef.Route.PublicPath` に置き換え、元の receiver、`Manifest.GetUrl(string)` overload、runtime guard、引数のtriviaを維持します。Pages 全体の canonical な初期化、catalog entry と PageId の一致、利用者の static 初期化による再入がないことを確認し、登録時と適用時に現在の compiler inputs を再検証します。Open/Stale/Unavailable、曖昧な参照、任意の式、利用者による型変換、getterや副作用、明示的な static constructor、古いworkspace snapshotには fix を提供しません。標準の severity、NoWarn、pragma、editorconfig を使います。LSA1402/1403の有限な診断範囲は下記、既存FixAllの提供範囲はCodeFixesのREADMEを参照してください。

LithoSharp用のRoslyn analyzerです。netstandard2.0、Microsoft.CodeAnalysis 4.14.0を使います。Core runtime・Node・MSBuildWorkspace・CodeFixのWorkspacesを参照しません。packageは自身のDLL/XMLとYamlDotNetをanalyzers/dotnet/csへ配置し、Roslyn本体を同梱しません。実行時Coreへの推移依存もありません。

The registered sinks below use metadata symbols from the LithoSharp assembly, exact overloads and parameter ordinals. Source-defined lookalikes, failed binding and user-defined conversions are excluded. Named arguments are resolved by their parameter symbols; omitted valid defaults do not produce diagnostics. Generated code is excluded.

### LSA1001

`SiteUrl.FromAbsolute(string value)` uses the same pure guard as the runtime. Null, relative or malformed URLs, whitespace, control characters, backslashes and non-HTTP(S) schemes are errors when every known value is invalid.

### LSA1002

`StaticSiteManifest.GetUrl(string publicPath)` rejects argument 0 only when every value in its nonempty Known snapshot is null, matching the runtime's initial `ThrowIfNull` before membership lookup or resolver execution. The exact public instance metadata overload returns `SiteUrl` from the same Core assembly. This argument check needs no receiver-existence, reachability or Closed-manifest proof. Unknown/getter values, invalidated locals and user conversions remain unproven; `!` preserves snapshots and original argument positions without proving a value. Null is never a missing route for LSA1201 or Complete coverage. Existing LSA1205 and opt-in LSA9001 keep their independent scope and whole-call policy locations, so a null argument can report LSA1002 together with an incomplete-coverage diagnostic. LSA1401 and its CodeFix remain direct-literal only.

`SiteUrl.FromRoute(SiteRoute route)` rejects argument 0 only when its nonempty finite set contains known null values exclusively, matching the runtime's initial `ThrowIfNull`. Null-forgiving syntax (`null!`, `default(SiteRoute)!`, or a known-null local followed by `!`) preserves those existing value snapshots. Explicit arguments are selected by `ArgumentKind`, since Roslyn can mark them `IsImplicit` when `!` is present; omitted defaults remain excluded. Diagnostics retain the original argument expression including `!`. An Unknown value set, null joined with Unknown, user-defined conversions, and local facts invalidated by calls/getters remain unproven, with or without `!`. This exact metadata registration checks both Core types and never evaluates a route, `PublicPath`, getter or user call; it does not certify a non-null route's contents, existence or ownership.

`SiteUrl.ForFile`, `SiteUrl.ForDirectory`, `SiteRoute.ForFile` and `SiteRoute.ForDirectoryIndex` validate the relative path and optional base URL independently, using the runtime SiteRoute source compiled privately into the analyzer. Traversal, reserved device names, invalid percent/UTF-8 encoding and invalid base URLs follow that source. Empty directory routes are valid.

The exact metadata instance `ContentRef<TEntry>.GetUrl(string? baseUrl = null)` also validates argument 0 through the same SiteRoute base URL guard. The generic containing type's original method definition and SiteUrl return type are checked separately; existing static registrations are unchanged. Omission and known null preserve the wrapper's existing route. Empty/relative base URLs and base URLs containing query or fragment are invalid, even when a general absolute URL guard would accept them. Receiver expressions are evaluated before argument snapshots; calls/getters that may change a local make that value Unknown. This rule reports only the argument contract, without proving receiver non-nullness, call reachability, entry existence or route ownership, and never executes a receiver/getter.

### LSA1003

`SiteAssetOutput(string id, string relativeOutputPath)` validates `relativeOutputPath`. `SiteAsset(string id, string inputRoot, string relativeInputPath, string relativeOutputPath)` also validates its relative input and output paths independently, at parameter ordinals 2 and 3. Both use the same pure `SiteRoute.NormalizeRelativeOutputPath` source as the runtime. These are filesystem names: percent text is literal, unlike route URI segments. Invalid static relative paths can be rejected without filesystem access. Input root absolutization, file existence, artifact ownership and registry completeness remain outside this registered scope. Unknown or mixed valid/invalid candidates do not produce an Error; named arguments retain their parameter identity and original evaluation-time snapshots.

### LSA1004

`SiteQualityOptions` validates the explicitly supplied `failureThreshold` with the runtime guard. `ExternalLinkCheckOptions` also validates all four constructor arguments with shared pure guards: a nonblank cache path, nonnegative cache duration, and bounded request interval/timeout. The timer maximum is 42949672940000 ticks; an interval may be zero, a timeout may not. A known null duration keeps the runtime default, while Unknown is never treated as null. Absolute cache paths are allowed; filesystem containment/reachability is outside this rule.

Duration witnesses come only from the compiler's actual core-library TimeSpan symbol: Zero/MinValue/MaxValue, the ticks constructor, FromTicks and supported integral unit/component factories. The host interprets checked integer ticks without calling those factories or user code. Fractional/nonfinite numbers, overflow, uncertain binary64 rounding, unsupported operators/overloads, user getters/factories and lookalikes remain Deferred. Existing candidate limits, evaluation-time snapshots, named-parameter binding and suppression apply. Deconstruction invalidates both scalar values and origins in the existing State; it cannot carry a stale path/duration into the new sink.

The finite flow contract is recorded in `static-value-contract.json`. Constants (including const fields), supported conversions, string concatenation, string-only unformatted interpolation, single local assignments and acyclic CFG branch joins may produce Known sets. Expressions are snapshotted at their evaluation point, so a later argument assignment cannot replace an earlier argument's value. Named arguments retain their source evaluation order; simple local assignment expressions return their snapshotted right-hand value. A set is capped at eight candidates. An Error requires **every** candidate to violate the selected argument guard. Mixed valid/invalid sets and Unknown branches do not produce an Error.

Properties, readonly/mutable fields, user calls, ToString/formatting, environment reads and ref aliases stay Unknown; no user code is executed. Calls/getters conservatively invalidate local and capture facts, including closures and ref/out escape. Any CFG back edge or exceptional region (including try/finally, using and lock) defers local/capture propagation; direct constants remain independently provable. Nested functions get independent states; outer captures stay Unknown. Unreachable CFG blocks are excluded. Budget exhaustion defers analysis without an additional coverage diagnostic. There is no global cross-compilation cache.

Locations use the original argument expression's UTF-16 span, including flow-derived values. No literal substring or origin offset is invented. Properties record Known, candidate count and the argument-span fallback. No diagnostic is a guarantee of runtime validity. Runtime guards still apply after suppression. Frontmatter/schema diagnostics remain owned by the Generator; this analyzer does not emit LSG001..006 or LSA1301..1302.

現在の開発候補は、Syntamark.Source [2.0.0-preview.3]のportable sourceを既定でコンパイルし、YamlDotNet [18.1.0]をpayloadへ同梱します。source-treeのビルドではDirectory.Build.propsが固定版を選び、SyntamarkSourceVersionの不一致を拒否します。canonical parserをrepoから直接Compileしたり、Syntamarkのruntime DLLをcompiler hostへ読み込ませたりしません。

### LSA1501

Warning identifies a bound use of the public, read-only instance
`LithoSharp.Testing.SiteTestDocument.Document` getter from metadata assembly
`LithoSharp.Testing`, returning metadata `AngleSharp.Dom.IDocument` from
`AngleSharp`. This registration also works in a Testing-only compilation without
Core URL, HTML or manifest sinks. No getter, factory, DOM method or user code runs.
Source lookalikes, v2 `Snapshot`, and unresolved `Document` syntax after upgrading
to v2 are outside this bounded rule. A warning does not certify that all legacy
DOM uses have been found.

Use the [Testing v2 migration map](../../docs/Testing-v2-api.md#migration)
to select `Snapshot.Query/QueryAll` or the retained assertion helpers. DOM mutation,
third-party casts/extensions and unsupported selectors require manual changes.
There is no automatic CodeFix or blind rename. Normal diagnostic suppression and
generated-code exclusion apply; runtime snapshot ownership remains unchanged.

### LSA1101

`Html.UnsafeRaw(string)` receives Info when its contents are Unknown to the bounded static flow. This reports unverified raw output without claiming a vulnerability. A known literal or supported known local is not reported. Ordinary strings and lookalike user APIs are excluded.

### LSA1102

An interpolated UnsafeRaw argument produces Error when explicit ToHtmlString output of a proven raw fragment is placed in a known quoted attribute. Direct raw-object interpolation emits its type name and no longer proves a fragment for this rule. An arbitrary IHtmlContent variable is not proven to be a raw fragment. The sealed HtmlText and HtmlAttributeValue wrappers use the runtime's HTML encoding and are permitted in that context.

### LSA1103

Interpolation in a known tag name, attribute name or unquoted attribute value produces Error. A preceding unknown hole invalidates the following context and prevents a definite Error there. Add an appropriate quoted attribute or change the template manually; there is no automatic quote fix.

### LSA1104

HTML encoding in a known script/style body, quoted event handler or style attribute produces Warning. Html.Encode and the two sealed encoded wrappers do not establish JavaScript/CSS safety. Choose appropriate serialization manually.

Local/capture aliases, built-in string concatenation and finite branches carry optional literal/typed-hole shapes on the existing evaluation snapshots. Scalar Unknown remains Unknown even when a shape is proven. Each sink requires the same original hole, rule and context across every alternative; independently proven sinks are unioned, with at most 16 deterministic related use locations. Missing shapes, escapes and budget exhaustion cannot leave a successful prefix. Deconstruction invalidates values and all optional proofs; ref/call/getter/loop/exception boundaries also drop the applicable shape and output-kind witnesses.

The exact template scope, supported output symbols, independent eight-alternative/513-piece budget, original source spans and Deferred cases are recorded in `html-template-context-spec.md`. Direct templates retain their 256-hole limit. Format/alignment now defer definite context diagnostics even at forbidden positions; no formatter is modeled. Suppress individual rules using normal compiler/editor configuration or a documented pragma after reviewing the output contract.

### LSA1105

Direct normal/verbatim/raw HTML literals and bounded built-in string additions made only of those literal leaves, passed to `Html.UnsafeRaw(string)`, use the same portable HTML bridge and pure LSQ001 URL checks as the runtime. Each token is decoded before concatenation; token text or escapes are never reinterpreted across a join. Error requires a Complete parse, an exact original URL span contained in one leaf, and a base-independent violation: an empty resource URL, malformed explicit absolute scheme, unsupported scheme or HTTP(S) userinfo. Cross-leaf URL/entity origins and ambiguous empty origins are Deferred per candidate. Entity decoding follows the bridge. Accepted `srcset` candidate URLs use the same scanner source as runtime; rejected descriptors and empty candidate lists add no URL Error. Each candidate's decoded range must map through that attribute's `HtmlText.Segments` and then the C# literal map; missing, discontinuous or safely unsplittable mappings leave that candidate Deferred. Relative/protocol-relative references, site membership, canonical/base policy, unsupported nonliteral leaves and other composite/dynamic inputs are Deferred. Partial/Failed parses never produce a definite literal URL Error. See `html-template-context-spec.md` for the exact contract and budgets.

このpreviewは未公開の開発候補です。全SDK/VS/C#拡張の認定やCodeFix出荷を示しません。

Source string constants, known locals/captures, aliases, finite branches and string-only interpolation also use the existing scalar evaluation snapshots with optional original-source pieces. Unknown values stay Unknown; origin loss or origin-budget overflow does not change scalar facts. Equal values retain all origin alternatives, including metadata-only or unmappable pieces. Deconstruction invalidates local/capture scalar values and origins. Additional ref escapes retain their existing conservative origin invalidation.

For each sink, LSA1105 requires the same original URL span, decoded URL, attribute role and LSQ001 issue in every known value and every origin alternative, with Complete HTML parses. Different-origin or mixed/Unknown branches, cross-piece origins and ambiguous empty origins are Deferred. Proven sinks are then unioned within the Compilation; they never intersect across uses. The primary location is the original definition URL and respects its pragma; up to 16 related use locations are ordered deterministically. No user getter/call/operator/formatter or Core runtime code is executed. This is a local URL proof, not whole-HTML or whole-site coverage.

Inline CSS in surviving unnamespaced `style` attributes and active `style` elements also uses the runtime CSS scanner source. Stylesheet text includes the existing top-level `@import` string projection; attributes do not. HTML entity decoding follows each parsed attribute/text chunk, including the distinction between HTML raw-text style and foreign-namespace text. Template content stays inert. CSS candidates map their raw token range through HTML segments and the existing C# origins; empty URLs require an unambiguous exact zero-length boundary. HTML and the relevant CSS projection must be Complete. CSS Partial or exhausted work/mapping leaves CSS Deferred without removing independent HTML URL diagnostics. The CSS sink budget is 262,144 steps, separately bounded from the existing HTML candidate mapping budget. No filesystem, external stylesheet, cascade/layout, base/site or asset membership is inferred.

### LSA1201

Explicit arguments, including null-forgiving `!`, use the existing evaluation snapshot after generated-origin, fingerprint and Closed-scope validation. `!` does not prove an Unknown value. Absence diagnostics cover the original argument expression, including `!` and parentheses, in UTF-16 positions. A known missing path reports LSA1201 without an LSA9001 coverage failure; known present or mixed present/missing strings also retain Complete coverage. LSA1401 and its CodeFix remain limited to a direct string literal.

Error applies only to `Guides.Manifest.GetUrl(path)` when `Guides` explicitly opts into `EmitStaticSiteManifest = true`, the generated property is verified against its runtime catalog initializer and current AdditionalFiles routes, and its compiler fingerprint is fresh. Every bounded known candidate must be absent from that exact immutable lookup. Mixed present/missing candidates, unknown arguments, receiver aliases, custom factories, metadata-only manifests and ordinary catalogs are Deferred. This lookup uses ordinal exact public paths; queries, fragments and URL resolution are outside its contract. A collection catalog does not establish site publication coverage. Anchors and assets remain Open.

### LSA1203

Explicit sink arguments, including null-forgiving `!`, use the same bounded instance-identity proof. Only `SuppressNullableWarningExpression` is exempt from unary mutation rejection; prefix and other postfix expressions remain rejected. Every local still passes the whole-owner reassignment, ref/out, closure and permitted-argument escape checks. Existing alias initializer forms and `SafeContext` are unchanged, so `ctx!` does not establish an owned receiver. Diagnostic locations cover the original argument including `!` and parentheses; related declaration locations and independent input/output scopes are preserved. This does not add LSA9001 scopes or change existing path guards.

When Roslyn gives no operation for a suppressed `ArgumentSyntax`, the local-use check resolves only the matching Explicit argument of its actual source invocation by source tree/span, then applies the existing permitted method, receiver and handler checks. This handoff supplies no identity or value evidence.

The initial scope is a transform's own copied input declarations. An exact metadata `SiteAssetTransform` constructor must receive an inline array/collection expression and an inline handler. Only that handler parameter's `SiteAssetTransformContext.GetUrl(SiteAsset)` and `OpenRead(SiteAsset)` are checked. Every bounded known input instance must be absent before an Error is reported at the original argument's UTF-16 span. A local alias of the same instance is accepted; equal IDs/paths on distinct instances are not membership. The declaration is a related location, and diagnostic properties name `asset-transform-inputs`, not site publication or filesystem coverage.

The independent output scope checks the same handler's exact `WriteAsync(SiteAssetOutput, ReadOnlyMemory<byte>, CancellationToken)` overload against the constructor's copied `outputs` declarations. The original output argument is primary, including named argument reordering; its declaration is related. Properties use `asset-transform-outputs` and `explicit-transform-outputs/1`. Equal output IDs/paths on distinct instances are absent, while an unchanged alias of the same instance is present. Unknown inputs do not discard a closed output proof, and unknown outputs do not discard a closed input proof. Each declaration and identity proof returns only from its own scope; shared context and owner safety requirements still apply to both.

Inputs and outputs are copied in the constructor body, after all arguments are evaluated. Local/shared arrays, array aliases and assignment expressions are therefore unsupported, including mutations before the call or in later arguments. Asset/output locals must have one source initializer and remain unchanged throughout the method, including after construction and inside deferred bodies. Reassignment, deconstruction, ref/out/escape, another closure, loops/statement control flow, unknown or mixed present/missing candidates, opaque declarations/handlers, foreign or nested contexts, binding failures and exceeded budgets remain Deferred. Only each inline handler's own context is used; nested handlers are analyzed separately. The limits reuse 8 candidates, 64 identity depth and 4,096 owner syntax nodes. Current compiler buffers are analyzed directly without cross-Compilation proof caching. Ordinary severity/NoWarn/primary-side pragma and generated-code exclusion apply.

No Core runtime code, user constructor/getter/factory/handler or file I/O executes in the compiler host. Runtime guards are unchanged. Output checking concerns membership only; it does not infer duplicate writes, unwritten outputs or whether a write executes. `GetUrl(SiteAssetOutput)`, arbitrary page/layout registries, generated/import assets, public-directory files and general HTML/Markdown asset URLs remain outside this subset. Absence of LSA1203 does not certify a site asset universe. LSA9001's policy scope is unchanged.

### LSA1205

Collections with an explicit static constructor are Unavailable, including an empty constructor or a constructor in another partial declaration. C# permits that constructor to reassign static get-only `Manifest` and `Catalog` auto-properties. The Analyzer does not infer immutable membership from their original initializer in that case.

Info identifies a generated Closed lookup whose fingerprint differs from current compiler inputs. Route absence is then Deferred. Fingerprints use original C# buffers and parse symbols, AdditionalText buffers and collection/id/route/site/variant metadata, project root/target framework/configuration/profile, and assembly identities/MVIDs/reference aliases. No disk scanning, network access or user code runs. Unreadable inputs, unsupported references or budgets (8,192 source/additional inputs; 8,388,608 framed text units) are Unavailable and defer absence. Other generators' source additions can conservatively make a lookup Stale. The known StaticContentGenerator output path is excluded from its own fingerprint; its marker is an origin convention, not a security attestation.

Normal Roslyn per-ID severity, editor configuration and original-source pragmas apply. LSG004 owns route collisions; LSG005/006 own YAML/schema diagnostics. These errors cause emitted lookup coverage to be Open and are not repeated as LSA1204/1301/1302. LSA1401 and its separate LithoSharp.CodeFixes action have the limited contract above. LSA1202/1204, LSA1203 beyond explicit transform inputs/outputs, LSA130x, whole-site publication coverage and other CodeFix actions remain unimplemented. LSA1403 has the selected producer subset below.

### LSA9001

RequireStaticCoverage is an optional compiler policy, disabled by default. Enable it with standard Roslyn configuration, for example `dotnet_diagnostic.LSA9001.severity = error` in `.editorconfig`, or the corresponding per-ID compilation option. Other severities, NoWarn and original-source pragmas retain their normal meaning; this policy does not change runtime guards.

The initial scope is the exact metadata `StaticSiteManifest.GetUrl(string)` request and explicit `SiteGenerationOptions.StaticRouteGuaranteeProducers` assignments. Fresh Closed lookups with known string candidates and source-proven empty producer selections need no policy diagnostic. Open, Stale, Unavailable, unknown candidates and exhausted proof budgets report the original request with the scope and unverified reason. This is neither a missing-reference assertion nor evidence that an opaque producer is dynamic. LSA1201/1205/1403 and Generator-owned schema diagnostics keep their existing meanings and locations.

Original operation callbacks discover requests independently of bounded CFG evaluation. Block/operation/nested-graph exhaustion cannot silently remove the opt-in notification. Untracked receivers, opaque/unregistered/branched producer selections and unknown identity remain Unavailable. An explicit empty array or collection expression is settled before registration, fingerprint and CFG requirements, including when those are unavailable. No getter, factory or runtime facade is executed. Anchor/asset or whole-site coverage, general unknown strings and unregistered/lookalike APIs are outside this policy's scope; no CodeFix or public runtime API is added.

### LSA1402

An explicit Core `ContentRef<T>` can select a generated catalog through its `Catalog.CollectionId` property. If exactly one verified fresh Closed manifest belongs to that same catalog, an existing constant entry ID paired with a different valid route reports an Error. The public path is normalized by the existing `SiteRoute.ForFile` or `ForDirectoryIndex` implementation and compared ordinally. Constructor arguments are bound by their parameter ordinal, so named argument order does not change the proof.

Only a compiler-known null base URL is supported; an unknown value whose `ConstantValue.Value` happens to be null is Deferred. The entire original route argument expression (ordinal 2 of the outer `ContentRef<T>` creation) is the primary diagnostic location. The entire original ID constant expression (ordinal 0 of the inner `ContentEntryId` creation) is the related location; it is not widened to the outer entry argument. `!` and parentheses are included only after the argument's source tree/span owner matches its own creation; an unproved mapping retains the existing operation location. This mapping supplies no value or provenance evidence. The diagnostic carries the same compiler provenance as the manifest rules and respects primary-side pragmas. No getter, initializer or user factory is executed.

Correct pairs, missing/unknown IDs, ambiguous scopes, Open/Stale/Unavailable manifests, handwritten catalogs, selector aliases, non-null or unknown bases, dynamic routes and user-defined conversions do not produce this Error. Invalid route syntax remains owned by LSA1002. Opaque `PageRef` IDs and other typed reference forms are outside this subset. No CodeFix is provided, and route lookup coverage is not whole-site publication, anchor or asset coverage.

### LSA1403

`SiteGenerationOptions.StaticRouteGuaranteeProducers` selects the same instances already registered in `Extensions`; it adds no registration or Prepare call. Both lists must be explicit arrays or collection expressions in the same directly constructed `SiteDefinition.Options`. Repeated selected instances count once. Straight-line, unchanged local identities and aliases can bind the same instance. A nonregistered selection, getter/factory selector, control-flow ambiguity or exceeded budget makes the request Unavailable, not a complete guarantee.

The initial recognized Deferred producer is the metadata `LithoSharp.Mdx.DocumentationSite` with a preceding, directly bound `AddCollection(new DocumentationCollection(...))` registration on that same instance. Its route contribution requires runtime preparation and is not enumerated from compiler inputs. Runtime value variation is not asserted. A source producer whose exact `PrepareAsync` returns `Task.FromResult(new SiteBuildContribution(...))` with a provably empty `ContentCollections` contributes no Deferred item. Other producer contracts/bodies are unsupported and are not converted from analysis Unknown into dynamic producer evidence.

One default Info reports the number of distinct selected, registered documentation contribution items. The original selection is primary, producer creation locations are related, and the current compiler-input fingerprint records provenance. It does not count runtime pages/routes or mark a Catalog/site Open. Missing Info does not imply Closed coverage, including for unsupported, unregistered or budget-limited selections. The lists are bounded to 64 items and identity/source inspection uses existing depth/operation budgets. No user getter/factory/constructor, producer, filesystem or runtime assembly is executed by the Analyzer. Existing scalar flow still handles Known locals and owns LSA1002 route errors; those calls are not producer evidence or Deferred items. Standard severity/NoWarn/pragma applies; there is no CodeFix. Anchors/assets/final HTML, nonselected producers, Posts/ExtraPages and whole-site coverage are outside this scope.
