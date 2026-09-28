---
title: バックアップ・ログ・診断
description: ファイルの世代バックアップと復元、ログ記録、出どころ説明、診断。
---

# バックアップ・ログ・診断

## ファイルバックアップ

モデル情報を持つ JSON・XML・YAML file source は、既定で不可分な `.bak` を1世代保持し、永続するユーザー領域に保存します。保存先の基準は Windows では `%LOCALAPPDATA%`、macOS では `~/Library/Application Support`、Linux では `$XDG_STATE_HOME` または `~/.local/state` です。既定の配置は `configlue-backups/{ModelId}.v{Version}/` で、同名ファイル同士が衝突しないようバックアップ名にリソースパス由来の安定したハッシュを含めます。Windows ではディレクトリとファイルを隠し属性にします。モデル情報のない単独の `FileResource` は従来どおり Windows では隣接する `backup/`、その他の OS では `.backup/` を使います。

`FileResourceOptions` で保存先と保持世代数を指定できます。`BackupDirectoryName` で `configlue-backups` の名前だけを変え、`BackupRootDirectory` で永続領域のルートを変更できます (相対パスはリソースファイルのディレクトリ基準)。`IncludeModelVersionInBackupDirectory = false` にするとフラットな配置になり、`BackupDirectoryMode = FileBackupDirectoryMode.ResourceDirectory` にすると従来どおりリソースファイルの隣に保存します。既存の `BackupDirectory` は保存先ディレクトリを直接指定する上書き設定です。相対パスはリソースファイルのディレクトリ基準で、`/` はリソースファイルと同じディレクトリを選びます。単独の `FileResource` でモデル/バージョン別の配置を有効にするには、コンストラクターに `backupSchema` を渡します。`BackupMaxCount` は最新世代を含む保持数で、`0` なら無効です。`RestoreLatestBackupAsync` で最新世代を明示復元できます。

```csharp
using Configlue.Provider.Json;
using Configlue.Resources;

model.UseJsonFile(new JsonFileSourceOptions
{
    Path = "settings.json",
    ResourceOptions = new FileResourceOptions
    {
        BackupDirectoryName = "my-backups",
        BackupMaxCount = 5,
    },
});
```

独自ルートとフラット配置を使うには `BackupRootDirectory` と `IncludeModelVersionInBackupDirectory = false` を設定します。不可分書き込み (一時ファイル+リネーム) と再試行つきアクセスで並行保存も安全です。

自動復旧は既定で無効です。JSON file source では `ResourceOptions` から有効にできます。

```csharp
using Configlue.Resources;

model.UseJsonFile(new JsonFileSourceOptions
{
    Id = "settings",
    Path = "settings.json",
    ResourceOptions = new FileResourceOptions { AutomaticBackupRecovery = true },
});
```

ファイルがない場合や JSON が壊れている場合、最新バックアップを読み直してデコードでき、失敗した読み取りの後に元ファイルが変更されていない場合にだけ復元します。以前のリソース隣接レイアウト、旧 current directory 基準の保存先、Configuration.Writable のタイムスタンプ付きバックアップも認識します。バックアップを作成する次回保存時に保持対象の世代を現行レイアウトへ移します。既定の JSON codec は不正な JSON を復旧対象として判定します。独自 codec の形式エラー復旧には `IStateCodecRecoveryPolicy` の実装が必要です。ファイル欠損からの復旧にはこの policy は要りません。

ファイル書き込みは一時ファイルの作成・書き込み・flush・置換の失敗を既定で 2 回再試行します (初回を含めて最大 3 回試行)。各試行の間は 100ms 待ち、キャンセルは再試行しません。`RetryCount` は初回後の再試行回数で、`RetryCount` と `RetryDelay` で変更できます。`RetryDelayFactory` を設定すると、1 始まりの再試行回数ごとに待ち時間を計算できます。

```csharp
var options = new FileResourceOptions
{
    RetryCount = 5,
    RetryDelayFactory = attempt => TimeSpan.FromMilliseconds(100 * attempt),
};
```

## 出どころと診断

* `await options.GetDetailsAsync()` は実効値と優先度順のソース別寄与を持つ型つきスナップショットを返します。設定 UI やトラブルシュートに使います。
* `IConfiglueDiagnostics<T>.GetDiagnostics()` はそのオプションランタイムの設定済みソース構成の不変スナップショットを返します。ソース ID・優先度・フォールバック方針・読み/書き/監視可否・物理出どころ・リソース同一性・退役状態に加え、既定と属性パス単位の書き込み経路も報告します。`GetWriteSourceId("Database.Endpoint")` で登録レベルの経路を解決できます。操作単位の書き込みプランは対象外です。最新の読み結果とリビジョンは `ReadAsync`、実効値と寄与ソースは `GetDetailsAsync()`、完了した書き込みは書き込み結果を使います。

```csharp
var diagnostics = options.GetDiagnostics();
var defaultWriteSource = diagnostics.GetWriteSourceId();
var endpointWriteSource = diagnostics.GetWriteSourceId("Database.Endpoint");
foreach (var source in diagnostics.Sources)
{
    Console.WriteLine(
        $"{source.Id}: priority={source.Priority}, active={source.IsActive}, " +
        $"read={source.CanRead}, write={source.CanWrite}, watch={source.CanWatch}, " +
        $"origin={source.PhysicalOrigin}, resource={source.ResourceId}");
}
```

## ログ

ファサードランタイムは DI に `ILoggerFactory` が登録されていれば使います。非 DI の呼び出し側は `ConfiglueModelBuilder<TModel>` に `Logger` を設定でき、`ConfiglueOptions<TModel, TFragment>` を直接組み立てる呼び出し側は任意の `logger` 引数を渡せます。ログは任意です。ソース読み判断・ウォッチャー失敗・書き込み・移行結果・リビジョン競合は、モデル・オプション名・ソース ID・物理出どころ・リソース ID などの構造化メタデータで記録されます。設定値そのものはログに出ません。

## 次のステップ

* [解決とマージ](../layering/resolution-and-merge.md)。
* [パッケージ](../reference/packages.md)。
