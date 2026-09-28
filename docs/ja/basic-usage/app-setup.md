---
title: アプリケーション構成
description: 非 DI コンテキスト、DI 登録、所有権、名前付きインスタンス。
---

# アプリケーション構成

`conf.Add<TModel>(...)` は 1 つのモデルを定義します（ソース、書き込み経路、バリデーター、オプション名）。
同じ定義が下記の両構成で動作します。

## DI なし

`ConfiglueApp.CreateContext(...)` は、独立したライフサイクル管理付きコンテキストを作成します。
`ConfiglueApp.Initialize(...)` と `ConfiglueApp.GetOptions<T>()` は、プロセス全体の既定コンテキストを共有します（旧 `Configlue` 静的クラスと同じ既定動作）。
破棄は `await ConfiglueApp.ShutdownAsync()` を呼び出します。

```csharp
using Configlue.Sources;

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

`ConfiglueContext` は作成したオプションとウォッチャータスクを所有します。
アプリが渡したソース・リーダー・ライター・リソースのインスタンスは呼び出し側所有のまま維持されます。
ただし `FromJsonFile` などのプロバイダー登録ヘルパーが作成したリソースはコンテキスト所有となり、ウォッチャー停止後に破棄されます。
名前付きインスタンスを扱う場合は、モデルコールバックで `OptionsName` を設定します。

## DI あり

```csharp
using Configlue.Sources;

builder.Services.AddConfiglue(conf => conf.Add<UserSettings>(model =>
{
    model.Sources(sources => sources.Add(CreateUserSettingsSource()));
    model.WriteRoute = StateWriteRoute.To("user-settings");
}));
```

コンテキストはサービスプロバイダーが所有します。
コンポーネントには `IReadOnlyOptions<T>` または `IWritableOptions<T>` を注入してください。
クラスモデル向けの `IOptions<T>` / `IOptionsSnapshot<T>` / `IOptionsMonitor<T>` を使う場合は、`Configlue.Extensions.MSOptions` パッケージを追加し、モデル登録後に明示的に登録します。

```csharp
services.AddConfiglueMicrosoftOptions<UserSettings>();
```

この拡張メソッドは `Configlue.Extensions.MSOptions` 名前空間に配置されています。

保存先やプロバイダーを DI から取得する場合は、プロバイダー対応コールバックを使用できます。
モデル登録そのものは `IServiceCollection` が変更可能なうちに行い、コールバックはランタイムソースセットの作成時に実行されます。

```csharp
model.Sources((provider, sources) =>
{
    var paths = provider!.GetRequiredService<ISettingsPathProvider>();
    sources.FromJsonFile(new JsonFileSourceOptions { Path = paths.SettingsFile });
});
```

既に実体化された `IOptionsSnapshot<T>` は通常のスナップショット通り、そのスコープの値を保持します。

モデル自体を直接 DI で注入する場合は、既定モデルの登録で `RegisterAsSingleton = true` を設定します。
DI はモデルが初めて解決された時点の options 値からモデルシングルトンを構築します。
その後ソースが変わっても注入済みモデルはこのスナップショットを保持します。
最新値や変更通知が必要な利用側には options インターフェイスを使用してください。
この設定は `AddConfiglue` で有効になり、既定のオプション名が必要です。

```csharp
builder.Services.AddConfiglue(conf => conf.Add<UserSettings>(model =>
{
    model.RegisterAsSingleton = true;
    model.Sources(sources => sources.Add(CreateUserSettingsSource()));
}));
```

DI で独自ソースを使う場合は `AddConfiglueOptions<TModel, TFragment>` の `(provider, sources) => ...` オーバーロードでサービスを解決し、`sources.Add(id, reader, priority, fallbackCondition)` で追加します。リーダーが実装するライター/ウォッチャーインターフェイスは自動検出され、分離型には `WithWriter` / `WithWatcher` を使います。コールバックはオプションシングルトン生成時に実行され、完全独自ライフサイクルには `Sources(sources => sources.Add(existingSource))` も使えます。

```csharp
using Configlue.Sources;

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

DataAnnotations 検証は既定で有効です。無効にするには登録時に `validateDataAnnotations: false` を渡します。[変更と検証](./changes-and-validation.md) 参照。

## 次のステップ

* 定番の共通/ローカル/指定/env 構成は [共通レイヤーソース](./common-sources.md)。コマンドライン上書きは明示的に追加できます。
* プロバイダー登録は [ファイル・形式・セクション](../sources/files-and-sections.md)。
