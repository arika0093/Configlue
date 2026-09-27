---
title: 共通レイヤーソース
description: global、local、指定ファイル、環境変数をまとめる source プリセット。
---

# 共通レイヤーソース

`Configlue` に含まれる `Configlue.Source.Common` パッケージは、標準構成 (global file、local file、任意の指定ファイル、任意の環境変数) を `CreateContext` と `services.AddConfiglue` のどちらでも組み立てます。コマンドライン上書きは `Configlue.Source.CommandLine` から明示的に追加します。

```csharp
using Configlue.Source.Common;

config.Add<AppSettings>(model =>
    model.UseCommonSources(
        "ExampleApp",
        specificFilePath: selectedPath,
        environmentPrefix: "EXAMPLE"));
```

標準の per-user directory と current directory に `settings.json` を使います。`specificFilePath` を渡すと指定ファイル層を追加し、`environmentPrefix` を渡すと環境変数層を追加します。ファイル名や serializer 設定、file resource を変更する場合は `CommonSourceOptions` を使います。

`UseCommonSources` は次の順にレイヤーを登録します:

| 層 | 登録条件 | 場所・入力 | 書き込み先 |
| --- | --- | --- | --- |
| Global | 常に登録 | 標準 per-user directory と `GlobalFileName` | 明示選択可能 |
| Local | 常に登録 | `LocalFilePath`、または current directory と `GlobalFileName` | 指定ファイルがなければ既定 |
| Specific | `SpecificFilePath` が指定された場合 | 指定パス | 指定時の既定 |
| Environment | `EnvironmentPrefix` が指定された場合 | 環境変数 | 読み取り専用 |

表の順が優先順位です。コマンドライン上書きを追加するには `Configlue.Source.CommandLine` をインストールし、既存のパース結果と明示的なマッピングを渡す `UseCommonSources` overload を使います。

source-local write には文字列キーを作らず、semantic selector を使えます:

```csharp
await options.Source(CommonSource.Local).SaveAsync(
    new AppSettings.Patch { Name = FragmentOperation<string>.Set("local-name") }
);
```

登録されたすべてのファイル層は source handle から明示的に書き込めます。通常の `SaveAsync` は指定ファイルがあればそこへ、なければ local file に書き込みます。通常の宛先を変更する場合だけ `WriteLayer` を設定します。層の有無は指定パスと環境変数 prefix で判断し、登録時に file system の権限確認は行いません。

`ConfiglueStandardPaths.GetStandardSaveDirectory(applicationId)` は application ID に対応した OS 標準の per-user 設定 directory を返します。ファイル名はアプリケーション側で決めます。

## 次の手順

* 基盤となる source は [環境変数とコマンドライン](../sources/environment-and-commandline.md) を参照してください。
* property ごとの書き込み ownership は [書き込みルーティング](../layering/write-routing.md) を参照してください。
