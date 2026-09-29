---
title: アプリケーション構成
description: 非 DI コンテキスト、DI 登録、所有権、名前付きインスタンス。
---

`conf.Add<TModel>(...)` は 1 つのモデルを定義します（ソース、書き込み経路、バリデーター、オプション名）。
同じ定義が下記の両構成で動作します。

## DI なし

`ConfiglueApp.CreateContext(...)` は、独立したライフサイクル管理付きコンテキストを作成します。
`ConfiglueApp.Initialize(...)` と `ConfiglueApp.GetOptions<T>()` は、プロセス全体の既定コンテキストを共有します。
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
model.ConfigureSources(registration =>
{
    var paths = registration.Services!.GetRequiredService<ISettingsPathProvider>();
    registration.Sources.FromJsonFile(
        new JsonFileSourceOptions { Path = paths.SettingsFile });
});
```

既に実体化された `IOptionsSnapshot<T>` は通常のスナップショット通り、そのスコープの値を保持します。

ファサードは、モデル本体を同期スナップショットとして DI 登録しません。通常は `IReadOnlyOptions<T>` または `IWritableOptions<T>` を注入し、非同期で値を読みます。Microsoft の同期 Options API が必要なフレームワークでは、前述の MSOptions アダプターを明示的に追加します。

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

## Subject ごとのモデル

現在の値がリクエスト、ユーザー、テナントなどのアプリケーションコンテキストに依存するモデルでは、`model.PerSubject<TAccessor>()` を指定します。アクセサーはアプリ側で DI 登録します。scoped な `IReadOnlyOptions<T>` / `IWritableOptions<T>` は、読み取りと保存のたびにアクセサーから現在の subject を取得します。アクセサーはモデル登録ごとに選択でき、`PerSubject` を指定しないモデルは通常どおりシングルトンです。

```csharp
services.AddScoped<CurrentTenantAccessor>();
services.AddConfiglue(conf =>
{
    conf.Add<UserSettings>(model =>
    {
        model.PerSubject<CurrentTenantAccessor>();
        model.Sources(sources => sources.Add(CreateTenantSource()));
    });

    conf.Add<ServerSettings>(model =>
        model.Sources(sources => sources.Add(CreateServerSource())));
});
```

`CurrentTenantAccessor` は `IConfiglueSubjectAccessor<TenantSubject>` を実装し、`GetCurrentAsync` から `TenantSubject : IConfiglueSubject` を返します。アクセサーはアプリ側で管理し、非同期サービスも利用できます。`ISubjectOptions<T>` は明示的な subject view を得るシングルトン入口として残り、`.For(subject)` で使います。検査、診断、ソース管理、編集セッションは共有ランタイムに対するサービスとして動作します。

アクセサーに `IConfiglueSubjectChangeSource` も実装すると、通知を受けた `OnChange` 購読が subject を再解決し、その subject の watcher に接続し直します。認証状態などが同じスコープ内で変わる場合に使えます。この任意インターフェイスを実装しない場合、watcher は購読開始時に解決した subject を監視し続けます。

任意の `Configlue.Resource.Http.AspNetCore` パッケージには、HTTP リクエストおよび Blazor 認証状態から subject を解決するアクセサーがあります。フレームワーク固有のコンテキストや claims への依存は Core パッケージに入りません。

```csharp
services.AddHttpContextConfiglueSubjectAccessor<TenantSubject>(
    context => new TenantSubject(context.User.FindFirst("tenant")!.Value));

services.AddBlazorAuthenticationConfiglueSubjectAccessor<TenantSubject>(
    (principal, _) => ValueTask.FromResult(
        new TenantSubject(principal.FindFirst("tenant")!.Value)));
```

モデルには `PerSubject<HttpContextConfiglueSubjectAccessor<TenantSubject>>()` または `PerSubject<BlazorAuthenticationConfiglueSubjectAccessor<TenantSubject>>()` を指定します。Blazor アクセサーは認証状態の変更を通知するため、既存の options watcher も新しい subject に追従します。

## 独自バリデーター

DataAnnotations 検証は既定で有効です。無効にするには登録時に `validateDataAnnotations: false` を渡します。
コードによる検証を追加するには、Microsoft の `IValidateOptions<T>` を `AddConfiglueValidator` で適合させるか、`IConfiglueValidator<T>` を実装して DI のシングルトンとして登録します。

```csharp
services.AddConfiglueValidator<UserSetting>(new UserSettingValidator());
services.AddSingleton<IConfiglueValidator<UserSetting>, UserSettingValidator2>();
```

[変更と検証](./changes-and-validation.md) 参照。

## 次のステップ

* 定番の共通/ローカル/指定/env 構成は [共通レイヤーソース](./common-sources.md)。コマンドライン上書きは明示的に追加できます。
* プロバイダー登録は [ファイル・形式・セクション](../sources/files-and-sections.md)。
