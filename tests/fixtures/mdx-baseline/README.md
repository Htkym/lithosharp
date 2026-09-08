# Pinned MDX reference

This is a development-only Docusaurus 3.10.2 comparison site and official MDX
toolchain check. It is not the LithoSharp MDX worker or a browser hydration test.

Use Node 24.13.0 and npm 11.6.2, then run from this directory:

```sh
npm ci --ignore-scripts --no-audit --no-fund
npm run build
npm test
```

Only `ci` restores dependencies. `build` generates a production reference at
`/product/` for `en` and `ja`; Japanese uses the same source fallback, not a
claimed translation. Broken links fail the build. `test` checks the single
React lock entries, named MDX exports, hydration-capable server rendering,
browser bundling and the reference static Counter HTML. It does not simulate a
click or certify hydration. Future AT-05 tests must click the production page
and check that Count 3 becomes Count 4 with no hydration warnings.

Review lockfile, license and test changes together when updating dependencies.
Do not add this dependency set to ordinary Markdown builds or Core packages.

日本語: Docusaurusの比較用サイトと公式MDX処理系の検証fixtureである。復元を明示的に
実行し、その後に静的サイトをビルドして検査する。LithoSharpのMDX対応やブラウザーでの
Hydrationを検証済みと扱わない。詳細は `docs/ssg-followup-baseline.md` を参照する。
