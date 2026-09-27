---
title: バックアップ・ログ・診断
description: ファイルの世代バックアップと復元、ログ記録、出どころ説明、診断。
---

# バックアップ・ログ・診断

## ファイルバックアップ

ファイルリソースは既定で不可分な `.bak` を1世代保持します。バックアップは Windows ではリソースファイルと同じ場所の `backup/`、その他の OS では `.backup/` に保存し、Windows ではディレクトリとファイルを隠し属性にします。`FileResourceOptions` で複数世代や保存先を変えられます。相対 `BackupDirectory` はリソースファイルのディレクトリを基準にします。`RestoreLatestBackupAsync` で最新世代を明示復元できます。

```csharp
var resource = new FileResource(
    "settings.json",
    new FileResourceOptions { BackupMaxCount = 5, BackupDirectory = "my-backups" });
```

`BackupMaxCount = 0` でバックアップ無効化です。不可分書き込み (一時ファイル+リネーム) と再試行つきアクセスで並行保存も安全です。

自動復旧は既定で無効です。JSON file source では `ResourceOptions` から有効にできます。

```csharp
model.UseJsonFile(new JsonFileSourceOptions
{
    Id = "settings",
    Path = "settings.json",
    ResourceOptions = new FileResourceOptions { AutomaticBackupRecovery = true },
});
```

ファイルがない場合や JSON が壊れている場合、最新バックアップ (Linux/macOS なら `.backup/settings.json.bak`) を読み直してデコードでき、失敗した読み取りの後に元ファイルが変更されていない場合にだけ復元します。既定の JSON codec は不正な JSON を復旧対象として判定します。独自 codec の形式エラー復旧には `IStateCodecRecoveryPolicy` の実装が必要です。ファイル欠損からの復旧にはこの policy は要りません。

ファイル書き込みは一時的な共有エラーを既定で最大 3 回再試行し、各試行の間に 100ms 待ちます。`RetryCount` と `RetryDelay` で変更できます。`RetryDelayFactory` を設定すると、1 始まりの再試行回数ごとに待ち時間を計算できます。

```csharp
var options = new FileResourceOptions
{
    RetryCount = 5,
    RetryDelayFactory = attempt => TimeSpan.FromMilliseconds(100 * attempt),
};
```

## 出どころと診断

* `IReadOnlyOptions<T>.ExplainAsync("Database.Host")` は実効値と優先度順の各存在ソース寄与を返します。設定 UI やトラブルシュートに使います。
* `IReadOnlyOptions<T>.ConfigurationInfo` は実効読みパス・次回書き込みパス・形式拡張子・インスタンス名・セクションなどのプロバイダー非依存メタデータを公開します。
* `IReadOnlyOptions<T>.GetDiagnostics()` はそのオプションランタイムの設定済みソース構成の不変スナップショットを返します。ソース ID・優先度・フォールバック方針・読み/書き/監視可否・物理出どころ・リソース同一性・退役状態に加え、既定と属性パス単位の書き込み経路も報告します。`GetWriteSourceId("Database.Endpoint")` で登録レベルの経路を解決できます。操作単位の書き込みプランは対象外です。最新の読み結果とリビジョンは `ReadAsync`、実効値と寄与ソースは `ExplainAsync(path)`、完了した書き込みは書き込み結果を使います。

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
