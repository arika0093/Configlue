---
title: 名前付きインスタンスと動的オプション
description: OptionsName による名前付きインスタンスと、実行時の追加・削除。
---

# 名前付きインスタンスと動的オプション

モデルは `model.EnableDynamicOptions = true` で動的な名前付きオプションに参加できます。コンテキストは `GetOptionsRegistry<TModel>()` を公開し、`TryAdd(name)` が同じソース/モデル構成をその `OptionsName` で作り、`TryRemoveAsync(name)` がウォッチャーを止めてヘルパー生成リソースを破棄してから戻ります。

```csharp
config.Add<AppSettings>(model =>
{
    model.EnableDynamicOptions = true;
    model.Sources(sources => sources.FromJsonFile(new()
    {
        Id = "tenant-settings",
        Path = "settings.json",
    }));
});

var registry = context.GetOptionsRegistry<AppSettings>();
registry.TryAdd("tenant-a");
var tenantOptions = context.GetOptions<AppSettings>("tenant-a");
await registry.TryRemoveAsync("tenant-a");
```

DI では `IOptionsMonitor<AppSettings>.Get("tenant-a")` が登録簿経由で追加・削除を追従します。削除後の `Get` は例外になります。動的な書き込み可能オプションは `IConfiglueInspectionRegistry<AppSettings>.Get(name)` で解決します。キー付きサービスはプロバイダー構築時に固定され、後からの名前には作られません。実体化済みの `IOptionsSnapshot<T>` は通常のスナップショット通りそのスコープの値を保ちます。

動的な名前付きオプションは実行時限定です。永続化されるプロファイルカタログは別途 `EnableProfiles` で利用できます。実行中に作る実行時プロファイルには `AddConfiglueOptionsRegistry<TModel, TModel.Fragment>(...)` を登録し、`IConfiglueInspectionRegistry<TModel>.TryAdd`・`Get`・`TryRemove` を使います。

固定登録の名前は予約済みです。削除はそのランタイムでの新規操作開始を止め、進行中操作とウォッチャー停止を待ってからヘルパー生成リソースを破棄します。削除後の新規コンテキスト検索は失敗します。既返却のハンドルは破棄済みになり、削除済みオプションの構成セッション保存は `ObjectDisposedException` で失敗します。

各オプションランタイムのソース集合は固定です。動的な名前付きオプション追加は登録定義から独立ランタイムを作り、削除はそのランタイム全体を退役させます。どちらの操作も他ランタイムのソース構成は変えません。

## 次のステップ

* 永続カタログは [プロファイル](./profiles.md)。
* 独自ソースの寿命は [アプリケーション構成](../basic-usage/app-setup.md)。
