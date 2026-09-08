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
click or certify hydration. Production hydration is tested separately in
[`../mdx-browser`](../mdx-browser).

Review lockfile, license and test changes together when updating dependencies.
Do not add this dependency set to ordinary Markdown builds or Core packages.

日本語: Docusaurus の比較用サイトと公式 MDX 処理系を検証する fixture である。
依存関係を明示的に復元し、静的サイトをビルドして検査する。
ブラウザーでの Hydration の動作は、別の `../mdx-browser` fixture で検証する。
