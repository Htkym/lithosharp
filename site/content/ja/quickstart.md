---
title: クイックスタート
supported_version: "4.0"
prerequisites: [introduction]
sidebar_position: 2
---

ドキュメントサイトを作成し、ページを編集してローカルでプレビューします。

## 準備するもの

.NET 10 SDK の 10.0.300 以降をインストールしてください。
この例では Markdown のみを使うため、Node.js や React は不要です。

以下のコマンドは新しい作業ディレクトリで実行してください。
既存のサイトに重ねてプロジェクトを作成しないでください。

## インストールしてサイトを作成する

```sh
dotnet new install LithoSharp.ProjectTemplates::4.0.0
dotnet tool install LithoSharp.Tool --version 4.0.0 --tool-path .tools
.tools/lithosharp new docs MyDocs -o MyDocs
```

Windows では `.tools/lithosharp` を `.tools/lithosharp.exe` に置き換えてください。
テンプレートはユーザーのテンプレート一覧に、CLI は作業ディレクトリ内の `.tools` にインストールされます。
LithoSharp のパッケージ、テンプレート、CLI は同じバージョンにそろえてください。

## 最初のページを編集する

`MyDocs/content/getting-started.md` を開き、内容を次のように置き換えます。

```markdown
---
title: はじめに
description: 最初のドキュメントページです。
order: 1
---

# はじめに

ドキュメントサイトへようこそ。

## 最初の作業

このページを編集して、プレビューで確認します。
```

生成されたプロジェクトでは、次のファイルで主な設定を管理します。

- `GuideFrontMatter.cs`: 必須のタイトルなどを定義する C# のコンテンツスキーマ
- `content/`: Markdown のページ
- `DocsSiteFactory.cs`: サイト名、ベース URL、コンテンツの読み込み、レイアウト

## ビルドしてプレビューする

```sh
.tools/lithosharp build MyDocs -c Release
.tools/lithosharp serve MyDocs -c Release --port 4317
```

ホームページは `http://localhost:4317/`、編集したページは
`http://localhost:4317/guide/getting-started/` で確認できます。
開発サーバーはサイトをビルドし、ファイルの変更を監視します。終了するには Ctrl+C を押してください。

## 公開の準備をする

`DocsSiteFactory.cs` でサイト名と公開先のベース URL を設定し、再度ビルドします。
Docs テンプレートは出力の品質チェックが有効になっており、エラーがあればビルドに失敗します。
外部サイトへの HTTP チェックは無効です。

生成された `MyDocs/dist` ディレクトリを静的 HTTP ホストに配置してください。
上記のコマンドだけでは、サイトは公開されません。

## 次のステップ

- [型付きコンテンツ](../typed-content/): スキーマの定義と生成された参照の使い方
- [ドキュメントと多言語対応](../documentation/): ナビゲーションと翻訳ページの整理
- [ツール](../tooling/): CLI とエディター連携
- [運用と検証](../operations/): 検証と公開の手順