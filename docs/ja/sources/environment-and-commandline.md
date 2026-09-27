---
title: 環境変数とコマンドライン
description: プロセス変数と System.CommandLine からの読み取り専用ソース。
---

# 環境変数とコマンドライン

どちらも読み取り専用で、通常はファイル層より高い優先度に置きます。

## 環境変数

`EnvironmentStateSource.FromEnvironment<TModel, TFragment>(id, prefix)` は `APP__DATABASE__HOST` のようなプロセス環境変数から読み取り専用の疎ソースを作ります。`__` で入れ子メンバーを区切り、メンバー名は大文字小文字を区別しません。

`[ConfiglueEnvironment("ENV_NAME")]` を付けたプロパティは、入れ子も含めてその変数名を (大文字小文字不問で) 読みます。一般的なスカラー値は不変カルチャでパースされ、アプリ固有型には独自パーサーを挟めます。リーダーは読み取りごとに内容リビジョンを再計算します。プロセス環境変数にウォッチャーはありません。

```csharp
model.Sources(sources =>
{
    sources.Add(EnvironmentStateSource.FromEnvironment<AppConfig, AppConfig.Fragment>(
        "environment", "APP"));
});
```

1引数ファサードでは同じソースを簡潔に登録できます:

```csharp
sources.FromEnvironment(new()
{
    Id = "environment",
    Prefix = "APP",
    Priority = 400,
});
```

ファサードのオプションはさらに `EnvironmentVariables` (テストや独自ホスト向け) と、アプリ固有スカラー変換用の `ValueParser` 上書きを受けます。

## コマンドライン

`Configlue.Source.CommandLine` はアプリ既存の `System.CommandLine` パース結果と、シンボル→パスの明示マッピングを受けます。明示指定されなかったシンボルにパーサー既定値があっても上書きにはなりません。エラーつきパース結果ではソース読み取りが失敗します。ルートと対象サブコマンドのシンボルは明示マップし、コマンドライン入力が変わったらソース/コンテキストを作り直します。

```csharp
using Configlue.Source.CommandLine;
using System.CommandLine;

model.Sources(sources => sources.FromCommandLine(new CommandLineSourceOptions
{
    Id = "command-line",
    ParseResult = parseResult,
    Priority = 500,
},
mappings =>
{
    mappings.Map<AppSettings, int>(portOption, settings => settings.Server!.Port);
    mappings.Map<AppSettings, bool>(verboseOption, settings => settings.Diagnostics!.Verbose);
}));
```

マッピングはパース済み値を JSON 序列化なしに直接メンバー型へ変換するため、NativeAOT でも動作します。独自形状には変換を付け、1シンボルを複数メンバーへ分配したり、複数シンボルを1メンバーに向ける (存在するものは後勝ち) こともできます:

```csharp
mappings.Map(databaseOption, "Database.Host", static value => value?.Split(':')[0]);
mappings.Map(databaseOption, "Database.Port", static value => int.Parse(value?.Split(':')[1] ?? "0", CultureInfo.InvariantCulture));
mappings.Map(firstOption, "RetryCount");
mappings.Map(secondOption, "RetryCount"); // 両方ある場合はこちらが勝つ
```

パッケージは System.CommandLine 2.0.12 向けにビルドし、3.x 系列にもある API のみ使っています。他の系列を確認するには `-p:ConfiglueSystemCommandLineVersion=<version>` (例 `3.0.0-rc.1.26425.128`) 付きでビルドします。

## 次のステップ

* リモートとアーカイブは [HTTP と ZIP](./http-and-zip.md)。
* 合成プリセットは [共通レイヤーソース](../basic-usage/common-sources.md)。
