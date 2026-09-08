# 0.2.0から0.3.0への移行

[English](migration-0.3.md)

0.3.0は0.x系列のstable候補です。1.0相当のAPI安定性は保証せず、今後のminor
releaseでは公開APIが変わる可能性があります。公開済みの0.2.0は置換もunlistもしません。

## Markdownサイトの更新

更新前:

```xml
<PackageReference Include="LithoSharp" Version="0.2.0" />
```

更新後:

```xml
<PackageReference Include="LithoSharp" Version="0.3.0" />
```

`dotnet restore --force-evaluate`を実行し、lockfileの差分を確認してからビルドします。
最初は新しい出力ディレクトリへ生成し、比較後に配置してください。両版とも.NET 10を
対象とし、候補版はSDK 10.0.300で検証しました。MarkdownだけならNode.jsとReactは不要です。
CoreにはAngleSharp 1.7.3が加わります。Markdig 1.3.2、YamlDotNet 18.0.0、
SkiaSharp 3.119.4とそのLinux native packageの依存版は変わりません。

## ソースとバイナリの互換性

実NuGet 0.2.0を基準としたpackage validationでは、公開API署名の削除や非互換な変更は
ありませんでした。旧reader、generator、Docs、Blog、custom templateを使う独立consumerは、
ソースを変更せず0.3.0でコンパイルできました。0.2.0でコンパイル済みのバイナリも、
0.3.0のruntimeと依存関係で3種類のサイトを生成できました。obsolete警告の追加と
namespace移動はありません。検証したAPIと入力に対する結果であり、あらゆる拡張を保証するものではありません。

`MarkdownPostReader`、`SiteGenerator.GenerateAsync`、`SiteCustomization`、`ISiteTemplate`は
引き続き使えます。既定テンプレートはDocsのままです。Blogには`BlogSiteTemplate`を明示します。
既存Core APIを必須の別パッケージへ移す変更はありません。

## 出力と挙動の変更

移行fixtureでは、Docs、Blog、custom templateの公開成果物33件のパスが維持されました。
Unicodeを含む階層route、追加ページ、CSS、JavaScript、search、RSS、sitemap、`llms.txt`を
含みます。本文、リンク、主要landmarkも維持されました。ただし出力バイト列は互換性の保証対象外です。

- テキスト出力のUTF-8 BOMがなくなり、改行が正規化されます。
- 存在しないsocial imageへのOpen GraphとTwitterの画像メタデータを出力しなくなります。
  画像がなければTwitter cardは`summary`になります。
- searchの日時には解決済みのbuild timestampを使い、indexのqueryには内容のfingerprintを
  使います。旧query値を日時として解析しないでください。
- 出力のownership metadataが加わります。非公開のcacheやownership fileを編集したり、
  サイトのコンテンツとして配布したりしないでください。

再現可能な生成には`SiteGenerationOptions.BuildTimestamp`または有効な`SOURCE_DATE_EPOCH`を
指定します。不正な日時設定は、現在時刻への暗黙の代替ではなくエラーになります。

公開条件のfieldが有効になります。`draft: true`、未来の`publish_from`、過去の`publish_until`、
現在の環境を含まない`environments`は、投稿を出力と派生成果物から除外します。既定の環境は
`Production`です。NuGet 0.2.0はこれらを無視しており、4種類のprobeでは公開投稿数が1から0へ
変わりました。別の意味のmetadataとして使っていた場合は、更新前にkey名や値を修正してください。
`PostCount`は公開件数を表します。これらの入力には挙動上のbreaking changeがあります。

安全性検証の強化により、以前は通った危険な入力が拒否される場合があります。検証した`ftp:`の
base URLは0.2.0で通り、0.3.0で拒否されました。routeは出力配下に
収め、予約名、曖昧なencoding、大文字小文字だけの衝突、fileとdirectoryの衝突を避けます。
入力、公開asset、cacheを出力と重ねることもできません。[route契約](compatibility-contract.ja.md)と
[assetの規則](assets-and-images.ja.md)に従って入力を修正してください。こうした入力に依存している
場合は、挙動上のbreaking changeとして扱います。大文字小文字だけの出力衝突は旧版も拒否していましたが、
例外は構造化された`SiteRouteValidationException`になります。`InvalidOperationException`の派生型です。
例外の厳密な型や旧messageを比較しているコードは見直してください。

従来のYAML readerは未知のkeyを許容する挙動を維持します。duplicate keyとunknown keyの
厳密な診断は、明示的に使うtyped collection binderとMDX/documentation loaderの機能です。
移行時は宣言したschemaへ入力を合わせてください。既存設定が自動変換されるわけではありません。

## Cache、theme、任意の機能

初回の比較には新しい出力先を使ってください。`clean: false`では、無関係なファイルやownershipが
確認できない旧出力を保持します。更新だけで0.2.0の古い成果物がすべて消えるとは限りません。
通常の増分生成では、ownership sidecarを対応するローカル出力と一緒に保持します。
cacheは再生成でき、ownershipの証拠には使いません。詳細は[増分ビルド](incremental-builds.ja.md)を参照してください。

既存themeのCSSとsemantic landmarkには[layout契約](layout-css-contract.md)が適用されます。
独自selectorとsnapshotは、HTMLのバイト一致ではなく意味を比較してください。search、versioned
Docs、locale variantは明示設定です。既存サイトの更新だけで有効化されたり、`.html` URLが変わったりはしません。

任意の`LithoSharp.Generators`、`LithoSharp.Images`、`LithoSharp.Testing`、`LithoSharp.Mdx`、
`LithoSharp.Tool`、`LithoSharp.ProjectTemplates`は0.3.0で提供します。このreleaseで新たに公開する
package IDであり、0.2.0のCore consumerに追加必須ではありません。利用する版は揃えてください。
生成コードは`AdditionalFiles`から再生成します。generatorには`PrivateAssets="all"`を指定し、
build診断を確認してください。

CLIとtemplateは任意の新しい入口です。[Quick Start](quickstart.ja.md)を参照してください。
既存のconsole applicationも使えます。MDXに限りNode.js 24.13.0を用意し、
`lithosharp restore-mdx`で同梱workerを明示的にrestoreします。lockfileはMDX 3.1.1、
React 19.2.4、esbuild 0.25.12を固定しています。通常のサイト生成ではnpm packageをinstallしません。
MDXは信頼するコードとして実行します。有効化の前に[既知の制約](known-limitations.ja.md)を確認してください。

## 元に戻す場合

package参照とlockfileを0.2.0へ戻し、再ビルドして別の空のディレクトリへ生成します。
新しい版のoutput transactionやcacheへ古いwriterを実行しないでください。配置を戻す場合は
assetを含む一式を復元し、開いたままのページが参照するhash付きassetを保持します。
追加された任意APIやMDXサイトはCore 0.2.0では動作しません。
