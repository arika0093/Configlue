---
title: プロファイル
description: 有効プロファイルカタログつきの永続化される名前付きプロファイル。
---

永続化される名前付きプロファイルはカタログ用に独立した書き込み可能ソースを使います。プロファイルソースファクトリーは各プロファイル名を受け取るため、プロファイル値をファイル・セクション・他リソースに分けて保存できます。

```csharp
model.EnableProfiles(profileCatalogSource, defaultProfileName: "default");
model.ConfigureSources(registration =>
    registration.Sources.FromJsonFile(new()
    {
        Id = "profile-state",
        Path = Path.Combine(profileDirectory, registration.StateName + ".json"),
    }));

var profiles = context.GetProfiledState<AppSettings>();
await profiles.CreateProfileAsync("work", copyFrom: "default");
await profiles.SetActiveProfileAsync("work");
var activeOptions = await profiles.GetActiveProfileAsync();
await activeOptions.SaveAsync(patch => patch.RetryCount = 3);
var current = await profiles.GetActiveValueAsync();
await profiles.RemoveProfileAsync("work");
```

プロファイル facade はプロファイルの選択とライフタイムを管理します。書き込みは `GetActiveProfileAsync` または `GetProfileAsync` が返す `IWritableState<TModel>` を通して行います。これにより明示的な `Unset` を含む patch-first の書き込みになります。プロファイルカタログソースは書き込み可能である必要があり、呼び出し側所有のままです。カタログは通常の書き込み可能 `StateSource<ConfiglueProfileCatalog>` で保存されるため、プロバイダーは独立に選べます。`IConfiglueProfiledState<TModel>` は最初の非同期操作で既定プロファイルを遅延復元/作成し、`CreateProfileAsync` で複写でき、有効プロファイル変更を永続化します。プロファイル削除はカタログとランタイムから除去しますが、裏の状態は残ります。

`OnChange` は active profile を追跡し、値の変更とプロファイル切替後の新しい値を通知します。カタログ source が watcher を提供する場合は、外部からのカタログ変更も監視します。subscription を破棄するとその callback が止まります。manager の catalog watcher は所有 context の破棄時に停止します。直接生成した profile manager は呼び出し側で破棄してください。

DI では同じ `EnableProfiles`・`ConfigureSources` 呼び出しを `services.AddConfiglue(...)` の中で行い、プロバイダーから `IConfiglueProfiledState<AppSettings>` を解決します。プロバイダー構築後に追加されたプロファイル名はキー付きサービスではなく `IOptionsMonitor` と `IConfiglueStateRegistry` で解決されます。非 DI の1引数入口は上記の `context.GetProfiledState<AppSettings>()` です。

```csharp
using Configlue.Sources;

services.AddSingleton<ProfileCatalogStore>();
services.AddConfiglueProfiledState<AppConfig, AppConfig.Fragment>(
    (provider, profileName) => CreateProfileSources(provider, profileName),
    provider =>
    {
        var catalogStore = provider.GetRequiredService<ProfileCatalogStore>();
        return new StateSource<ConfiglueProfileCatalog>("profile-catalog", catalogStore, writer: catalogStore);
    });
```

プロファイル名は state 登録簿の名前でもあるため、固定 `StateName` 登録と衝突できません。`ConfigureSources` は固定登録自体を含む各名前つきランタイム構築で実行され、そのランタイムの正確な `StateName` を受け取ります — プロファイル固有ソースの構築に使います。名前付きプロファイルにはキー付き DI 登録も使えます (例: `AddConfiglueState<TModel, TModel.Fragment>("profile", sourceSet)` と `GetRequiredKeyedService<IReadOnlyState<TModel>>("profile")`)。

## 次のステップ

* 永続化なしの実行時限定の名前つき実体は [名前付きインスタンスと動的 state](./dynamic-states.md)。
* [保存場所移行](../migration/storage-migration.md)。
