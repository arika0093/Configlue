---
title: 保存場所移行
description: 検証と退役つきでソース間に寄与をコピーする。
---

スキーマ移行が形状を進化させるのに対し、保存場所移行は寄与をソース間で引っ越します — 新ファイル場所、形式変更、層の統合などです。

## 単一ソースのコピー

`IConfiglueSources<T>.MigrateSourceAsync(sourceKey, targetKey)` は1つのソース寄与をコピーし、スキーマ移行連鎖を適用して選択先に書き込みます。安定したアプリケーション定義の論理名には `SourceKey<T>.Named("legacy")` と `SourceKey<T>.Named("current")` を使います。provider が生成する不透明 ID は診断に便利ですが、通常のアプリケーション呼び出しでは不要です。

## 退役つき複数宛先移行

`MigrateSourcesToTargetsAsync(sourceIds, targetProjections)` は選択寄与だけをマージし、宛先ごとにフラグメント投影を適用して、各宛先をリビジョンチェック・検証します。再試行時は選択ソースを読み直し、journal に完了記録がある宛先も含めて再確認します。宛先が最新ソース寄与の投影と一致している場合だけ書き込みを省略します。前回の実行から source が変わっていれば、宛先を新しい寄与に合わせて再調整します。後段の宛先で失敗したら、移行を再実行して再開します。複数宛先の書き込みはアトミックではありません。

複数宛先をプロセス再起動後も再開する場合は、`StateStorageMigrationDefinition<TFragment>` と `FileStateStorageMigrationJournal` を使います。journal は `StateStorageMigrationProgress` を migration ID ごとの JSON ファイルに永続化し、各宛先の検証後に更新します。ファイルは `FileResource` が revision check 付きで原子的に置換し、migration 全体の間はプロセス間 lease を保持するため、同じ ID の別プロセスは先行実行の終了を待ちます。独自 journal でも同じ動作が必要なら `IStateStorageMigrationLeaseProvider` を実装してください。lease を提供しない journal は呼び出し側で同時実行を調整します。

JSON ファイルから YAML ファイルへ形式を移す例です。両ソースの codec は同じ生成 Fragment に変換されるため、形式変換はターゲット source の writer が行います。

```csharp
using Configlue.Migrations;

var migration = new StateStorageMigrationDefinition<AppSettings.Fragment>(
    "settings-json-to-yaml-v1",
    ["legacy-json"],
    [new StateStorageMigrationTarget<AppSettings.Fragment>("settings-yaml", fragment => fragment)],
    retireSources: true);

var journal = new FileStateStorageMigrationJournal("./.configlue-migrations");
var progress = await ((IConfiglueSources<AppSettings>)state).MigrateAsync(migration, journal);
```

再起動時は state を組み立てる前に `journal.ReadAsync(migration.Id)` を呼び、`SourcesRetired` が true なら旧 JSON source を登録から省きます。定義と同じ ID で `MigrateAsync` を呼ぶと、journal が退役済み状態を返すため旧 source は不要です。移行途中なら旧 source を登録して再開します。

複数ファイルへ分割する場合はターゲットごとに投影を宣言します。選択元だけを先にマージし、それぞれの target に必要な subtree を渡します。

```csharp
using Configlue.Migrations;

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

全宛先の検証後に、仮想解決で実効モデル不変が証明できた場合に限り、選択ソースをその state 実体から除去するには `retireSources: true` を渡します。結果の `RetiredSourceIds` に列挙されます。これは実行中 state 構成の変更であり、裏データは削除しないため、将来の起動向けにアプリの登録からも退役ソースを除去してください。

## 実務メモ

* 宛先検証が成功するまで元データは残します。失敗したら同じソース・宛先定義・journal で再試行します。
* 旧ファイルの削除は明示的なアプリ判断でのみ行います — 退役は裏データを決して消しません。
* 形式変更 (例: JSON→YAML) では旧表現を読み取り専用フォールバック入力としてモデル化し、そのソース ID を新しい書き込み先に移行します。

## 次のステップ

* [スキーマ移行](./schema-migration.md)。
* 旧来コーデックの手順は [Configuration.Writable の取り込み](./adopting-configuration-writable.md)。
