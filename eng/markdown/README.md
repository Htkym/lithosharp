# Fixed Syntamark pair

Normal verification is offline:

```sh
node --test eng/markdown/Verify-FixedPair.test.mjs
node eng/markdown/Verify-FixedPair.mjs verify-fixed eng/markdown/feed
```

Stable selection requires the exact version in `eng/version-matrix.json`, a
successful formal `Htkym/syntamark` producer run on main, its reviewed sealed
manifest SHA256, the matching stable source tag, and both packages published on
NuGet.org. Ingestion downloads the actual producer workflow artifact and official
NuGet archives. Supply a new stage directory:

```sh
node eng/markdown/Verify-FixedPair.mjs stage-published-stable <version> <producer-run-id> <reviewed-manifest-sha256> <new-stage>
```

The result is `STAGED_NOT_ACCEPTED` with consumer acceptance `NOT_RUN`. The stage
records official raw archive hashes and preserves the producer ZIP for later
offline verification. NuGet may add `.signature.p7s`; all original payload entries
must match the sealed pair. Signature presence does not verify a signature.

After reviewing a real published stage:

1. Select its `component-pair.json`, `release-provenance.json`,
   `publication-artifact.zip`, and both `feed/*.nupkg` archives in this directory.
   Preserve the prior immutable pair and its evidence.
2. Align `Directory.Build.props`, `eng/version-matrix.json`,
   `MarkdownYamlIdentity.cs`, any parser-stamp assertions, and the affected owned
   NuGet lock files with that exact selection. Generate locks from the published
   archives; keep runtime and Source paired.
3. Run `eng/markdown/Test-FixedPairConsumers.ps1`. It reuses strict package XML
   validation, the actual rebuilt Generator, and the existing Artifacts and
   Portability consumers with a fresh short temporary package cache. Each attempt
   has a separate JSON evidence directory under `artifacts/markdown-pair-consumers`.
4. Run the required normal CI, including compiler, template, Golden Path, site,
   and installed-extension consumers. Check the final LithoSharp package set with
   `eng/Test-PackageDistribution.ps1`; publication uses `-SkipPack` on the exact
   final archives already copied into its output `feed` directory.
5. Run the live publication check before formal acceptance:

   ```sh
   node eng/markdown/Verify-FixedPair.mjs verify-published-stable eng/markdown/feed
   ```

The live check verifies the qualifying producer run, actual stable tag, and exact
NuGet.org archive bytes against the reviewed saved producer ZIP and receipt. It
does not need an unexpired Actions artifact. Package publication verification and
consumer acceptance remain separate checks.

Keep the active preview pair until those published inputs and selection checks
are available. These commands do not merge or publish either repository.
