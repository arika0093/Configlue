---
title: 共通レイヤーソース
description: 共通・ローカル・指定・環境変数・コマンドラインプリセット。
---

# 共通レイヤーソース

任意パッケージ `Configlue.Source.Common` は定番のアプリ構成 (共通ファイル・ローカルファイル・明示指定ファイル・環境変数・コマンドライン) を組み立てます。`CreateContext` と `services.AddConfiglue` のどちらでも使えます:

```csharp
using Configlue.Source.Common;

config.Add<AppSettings>(model => model.UseCommonSources(new CommonSourceOptions
{
    ApplicationId = "ExampleApp",
    GlobalFileName = "settings.json",
    SpecificFilePath = selectedPath, // 選択ファイルパス。項目上書きとは別
    EnvironmentPrefix = "EXAMPLE",
    CommandLineParseResult = parseResult,
    ConfigureCommandLineMappings = mappings => mappings.Map(portOption, "Server.Port"),
    WriteLayer = CommonSourceWriteLayer.Global,
}));
```

`UseCommonSources` は次の安定した論理ソースに展開されます:

| ソース ID | 優先度 | 含まれる条件 | 場所/入力 | 書き込み可 |
| --- | ---: | --- | --- | --- |
| `common.global` | 100 | `EnableGlobalFile` | `Path.Combine(GetStandardSaveDirectory(ApplicationId), GlobalFileName)` | `WriteLayer` 選択時のみ |
| `common.local` | 200 | `EnableLocalFile` | `Path.GetFullPath(LocalFilePath ?? Path.Combine(Environment.CurrentDirectory, GlobalFileName))` | `WriteLayer` 選択時のみ |
| `common.specific` | 300 | `EnableSpecificFile` かつ `SpecificFilePath` 設定済み | `Path.GetFullPath(SpecificFilePath)` | `WriteLayer` 選択時のみ |
| `common.environment` | 400 | `EnableEnvironment` かつ `EnvironmentPrefix` 設定済み | 環境変数 | 不可 |
| `common.commandLine` | 500 | `EnableCommandLine` かつ `CommandLineParseResult` 設定済み | 既存パース結果からのマッピング | 不可 |

複数ソースに存在する項目は優先度が高い方が勝ちます。ファイルがない場合ファイルソースはフォールスルーし、それ以外の読み取り失敗は伝播します。`WriteLayer` で選んだファイルちょうど1つだけが書き込み可能で、無効・利用不可なファイル層を選ぶと登録時に例外になります。`SpecificFilePath` のコマンドライン選択は項目レベルのマッピングとは別物で、パース結果には `ConfigureCommandLineMappings` が必須です。層を省くには `Enable*` スイッチを、場所と保存先の変更には `LocalFilePath`・`SpecificFilePath`・`WriteLayer` を設定します。

`ConfiglueStandardPaths.GetStandardSaveDirectory(applicationId)` はプラットフォーム標準のユーザー別構成ディレクトリにアプリ識別子を足したパスを返します。ファイル名はアプリが決めます。

## 次のステップ

* 基礎のソースは [環境変数とコマンドライン](../sources/environment-and-commandline.md)。
* パス単位の書き込み所有は [書き込み経路指定](../layering/write-routing.md)。
