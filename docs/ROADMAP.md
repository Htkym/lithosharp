# Roadmap

PageSharp は `Htkym/daily-update` から切り出した汎用静的サイトジェネレータです。
公開できる状態までは整っており、汎用ライブラリとしての基本的な整理はひと通り終えました。
ここから残るのは、設計判断が必要な拡張と、公開運用まわりの外部設定が中心です。

## 対応済み

0.x のうちに進めてきた整理のうち、次は対応済みです。

- `SiteSettings` の既定値を中立化した（`Title="My Site"`、`Language="en"`、`Author` 空、
  `TimeZone="UTC"` など）。利用側が設定で上書きする前提。
- `SiteText` の既定（`SiteText.English`）を中立な英語ロケールに整理した。切り出し元の
  日本語文言は `SiteText.Japanese` にロケール例として残してあり、利用側は
  `Text = SiteText.Japanese` で選べる。
- 検索 UI のステータス文言を `SiteText` 経由に変えた。これまではスクリプトに日本語を
  直書きしていたが、英語・日本語どちらのロケールでも切り替わるようになった。
- 公開 API の XML ドキュメントコメントを英語化した。`GenerateDocumentationFile` が
  有効なので、IntelliSense もそのまま英語になる。
- 生成物の主要パス（feed、sitemap、search-index、メタタグ、検索スクリプトのロケール）に
  対する回帰テストを追加した。
- front matter のスキーマ（`title`、`date`、`summary`、`tags`、`sources`、`type`）を
  README にドキュメント化した。`sources` の `type` が取り得る値も説明している。
- `PackageIcon`（`src/PageSharp/icon.png`）を追加し、`dotnet pack` の内容
  （README・アイコン・シンボルパッケージ）を確認した。
- community health files（CONTRIBUTING、CODE_OF_CONDUCT、SECURITY、Issue / PR テンプレート、
  dependabot）を追加した。

## 今後の検討（設計判断が必要）

利用層や出力への影響が大きく、方針を決めてから着手したい項目です。

- OG 画像生成（SkiaSharp）の扱い。画像が不要な利用者向けに任意機能として分離するか、
  フォント同梱で出力を決定的にするかを検討する。`SocialImageGenerator` はシステム
  フォント依存で、フォントが無い環境では CJK が豆腐になる。
- テーマ拡張が `AdditionalCss` の上書きのみ。配色パレット用のトークンなど、構造化した
  拡張点を足すかを検討する。
- ターゲットフレームワークの方針。現在は `net10.0` 単独（出力ドリフトを避けるための選択）。
  利用層を広げるなら `net8.0` / `net9.0` へのマルチターゲットを検討する。使用言語機能は
  C# 11/12 相当で net8.0 でも動く。マルチ化する場合は各 TFM の `packages.lock.json` を
  再生成する。

## 出力バイトの安定性に関する注意

切り出し元の daily-update は当面 PageSharp の生成出力に依存します。次を変えると出力
バイト列が変わり、消費側の見た目や差分検証に影響します。

- テンプレートの改行コード（raw string は LF）と書き込み時のエンコード
  （`File.WriteAllText` は UTF-8 BOM）。
- ターゲットフレームワークの変更やテンプレート整形、ファイル書き込み方法の変更。
- 既定ロケールの中立化。既定文言が英語になったため、日本語のままにしたい利用側は
  `Text = SiteText.Japanese` を渡す必要がある。

汎用化の各変更では、消費側のサイト出力への影響をあわせて確認するのが安全です。

## NuGet 公開の準備

ワークフロー `.github/workflows/publish-nuget.yml` は `v*` タグ契機・OIDC trusted
publishing で組んであります。リポジトリ内の準備（`PackageIcon`、pack 内容の確認）は
済んでおり、残るのは外部設定です。

- nuget.org で trusted publisher（OIDC）ポリシーを `Htkym/pagesharp` とワークフロー
  `publish-nuget.yml` に紐づけて作成する。
- リポジトリ変数 `NUGET_USER` に nuget.org のユーザー名を登録する
  （ワークフローが `vars.NUGET_USER` を参照する）。
- 公開前チェックとして、空のプロジェクトから `PackageReference` で参照し、実際にサイトが
  生成できることを確認する（pack 内容とサンプル実行は確認済み）。

初回リリースの手順:

1. `main` を公開可能な状態にする。
2. バージョンタグ `v0.1.0` を打って push する。
3. publish ワークフローがタグからバージョンを取り、`dotnet pack` から
   `dotnet nuget push` までを実行する。

## リポジトリ体裁

- community health files は追加済み（CONTRIBUTING、CODE_OF_CONDUCT、SECURITY、
  Issue / PR テンプレート、dependabot）。
- SourceLink は `PublishRepositoryUrl` 経由で有効化済み（nuspec に repository commit が入る）。
- SkiaSharp はネイティブ依存。`build.yml` のサンプル実行で Linux 動作はある程度確認できるが、
  OG 画像のフォント差異には引き続き注意する。
- README のバッジは、build は初回ワークフロー実行後、NuGet は初回公開後に有効化される。

## 消費側（daily-update）の追従

このリポジトリの範囲外ですが、全体像として残るのは次です。

- 既定ロケールの中立化により、daily-update が既定の日本語文言に依存していた箇所は出力が
  変わる。`Text = SiteText.Japanese` を渡すなどして追従する。
- PageSharp 公開後、daily-update を in-repo のプロジェクト参照から `PackageReference` へ
  切り替える、あるいは `src/PageSharp` を撤去する。
- 切り替え後、daily-update のサイト出力が想定どおりであることを確認する。
