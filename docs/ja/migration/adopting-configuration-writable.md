---
title: Configuration.Writable の取り込み
description: 旧来のインライン版つきファイルを読んで移行入力にする。
---

# Configuration.Writable の取り込み

[Configuration.Writable](https://github.com/arika0093/Configuration.Writable) が書いたファイルを残したまま Configlue を導入できます。旧来ファイルには触れず、Configlue は opt-in デコーダーで読んで、その寄与を通常の書き込み先にコピーします。

## 旧来デコーダー

Configuration.Writable の JSON/YAML ファイルを取り込むには、opt-in の旧来デコーダー `ConfigurationWritableJsonStateCodec<TFragment>` / `ConfigurationWritableYamlStateCodec<TFragment>` を使います。まず `JsonSectionResource`・`YamlSectionResource` で入れ子セクションを選び、`SerializedStateReader<TFragment>` とライターなし `StateSource<TFragment>` で包みます。

デコーダーはインライン `$version` (と `Version` フォールバック) を認識し、印のないオブジェクト/マッピングを版 1 とみなし、履歴配送向けに現行 Configlue モデル ID へ版を対応づけられます。`$schema` はメタデータとして剥がします。空・空白のみの YAML は空の疎フラグメントとして読みます。BOM つき YAML にはソース符号化をコーデックに渡し、入れ子セクション読みでは `YamlSectionResource` の `textEncoding` にも渡します。既定は厳密 UTF-8 のままです。

```csharp
var oldFile = new FileResource("./old-settings.json");
var oldSection = new JsonSectionResource(
    oldFile,
    writer: null,
    sectionPath: "ApplicationSettings:Database",
    watcher: null);
var oldReader = new SerializedStateReader<AppSettings.Fragment>(
    oldSection,
    new ConfigurationWritableJsonStateCodec<AppSettings.Fragment>(
        modelId: AppSettings.ConfiglueSchema.ModelId));
var oldSource = new StateSource<AppSettings.Fragment>("legacy", oldReader);
var currentSource = CreateCurrentSettingsSource(); // 通常 Configlue コーデックの書き込み可能ソース

await using var options = new ConfiglueOptions<AppSettings, AppSettings.Fragment>(
    new StateSourceSet<AppSettings.Fragment>([oldSource, currentSource]),
    StateWriteRoute.To("current"));

// 旧寄与だけコピーし、oldFile は再試行/復旧向けに残す。
await options.MigrateSourceAsync("legacy", "current");
```

上記のファイル・セクションリソースはアプリ所有のままです。履歴的な項目形状を持つファイルには `SerializedStateReader<TFragment>` にスキーマ配送子を渡し、履歴フラグメントごとに対応する旧来コーデックを登録します。

## 安全な取り込みの約束

* 旧来ソースは読み取り専用の移行入力として追加し、そのソース ID だけを `MigrateSourceAsync`・`MigrateSourcesToTargetsAsync` で書き込み先にコピーします。
* 宛先検証が成功するまで元ファイルは残します。失敗したら同じソース・宛先定義で再試行します。
* 旧ファイルの削除は明示的なアプリ判断でのみ行います。

## 次のステップ

* 取り込み後の版連鎖は [スキーマ移行](./schema-migration.md)。
* 複数宛先の引っ越しは [保存場所移行](./storage-migration.md)。
