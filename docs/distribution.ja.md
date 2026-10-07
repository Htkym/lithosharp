# 配布

[English](distribution.md)

何を配布し、どこで動作させ、誰が承認するかを説明する。拡張機能は既存のMarketplace publisher
`htkym`を使う。公開は未実施であり、リポジトリの設定と別途のリリース承認が必要である。
この資料は公開承認を与えるものではない。

## NuGet package

7 packageを同一version 1.1.0で配布する準備をしている。`LithoSharp`、`LithoSharp.Generators`、
`LithoSharp.Images`、`LithoSharp.Tool`、`LithoSharp.Testing`、`LithoSharp.Mdx`、
`LithoSharp.ProjectTemplates`である。test専用の`LithoSharp.FixtureExtension`は
packしない。`eng/Validate-Package.ps1`が内容・metadata・version整合・Markdig
禁止を検査し、`eng/Test-PackageDistribution.ps1`がpack・検証・隔離feedからの
tool/template導入・packageのみのtemplate site buildを通す。

`v*`系列のtag（`v1.1.0`）だけが`.github/workflows/publish-nuget.yml`を駆動する。
下記の拡張tag系列はこのglobに一致しない。

## 言語server

server（`src/LithoSharp.LanguageServer`、version 1.1.0）はframework-dependentで
出す。`dotnet LithoSharp.LanguageServer.dll`に`deps.json`、`runtimeconfig.json`、
managed依存、RID別native資産、worker source（`worker/*.mjs`、`runtime/`、
`package.json`、`package-lock.json`）を添える。restore済み`node_modules`は
同梱しない。trimming・単一file・AOTはproject fileで無効であり、flag運用に
頼らない。`eng/Test-LspDistribution.ps1`が配置と、初期化・Markdown診断・
Nodeなし/worker未復元時の縮退を検査する。serverはNuGet packageにせず、
VSIXまたは手動導入で運ぶ。

## VSIX

Windows x64・Linux x64・macOS arm64で同一のportable VSIXである。
compile済み拡張、manifest、文書、framework-dependentな言語serverとRID別native資産、
MDX worker sourceとlockfileを同梱する。`.local`・secret・log・資格情報・
開発用`node_modules`・fixture・testは含めない。`eng/Test-VsixContents.ps1`は
lockfileで固定したVSCE 4.0.0を使い、禁止内容・必須項目・stagingとのpayload hash・
version・publisherの整合を検査する。その後、実VSIXから展開した言語serverで
3件のsmokeを行う。local導入手順は[Golden Path](golden-path.ja.md#4-vsix導入local)を
参照する。pack（`vsix:pack`）は公開しない。

## MDX workerの復元

restore済みnpm依存はmachine固有の状態なので、VSIXは`node_modules`ではなく
sourceとlockfileを持つ。`LithoSharp: Restore MDX Worker`が同梱sourceを
hash付き拡張storage（`worker-<lockfile12>`）へ写し、`npm ci --ignore-scripts
--no-audit --no-fund`を実行する。利用者project・global npm状態・利用者npm
設定には触らない。`lithosharp.workerDirectory`で上書きでき、変更後は
`LithoSharp: Restart Language Server`が必要である。未復元でもMarkdown診断は
動き、MDXは理由付きで出ない。

## tagと承認

- Core：`v1.1.0`系列 → `publish-nuget.yml`（pack・検証・OIDC push）。
- 拡張：`extension/lithosharp-vscode/0.1.0`のpushは`publish-extension.yml`への
  公開要求になる。tagは`package.json`のversionと一致し、そのcommitは`main`に
  含まれている必要がある。workflowは、必須reviewerが1人以上設定された
  `marketplace`環境を確認してから進む。
- 既存のinstalled-extension workflowがpublisher付きVSIXを1個作り、同じ実体を
  3 OSで導入して検証する。全jobの成功と環境reviewerの承認後、公開jobが
  producerのSHA256・source commit・version・publisherを照合する。
  VSCE 4.0.0へそのVSIXを渡し、compile・staging・再packは行わない。
- 手動dispatchの既定値`publish=false`は検証のみ行う。`publish=true`には
  対応する拡張tagが必要であり、branchでのdispatchは失敗する。
- `eng/Test-ReleasePipeline.ps1`はtrigger分離と公開条件を検査する。
  `eng/Test-ExtensionPublishGuards.ps1`は環境承認条件とartifactをofflineで検査し、
  誤tag・改変したbytes・identity不一致の拒否も確認する。

## 初回Marketplace公開の前に必要な設定

1. 公開担当者が`htkym`として公開できる権限を持つことを確認する。拡張機能IDは
   `htkym.lithosharp`であり、`VSCE_PUBLISHER`変数の設定は不要である。
2. GitHubの`marketplace`環境に必須レビュアーを1人以上設定する。
   レビュアーが設定されていない環境を作成しただけでは公開できない。
3. このrepositoryまたは同環境のsecretに、有効な`VSCE_PAT`を登録する。
   PATにはMarketplace Manage scopeとpublisherへの権限が必要である。
   他repositoryのsecretは自動では共有されない。tokenはsource・PR・log・chatに書かない。
4. レビュー済みPRをmergeし、別途release承認を得てから拡張tagを作りpushする。
   tagのpushは公開要求になる。

[VS Code公式の公開手順](https://code.visualstudio.com/api/working-with-extensions/publishing-extension)は、
PATによる公開方法と、2026年12月1日のAzure DevOps global PAT廃止を案内している。
今回のworkflowは既存SharpDepsと同じPAT経路を準備した。廃止後のidentity認証への
切り替えには、別途の設定変更とレビューが必要である。このscriptは資格情報や
権限を設定しない。

## 監査範囲

NuGet（`dotnet list package --vulnerable`）、npm（拡張とworkerの`npm audit`）、
worker依存license（全MIT）を配布runごとに確認する。結果と例外はtask memoに
残し、常設の「脆弱性0」主張はしない。NuGet分は`THIRD-PARTY-NOTICES.md`が
対象を定める。
