---
title: Configuration.Writable の取り込み
description: 旧来のインライン版つきファイルを読んで移行入力にする。
---

[Configuration.Writable](https://github.com/arika0093/Configuration.Writable) が書いたファイルを残したまま Configlue を導入できます。旧来ファイルには触れず、Configlue は opt-in デコーダーで読んで、その寄与を通常の書き込み先にコピーします。

## 旧来ドキュメント

Configuration.Writable の JSON/YAML ファイルを取り込むには、シンプルなドキュメントレイアウト (`DocumentLayout.Simple`、既定) で読みます。コーデックはインライン `$version` (と `Version` フォールバック) を認識し、版のないスキーマ注釈つきオブジェクト/マッピングを版 1 とみなし、履歴配送向けに現行 Configlue モデル ID へ版を対応づけられます。`StateSchemaDispatcher<T>` はモデル ID のないインライン版を dispatcher の対象モデルに対応づけます。`$schema` はメタデータとして剥がします。空・空白のみの YAML は空の疎フラグメントとして読みます。BOM つき YAML の符号化は自動判定します。明示的な非 UTF-8 符号化で入れ子セクションを読む場合は、`YamlSectionResource` とコーデックの両方に `textEncoding` を渡します。まず `JsonSectionResource`・`YamlSectionResource` で入れ子セクションを選び、`SerializedSource<TFragment>` とライターなし `StateSource<TFragment>` で構成します。

```csharp
using Configlue.Codecs;
using Configlue.Migrations;
using Configlue.Resources;
using Configlue.Sources;
using Configlue.State;
using Configlue.Provider.Json;

var oldFile = new FileResource("./old-settings.json");
var oldSection = new JsonSectionResource(
    oldFile,
    writer: null,
    sectionPath: "ApplicationSettings:Database",
    watcher: null);
var oldReader = new SerializedSource<AppSettings.Fragment>(
    oldSection,
    new JsonStateCodec<AppSettings.Fragment>(
        documentLayout: new DocumentLayoutOptions
        {
            ModelId = AppSettings.ConfiglueSchema.ModelId,
        }));
var oldSource = new StateSource<AppSettings.Fragment>("legacy", oldReader);
var currentSource = CreateCurrentSettingsSource(); // 通常 Configlue コーデックの書き込み可能ソース

await using var context = ConfiglueApp.CreateContext(app =>
    app.Add<AppSettings>(model =>
    {
        model.Sources(sources =>
        {
            sources.Add(oldSource);
            sources.Add(currentSource);
        });
        model.WriteRoute = StateWriteRoute.To("current");
    }));

// 旧寄与だけコピーし、oldFile は再試行/復旧向けに残す。
await context.GetSources<AppSettings>().MigrateSourceAsync("legacy", "current");
```

上記のファイル・セクションリソースはアプリ所有のままです。履歴的な項目形状を持つファイルには `SerializedSource<TFragment>` にスキーマ配送子を渡し、履歴フラグメントごとに対応する旧来コーデックを登録します。

## 安全な取り込みの約束

* 旧来ソースは読み取り専用の移行入力として追加し、そのソース ID だけを `MigrateSourceAsync`・`MigrateSourcesToTargetsAsync` で書き込み先にコピーします。
* 宛先検証が成功するまで元ファイルは残します。失敗したら同じソース・宛先定義で再試行します。
* 旧ファイルの削除は明示的なアプリ判断でのみ行います。

## アプリ API の移行

ファイルを取り込んだ後、アプリの登録と読み書き箇所を次のように置き換えます。ソースと保存先は `conf.Add<TModel>(...)` で明示し、既存ファイルの形式に合う provider を登録してください。

| Configuration.Writable | Configlue |
| --- | --- |
| `[OptionsModel]` | `[ConfiglueModel]`。クラスは `partial` にし、生成される `Patch` を疎な保存に使います。 |
| `WritableOptions.Initialize(...)` | `ConfiglueApp.CreateContext(...)`。既定の共有 context が必要なら `ConfiglueApp.Initialize(...)` と `GetState<T>()`。 |
| `WritableOptions.GetState<T>()` | `context.GetState<T>()` または `ConfiglueApp.GetState<T>()`。 |
| `CurrentValue` | `await state.GetValueAsync()`。Configlue の基本 API は非同期です。同期 `IOptions<T>` adapter が必要な DI アプリは `Configlue.Extensions.MSOptions` を明示的に登録します。 |
| `SaveAsync(value => ...)` | `await state.SaveAsync(patch => ...)`。指定した項目だけを保存します。解決済みモデル全体を編集する場合は `context.GetEditSessions<T>().OpenEditSessionAsync()` を使います。 |
| `OnChange(...)` / `OnReloadFailed(...)` | `state.OnChange(...)` と `context.GetDiagnostics<T>().OnReloadFailed(...)`。返された subscription は不要になった時点で破棄します。 |
| `InstanceName` / named options | 固定名は登録時の `StateName`、実行時に追加・削除する名前は `EnableDynamicStates` と `GetStateRegistry<T>()`。永続化された profile catalog が必要なら `EnableProfiles(...)` を使います。 |
| `ConfigurationInfo` | topology と write route は `context.GetDiagnostics<T>().GetDiagnostics()`、値や各項目の出所は `await state.GetDetailsAsync()`。 |
| `AddWritableOptions(...)` | `services.AddConfiglue(...)`。`IOptions<T>` なども必要な場合は `AddConfiglueMicrosoftOptions<T>()` を追加します。 |

独立した非 DI context の例です:

```csharp
await using var context = ConfiglueApp.CreateContext(config =>
{
    config.Add<UserSettings>(model =>
    {
        model.UseDefaultJsonFile();
    });
});

var state = context.GetState<UserSettings>();
var value = await state.GetValueAsync();
using var subscription = state.OnChange(updated => Console.WriteLine(updated.Name));
await state.SaveAsync(patch => patch.Name = "new name");
```

`CreateContext` は context とそこで開始する watcher の寿命を管理します。アプリが渡した source や resource は引き続きアプリ所有です。固定登録の named state や DI 登録の詳細は[アプリケーション構成](../basic-usage/app-setup.md)、実行時追加・削除と永続 profile の違いは[動的 state](../profiles/dynamic-states.md)と[プロファイル](../profiles/profiles.md)を参照してください。

## 次のステップ

* 取り込み後の版連鎖は [スキーマ移行](./schema-migration.md)。
* 複数宛先の引っ越しは [保存場所移行](./storage-migration.md)。
