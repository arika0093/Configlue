---
title: "STEP 8: 版を上げて移行する"
description: 互換性のない変更は版を上げ、旧モデルからの移行を宣言する。
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 8: 版を上げて移行する

項目の改名や形の変更は、いつか必ず来ます。Configlue では互換性のない変更のときに版（Version）を上げ、古い形を残したまま移行方法を書きます。古いファイルは捨てずに読み替えられます。

## 版を上げる

新しい形を Version 2 にし、古い形は別名で残します。

```csharp
// Version 2（新しい形）
[ConfiglueModel("tutorial.settings", Version = 2)]
public partial class AppSettings
{
    public string DisplayName { get; set; } = "ExampleApp";
}

// Version 1（古い形）
[ConfiglueModel("tutorial.settings", Version = 1)]
public partial class AppSettingsV1
{
    public string Name { get; set; } = "ExampleApp";
}
```

## 移行方法を書く

```csharp
[ConfigluePreviousVersion(typeof(AppSettingsV1))]
public partial class AppSettings
{
    public AppSettings Migrate(AppSettingsV1 source) => new()
    {
        DisplayName = source.Name,
    };
}
```

同名・同型の項目は `Fragment.FromPrevious` で写るので、改名した項目だけ明示的に書けば済みます。ソース単位の移行連鎖には `IStateSchemaMigration<T>` の実装をサービス登録することもできます。

## 保存場所の引っ越しも同じ考え方で

形の移行とは別に、保存場所の引っ越し（ファイルの分割・形式の変更・古いソースの退役）もあります。こちらは検証付きのコピーで行います。

保存場所ガイドのように二つのソース ID を登録したうえで、どちらのアプリ構成からも同じ移行を呼べます。

<Tabs syncKey="di">
<TabItem label="DI なし">

```csharp
var options = context.GetOptions<AppSettings>();
var result = await options.MigrateSourceAsync("legacy-settings", "settings");
Console.WriteLine($"{result.SourceId} から {result.TargetId} へコピーしました。");
```

</TabItem>
<TabItem label="DI あり">

```csharp
public sealed class SettingsMigrator(IWritableOptions<AppSettings> options)
{
    public async Task MigrateAsync()
    {
        var result = await options.MigrateSourceAsync("legacy-settings", "settings");
        Console.WriteLine($"{result.SourceId} から {result.TargetId} へコピーしました。");
    }
}
```

</TabItem>
</Tabs>

対象の検証が通ってから退役させる流れは、[保存場所の移行](../migration/storage-migration.md)を見てください。複数宛先への一括移行や、移行後の旧ソース退役も同じ場所にまとまっています。

次: [STEP 9: NativeAOT に対応する](./09-native-aot.md)。トリミング安全な構成にします。
