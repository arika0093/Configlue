---
title: 変更と検証
description: 変更通知、デバウンス、DataAnnotations、独自バリデーター。
---

# 変更と検証

## 変更検出

```csharp
using var changeSubscription = options.OnChange(updated =>
    Console.WriteLine($">> Settings changed: {updated.Name}"));
```

ファイル・HTTP (ポーリング)・独自ウォッチャーソースの更新は同じコールバックに流れます。DI では `IOptionsMonitor<T>.OnChange` も使えます。

変更通知は既定で 300ms デバウンスされます。登録時に `onChangeDebounce: TimeSpan.Zero` を渡すと無効化でき、大きくすれば高頻度の外部編集をまとめられます。

値変更とは別に、バックグラウンド再読み込みの失敗を購読できます:

```csharp
using var reloadFailureSubscription = options.OnReloadFailed(exception =>
    logger.LogError(exception, "Configuration reload failed"));
```

watcher または再読み込みで発生した例外が通知されます。変更後の state が `NotFound` / `Unavailable` になった場合は、その status を示す `InvalidOperationException` が渡されます。明示的な `ReadAsync` の失敗や `OnChange` listener の例外はこの通知に含まれません。reload-failure listener が例外を投げてもログに記録し、他 listener と watcher の再試行は継続します。

## 検証

DataAnnotations 検証は既定で保存時に実行されます。無効にするには `ValidateDataAnnotations = false` を設定します。NativeAOT など実行環境が動的コードをサポートしない場合、リフレクションを使う DataAnnotations 検証は自動的にスキップされます。登録済みの独自バリデーターは引き続き実行されます。

検証に失敗すると `ConfiglueValidationException` が送出され、options 名・型・すべての失敗メッセージを確認できます。この検証契約は Configlue Core に属し、`Microsoft.Extensions.Options` を必要としません。

```csharp
conf.Add<UserSetting>(model =>
{
    model.ValidateDataAnnotations = false; // 必要な場合のみ
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
* ファイル安全と `ExplainAsync` は [バックアップ・ログ・診断](../advanced/backups-and-observability.md)。
