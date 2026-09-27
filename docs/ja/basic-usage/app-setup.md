---
title: アプリケーション構成
description: 非 DI コンテキスト、DI 登録、所有権、名前付きインスタンス。
---

# アプリケーション構成

`conf.Add<TModel>(...)` は1つのモデルを定義します: ソース、書き込み経路、バリデーター、オプション名。同じ定義が下記の両構成で動きます。

## DI なし

`ConfiglueApp.CreateContext(...)` は独立した寿命管理つきコンテキストを作ります。`ConfiglueApp.Initialize(...)` + `ConfiglueApp.GetOptions<T>()` はプロセス全体の既定コンテキストを共有します (旧 `Configlue` 静的クラスと同じ既定)。破棄は `await ConfiglueApp.ShutdownAsync()` です。

```csharp
await using var context = ConfiglueApp.CreateContext(conf =>
{
    conf.Add<UserSettings>(model =>
    {
        model.Sources(sources => sources.Add(CreateUserSettingsSource()));
        model.WriteRoute = StateWriteRoute.To("user-settings");
    });
});

var options = context.GetOptions<UserSettings>();
```

`ConfiglueContext` は作ったオプションとウォッチャータスクを所有します。アプリが渡したソース・リーダー・ライター・リソースのインスタンスは呼び出し側所有のままです — ただし `FromJsonFile` などのプロバイダー登録ヘルパーが作ったリソースはコンテキスト所有で、ウォッチャー停止後に破棄されます。名前付きインスタンスにはモデルコールバックで `OptionsName` を設定します。

## DI あり

```csharp
builder.Services.AddConfiglue(conf => conf.Add<UserSettings>(model =>
{
    model.Sources(sources => sources.Add(CreateUserSettingsSource()));
    model.WriteRoute = StateWriteRoute.To("user-settings");
}));
```

コンテキストはサービスプロバイダーが所有します。`IReadOnlyOptions<T>` / `IWritableOptions<T>` を注入するか、クラスモデル向けの `IOptions<T>` / `IOptionsSnapshot<T>` / `IOptionsMonitor<T>` アダプターを使います。既に実体化された `IOptionsSnapshot<T>` は通常のスナップショット通り、そのスコープの値を保ちます。

DI で独自ソースを使う場合は `AddConfiglueOptions<TModel, TFragment>` の `(provider, sources) => ...` オーバーロードでサービスを解決し、`sources.Add(id, reader, priority, fallbackCondition)` で追加します。リーダーが実装するライター/ウォッチャーインターフェイスは自動検出され、分離型には `WithWriter` / `WithWatcher` を使います。コールバックはオプションシングルトン生成時に実行され、完全独自ライフサイクルには `Sources(sources => sources.Add(existingSource))` も使えます。

```csharp
services.AddSingleton<UserSettingsSource>();
services.AddConfiglueOptions<AppConfig, AppConfig.Fragment>(
    (provider, sources) =>
    {
        sources.Add(
            "user-settings",
            provider.GetRequiredService<UserSettingsSource>(),
            priority: 100,
            fallbackCondition: StateFallbackCondition.NotFound,
            physicalOrigin: "user-settings.json");
    },
    StateWriteRoute.To("user-settings"));
```

## 独自バリデーター

```csharp
services.AddConfiglueValidator<UserSetting>(new UserSettingValidator());
```

または登録時に `validateDataAnnotations: true` を渡します。[変更と検証](./changes-and-validation.md) 参照。

## 次のステップ

* 定番の共通/ローカル/指定/env/cli 構成は [共通レイヤーソース](./common-sources.md)。
* プロバイダー登録は [ファイルとセクション](../sources/files-and-sections.md)。
