---
title: 環境変数とコマンドライン
description: プロセス変数と System.CommandLine からの読み取り専用ソース。
---

どちらも読み取り専用で、通常はファイル層より高い優先度に置きます。

## 環境変数

`EnvironmentStateSource.FromEnvironment<TModel, TFragment>(id, prefix)` は、`APP__DATABASE__HOST` のようなプロセス環境変数から読み取り専用の疎ソースを作ります。
`__` で入れ子メンバーを区切り、メンバー名は大文字小文字を区別しません。

`[ConfiglueEnvironment("ENV_NAME")]` を付けたプロパティは、入れ子も含めてその変数名を（大文字小文字不問で）読み取ります。
一般的なスカラー値は不変カルチャでパースされ、アプリ固有型には独自パーサーを挟めます。
リーダーは読み取りごとに内容リビジョンを再計算します。
プロセス環境変数にウォッチャーはありません。

```csharp
model.Sources(sources =>
{
    sources.Add(EnvironmentStateSource.FromEnvironment<AppConfig, AppConfig.Fragment>(
        "environment", "APP"));
});
```

1 引数ファサードでは同じソースを簡潔に登録できます。

```csharp
sources.FromEnvironment(new() { Prefix = "APP" });
```

ソース ID は正規化済み prefix から内部生成されます。
高度なソース選択用に安定 ID が必要な場合だけ `Id` を指定します。
テストや独自ホスト向けの `EnvironmentVariables` と、スカラー変換用の `ValueParser` も指定できます。

## コマンドライン

`Configlue.Source.CommandLine` は、アプリ既存の `System.CommandLine` パース結果と、シンボルからパスへの明示マッピングを受けます。
明示指定されなかったシンボルにパーサー既定値があっても上書きにはなりません。
エラー付きパース結果ではソース読み取りが失敗します。
ルートと対象サブコマンドのシンボルは明示的にマップし、コマンドライン入力が変わったらソースやコンテキストを作り直します。

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

マッピングはパース済み値を JSON シリアライズなしに直接メンバー型へ変換するため、NativeAOT でも動作します。
独自形状には変換関数を付与し、1 つのシンボルを複数メンバーへ分配したり、複数シンボルを 1 つのメンバーへ向ける（存在する値が後勝ち）ことも可能です。

```csharp
mappings.Map(databaseOption, "Database.Host", static value => value?.Split(':')[0]);
mappings.Map(databaseOption, "Database.Port", static value => int.Parse(value?.Split(':')[1] ?? "0", CultureInfo.InvariantCulture));
mappings.Map(firstOption, "RetryCount");
mappings.Map(secondOption, "RetryCount"); // 両方ある場合はこちらが勝つ
```

パッケージは System.CommandLine 2.0.12 向けにビルドし、3.x 系列にもある API のみ使っています。
他の系列を確認するには `-p:ConfiglueSystemCommandLineVersion=<version>`（例 `3.0.0-rc.1.26425.128`）付きでビルドします。

## 次のステップ

* リモートとアーカイブは [HTTP と ZIP](./http-and-zip.md)。
* 合成プリセットは [共通レイヤーソース](../basic-usage/common-sources.md)。
