---
title: ライフタイムとソースの優先順位
description: プロセス全体の context と明示的な context を使い分け、ソース解決の順序を理解する。
---

Configlue には2つのライフタイムがあります。どちらも同じ State API とソース解決規則を使い、context を誰が所有するかが異なります。

## プロセス全体で1つの context を使う

`ConfiglueApp.Initialize(...)` は、プロセス全体で共有する既定の context を1つ作ります。CLI や小規模なアプリケーションで、context を引数として受け渡す必要がない場合に使えます。

```csharp
ConfiglueApp.Initialize(config =>
{
    config.Add<AppSettings>(model =>
        model.UseJsonFile("settings.json"));
});

var state = ConfiglueApp.GetState<AppSettings>();
var value = await state.GetValueAsync();

await ConfiglueApp.ShutdownAsync();
```

初期化前に `GetState<T>()` を呼ぶと `InvalidOperationException` が発生します。既定の context が存在する状態で再び初期化した場合も同じです。`ShutdownAsync` は複数回呼べ、既定の context を破棄して参照を消します。その後は新しい context を初期化できます。

`Initialize` が行うのは登録とランタイムの構築です。ソースの読み書きは引き続き非同期で実行されます。

## 明示的な context を使う

`ConfiglueApp.CreateContext(...)` は独立した `ConfiglueContext` を返します。テスト、ライブラリ、DI、複数環境の同時利用など、所有期間をコード上で明示したい場合はこちらを使います。

```csharp
await using var context = ConfiglueApp.CreateContext(config =>
{
    config.Add<AppSettings>(model =>
        model.UseJsonFile("settings.json"));
});

var state = context.GetState<AppSettings>();
```

context は state ランタイム、watcher、`FromJsonFile` などの登録ヘルパーが作成したリソースを所有します。アプリケーション側で生成して渡した source、reader、writer、resource は呼び出し側が所有します。

## ソースの優先順位

読み取り時は、数値の `Priority` が高いソースから順に解決します。同じ優先順位のソースが複数ある場合は、先に登録したソースが先に評価されます。

解決単位はソース全体ではなく各メンバーです。上位ソースがある項目を持たなければ、そのソースに設定された fallback 規則に従って下位ソースを調べます。

`Unset` は、指定したソースにある寄与を削除する操作です。モデルの初期値を書き込む操作ではありません。寄与を削除すると、次の下位ソース、またはモデルの初期値が有効値になります。

読み取りの優先順位と書き込み先は別に決まります。下位の書き込み可能ソースへ保存しても、上位の読み取り専用ソースが同じ項目を持つ間は有効値が変わらない場合があります。設定 UI で編集可能と表示する前に、`GetDetailsAsync()` で有効なソースと editability を確認できます。

merge の規則と provenance は [解決とマージ](../layering/resolution-and-merge.md)、保存先の決定は [書き込み経路指定](../layering/write-routing.md) を参照してください。
