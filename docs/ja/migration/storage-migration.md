---
title: 保存場所移行
description: 検証と退役つきでソース間に寄与をコピーする。
---

# 保存場所移行

スキーマ移行が形状を進化させるのに対し、保存場所移行は寄与をソース間で引っ越します — 新ファイル場所、形式変更、層の統合などです。

## 単一ソースのコピー

`IWritableOptions<T>.MigrateSourceAsync(sourceId, targetId)` は1つのソース寄与をコピーし、スキーマ移行連鎖を適用して選択先に書き込みます。

## 退役つき複数宛先移行

`MigrateSourcesToTargetsAsync(sourceIds, targetProjections)` は選択寄与だけをマージし、宛先ごとにフラグメント投影を適用して、各宛先をリビジョンチェック・検証します。完了済み宛先は再試行でスキップされ、後段の宛先で失敗したら移行を再実行して再開します。複数宛先の書き込みはアトミックではありません。

複数宛先をプロセス再起動後も再開する場合は、`StateStorageMigrationDefinition<TFragment>` と `IStateStorageMigrationJournal` を使います。journal は `StateStorageMigrationProgress` を migration ID ごとに永続化し、各宛先の検証後に更新します。アプリ側で journal の保存先を選び、書き込みを原子的に実装してください。同じ migration ID を複数プロセスから同時実行しないでください。

JSON ファイルから YAML ファイルへ形式を移す例です。両ソースの codec は同じ生成 Fragment に変換されるため、形式変換はターゲット source の writer が行います。

```csharp
var migration = new StateStorageMigrationDefinition<AppSettings.Fragment>(
    "settings-json-to-yaml-v1",
    ["legacy-json"],
    [new StateStorageMigrationTarget<AppSettings.Fragment>("settings-yaml", fragment => fragment)],
    retireSources: true);

// journal は StateStorageMigrationProgress を永続ストレージに保存するアプリ実装。
var progress = await options.MigrateAsync(migration, journal);
```

再起動時は options を組み立てる前に `journal.ReadAsync(migration.Id)` を呼び、`SourcesRetired` が true なら旧 JSON source を登録から省きます。定義と同じ ID で `MigrateAsync` を呼ぶと、journal が退役済み状態を返すため旧 source は不要です。移行途中なら旧 source を登録して再開します。

複数ファイルへ分割する場合はターゲットごとに投影を宣言します。選択元だけを先にマージし、それぞれの target に必要な subtree を渡します。

```csharp
var migration = new StateStorageMigrationDefinition<AppSettings.Fragment>(
    "settings-json-to-split-files-v1",
    ["legacy-json"],
    [
        new("database-file", fragment => new AppSettings.Fragment
        {
            Database = fragment.Database,
        }),
        new("ui-file", fragment => new AppSettings.Fragment
        {
            Ui = fragment.Ui,
        }),
    ],
    retireSources: true);
```

全宛先の検証後に、仮想解決で実効モデル不変が証明できた場合に限り、選択ソースをそのオプション実体から除去するには `retireSources: true` を渡します。結果の `RetiredSourceIds` に列挙されます。これは実行中オプション構成の変更であり、裏データは削除しないため、将来の起動向けにアプリの登録からも退役ソースを除去してください。

## 実務メモ

* 宛先検証が成功するまで元データは残します。失敗したら同じソース・宛先定義・journal で再試行します。
* 旧ファイルの削除は明示的なアプリ判断でのみ行います — 退役は裏データを決して消しません。
* 形式変更 (例: JSON→YAML) では旧表現を読み取り専用フォールバック入力としてモデル化し、そのソース ID を新しい書き込み先に移行します。

## 次のステップ

* [スキーマ移行](./schema-migration.md)。
* 旧来コーデックの手順は [Configuration.Writable の取り込み](./adopting-configuration-writable.md)。
