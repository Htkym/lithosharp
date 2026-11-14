# Build graph

[日本語](build-graph.ja.md)

`SiteGenerationResult.BuildPlan` describes the artifacts written by this generation
and their direct inputs. See [incremental builds](incremental-builds.md) for cache
reuse and selective execution.

## Built-in templates

`DocsSiteTemplate` and `BlogSiteTemplate` use separate nodes for pages and shared
artifacts. Owners of Markdown pages, extra pages, CSS, JavaScript, listings, search,
RSS, sitemap, `llms.txt`, favicon, manifest and social images match actual output files.

Docs navigation is a collection input containing published page routes, labels,
ordering and extra-page navigation entries. Each Docs page depends on that node.
Adding or removing a page, or changing its title, route or sidebar configuration,
therefore invalidates outputs that use navigation and adjacent-page links.

Blog listings, archives, tags, search indexes, RSS and sitemaps each depend only on
the page entries they actually use. A body-only edit invalidates the post page,
search index and search page. Date and tag changes also invalidate aggregates that
use those fields. The search page depends on the search-index node and includes
the SHA-256 of the index's emitted UTF-8 bytes in its cache key.

Favicon inputs retain the source content's SHA-256. Value inputs compare declared
code points without Unicode normalization. To track content through the public
API, use `BuildInput.FromFile(path, fingerprint)` or
`BuildInput.FromCollection(id, fingerprint)`. Supply a nonempty deterministic
fingerprint; values are compared without normalization or trimming.

## Markdown and typed-page inputs

Markdown body inputs use an exact text hash independently of semantic information.
A body edit is an input change even if its meaning is unchanged. Actual cache keys
also include the generator implementation identity and Markdown compiler
fingerprint. Comparing build plans alone does not authorize persistent cache reuse.

Typed collection pages track content, metadata, renderer identity and declared
dependencies. Caching requires explicit `IsCacheable` and `RendererFingerprint`
declarations. Reuse checks Markdown diagnostic records as well as rendered bodies
and artifacts. See [incremental builds](incremental-builds.md) for declarations
and diagnostic restoration.

## Custom templates

An arbitrary `ISiteTemplate` has unknown internal dependencies, so it is represented
as a single always-executed `template:legacy-opaque` node. The generator's shared
artifacts belong to `generator:common` and depend on that opaque node.

## Invalidation report

`SiteBuildPlan.GetInvalidatedNodes` compares the previous and current validated
plans. It checks inputs, dependencies, artifact declarations and propagation from
dependency nodes, then returns node IDs and reasons in ordinal order. Removed
nodes are absent because they are not execution targets in the current plan.
Persistent caching and file deletion are handled in later stages.

## Generation report

`SiteGenerationResult.BuildReport` records build time, environment, template type,
owned artifacts per node, invalidation reasons and generated, unpublished and stale
artifact removal results deterministically with relative paths.
`SiteGenerationOptions.PreviousBuildPlan` adds a comparison with the current plan.
The report is currently an in-memory API. It is not automatically saved into the
output tree, avoiding overwriting user-managed files and preserving existing output
and state after failure or cancellation.
