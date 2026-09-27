---
title: プロファイル
description: 有効プロファイルカタログつきの永続化される名前付きプロファイル。
---

# プロファイル

永続化される名前付きプロファイルはカタログ用に独立した書き込み可能ソースを使います。プロファイルソースファクトリーは各プロファイル名を受け取るため、プロファイル値をファイル・セクション・他リソースに分けて保存できます。

```csharp
model.EnableProfiles(profileCatalogSource, defaultProfileName: "default");
model.SourcesForOptions((profileName, sources) =>
    sources.FromJsonFile(new()
    {
        Id = "profile-state",
        Path = Path.Combine(profileDirectory, profileName + ".json"),
    }));

var profiles = context.GetProfiledOptions<AppSettings>();
await profiles.CreateProfileAsync("work", copyFrom: "default");
await profiles.SetActiveProfileAsync("work");
var active = await profiles.GetActiveValueAsync();
await profiles.RemoveProfileAsync("work");
```

プロファイルカタログソースは書き込み可能である必要があり、呼び出し側所有のままです。カタログは通常の書き込み可能 `StateSource<ConfiglueProfileCatalog>` で保存されるため、プロバイダーは独立に選べます。`IConfiglueProfiledOptions<TModel>` は最初の非同期操作で既定プロファイルを遅延復元/作成し、`CreateProfileAsync` で複写でき、有効プロファイル変更を永続化します。プロファイル削除はカタログとランタイムから除去しますが、裏の状態は残ります。

DI では同じ `EnableProfiles`・`SourcesForOptions` 呼び出しを `services.AddConfiglue(...)` の中で行い、プロバイダーから `IConfiglueProfiledOptions<AppSettings>` を解決します。プロバイダー構築後に追加されたプロファイル名はキー付きサービスではなく `IOptionsMonitor` と `IConfiglueOptionsRegistry` で解決されます。非 DI の1引数入口は上記の `context.GetProfiledOptions<AppSettings>()` です。

```csharp
services.AddSingleton<ProfileCatalogStore>();
services.AddConfiglueProfiledOptions<AppConfig, AppConfig.Fragment>(
    (provider, profileName) => CreateProfileSources(provider, profileName),
    provider =>
    {
        var catalogStore = provider.GetRequiredService<ProfileCatalogStore>();
        return new StateSource<ConfiglueProfileCatalog>("profile-catalog", catalogStore, writer: catalogStore);
    });
```

プロファイル名はオプション登録簿の名前でもあるため、固定 `OptionsName` 登録と衝突できません。`SourcesForOptions` は固定登録自体を含む各名前つきランタイム構築で実行され、そのランタイムの正確な `OptionsName` を受け取ります — プロファイル固有ソースの構築に使います。名前付きプロファイルにはキー付き DI 登録も使えます (例: `AddConfiglueOptions<TModel, TModel.Fragment>("profile", sourceSet)` と `GetRequiredKeyedService<IReadOnlyOptions<TModel>>("profile")`)。

## 次のステップ

* 永続化なしの実行時限定の名前つき実体は [動的オプション](./dynamic-options.md)。
* [保存場所移行](../migration/storage-migration.md)。
