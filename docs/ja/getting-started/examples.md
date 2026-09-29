---
title: サンプル集
description: リポジトリ同梱の実行可能なサンプルアプリ。
---

`example/` ディレクトリに実行可能なサンプルがあります。リポジトリルートから実行してください。

## ファイル保存のコンソールアプリ

`Example.ConsoleApp` は `ConfiglueApp.CreateContext` で生成モデルを登録し、JSON ファイルリソースを `GetOptions<T>()` で読み書きします。

```sh
dotnet run --project example/Example.ConsoleApp
dotnet run --project example/Example.ConsoleApp -- --set-name Ada
```

設定ファイルは実行ファイルの横に書き込まれます。

## ファサードを使わない低レベル構成

`Example.SimpleApp` は `ConfiglueApp` ファサードを使わず、`ConfiglueOptions<TModel, TFragment>` と `StateSourceSet<T>` を直接組み立てて同じ流れを示します。

```sh
dotnet run --project example/Example.SimpleApp
dotnet run --project example/Example.SimpleApp -- --set-name Ada
```

## Worker Service (DI)

`Example.WorkerService` は Generic Host に Configlue を登録します。バックグラウンドワーカーが設定を読み、`RunCount` を増やして5秒ごとに保存します。Ctrl+C で停止します。

```sh
dotnet run --project example/Example.WorkerService
```

## HTTP ポリシーつき複数ソース

`Example.MultiSource` は優先度の高い HTTP ポリシーソースと、書き込み可能な明示設定、読み取り専用のローカル/共通 JSON ファイルを組み合わせます。HTTP ポリシーが無い・一時的に取れない場合はファイル層にフォールスルーし、恒久的な HTTP エラーはアプリに伝わります。

```sh
dotnet run --project example/Example.MultiSource
dotnet run --project example/Example.MultiSource -- --set-name Ada
```

`CONFIGLUE_POLICY_URL` に HTTP リソースルートを設定するとリモートポリシー層が有効になります。`example/Example.MultiSource/policy.json` はローカル配信向けの小さな fixture です。

## YAML コンソールアプリ

`Example.ConsoleApp.Yaml` は同じ形の生成モデルをキャメルケース名で YAML 保存します。

```sh
dotnet run --project example/Example.ConsoleApp.Yaml
dotnet run --project example/Example.ConsoleApp.Yaml -- --set-name Ada
```

## NativeAOT コンソールアプリ

`Example.ConsoleApp.NativeAot` はソース生成された `System.Text.Json` メタデータを使い、保存モデルを Configlue の疎フラグメントに投影します。

```sh
dotnet publish example/Example.ConsoleApp.NativeAot --configuration Release --runtime linux-x64 --self-contained true
```

各ガイドは実行可能な参照が役立つ箇所でここに戻ってきます。全サンプル共通の編集パターンは [読み書き・編集セッション・パッチ](../basic-usage/reading-and-writing.md) からどうぞ。
