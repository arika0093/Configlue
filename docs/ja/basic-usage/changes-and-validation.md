---
title: 変更と検証
description: 変更通知、デバウンス、DataAnnotations、独自バリデーター。
---

## 変更検出

```csharp
using var changeSubscription = options.OnChange(updated =>
    Console.WriteLine($">> Settings changed: {updated.Name}"));
```

ファイル・HTTP（ポーリング）・独自ウォッチャーソースの更新は同じコールバックに流れます。
DI では `IOptionsMonitor<T>.OnChange` も使用できます。

変更通知は既定で 300ms デバウンスされます。
登録時に `onChangeDebounce: TimeSpan.Zero` を渡すと無効化でき、大きくすれば高頻度の外部編集をまとめられます。

値変更とは別に、バックグラウンド再読み込みの失敗を購読できます。

```csharp
var diagnostics = context.GetDiagnostics<UserSetting>();
using var reloadFailureSubscription = diagnostics.OnReloadFailed(exception =>
    logger.LogError(exception, "Configuration reload failed"));
```

ウォッチャーまたは再読み込みで発生した例外が通知されます。
変更後の状態が `NotFound` / `Unavailable` / `Invalid` になった場合は、その状態を示す `InvalidOperationException` が渡されます。
明示的な `ReadAsync` の失敗や `OnChange` リスナーの例外はこの通知に含まれません。
`OnReloadFailed` リスナーが例外を投げてもログに記録され、他リスナーとウォッチャーの再試行は継続します。

## 検証

DataAnnotations 検証は既定で保存時に実行されます。
無効にするには `ValidateDataAnnotations = false` を設定します。
NativeAOT など実行環境が動的コードをサポートしない場合、リフレクションを使う DataAnnotations 検証は自動的にスキップされます。
登録済みの独自バリデーターは引き続き実行されます。

検証に失敗すると `ConfiglueValidationException` が送出され、オプション名・型・すべての失敗メッセージを確認できます。
この検証契約は Configlue Core に属し、`Microsoft.Extensions.Options` を必要としません。

読み取り時も検証されます。
`ReadValidationMode` で読み取り失敗の扱いを選びます。
`EffectiveThrow`（既定）は最終解決値が不正な場合に例外を送出します。
`StrictThrow` はいずれかのソースが不正値を寄与した時点で例外を送出します。
`IgnoreValue` は不正な寄与メンバーを除外して残りを解決します。
不正値を報告したソースは `Invalid` 読み取りステータスで来歴や詳細診断に残ります。
検証に失敗したウォッチャー再読み込みは `OnChange` リスナーには流されず、`OnReloadFailed` に通知されます。

同じメンバーへの競合編集は既定で拒否されます。
モデル登録の `WriteConflictResolution = WriteConflictResolution.LastWriteWins` を設定すると、同時に変更されたメンバーには編集セッションの値を優先します。
最新状態にある無関係な変更は保持し、ソースの書き込みでは引き続きリビジョンを確認します。

## 設定の詳細

`GetDetailsAsync()` は、実効値・ソース別寄与・編集可否・コレクション要素の出どころを持つ、生成された強い型のスナップショットを一度の解決から返します。

```csharp
var details = await options.GetDetailsAsync();

string name = details.Name;
bool editable = details.Name.IsEditable;
var origin = details.Name.Source?.DisplayName;
foreach (var source in details.Name.Sources)
{
    Console.WriteLine($"{source.Source.DisplayName}: {source.State} = {source.Value}");
}
```

```csharp
conf.Add<UserSetting>(model =>
{
    model.ValidateDataAnnotations = false; // 必要な場合のみ
    model.ReadValidationMode = ReadValidationMode.IgnoreValue; // 必要な場合のみ
    model.WriteConflictResolution = WriteConflictResolution.LastWriteWins; // 必要な場合のみ
    // ...sources...
});
```

```csharp
public partial class UserSetting
{
    [Required, MinLength(3)]
    public string Name { get; set; } = "default name";
    [Range(0, 150)]
    public int Age { get; set; } = 20;
}
```

Microsoft オプションバリデーターを使う場合はインスタンスを `AddConfiglueValidator<T>(IValidateOptions<T>)` で登録します:

```csharp
services.AddConfiglueValidator<UserSetting>(new UserSettingValidator());
```

旧フラグメントが同じ生成形状を持つ場合の `IStateSchemaMigration<TFragment>` 登録は [スキーマ移行](../migration/schema-migration.md) 参照。

Microsoft Options アダプターは `Configlue.Extensions.MSOptions` の `services.AddConfiglueMicrosoftOptions<T>()` で opt-in します。`IOptions<T>` は最初の値をキャッシュし、各 snapshot はスコープ内で値をキャッシュします。monitor は watcher を持つ source の場合、名前ごとに値をキャッシュし、変更通知後に成功した値へ更新します。getter は毎回 clone を返し、再読み込みに失敗した場合は最後に成功した値を保ちます。watcher を持つ source がない場合は無効化の合図がないため、`Get` のたびに現在状態を読みます。これらのアダプターは同期読みを使うため、非同期フローでは `ReadAsync`・`GetValueAsync` を使ってください。

## 次のステップ

* プロバイダー登録は [ファイル・形式・セクション](../sources/files-and-sections.md)。
* ファイル安全と設定詳細は [バックアップ・ログ・診断](../advanced/backups-and-observability.md)。
