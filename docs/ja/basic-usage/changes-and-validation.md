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

検証は保存時に実行されます。モデルビルダーの `ValidateDataAnnotations` で属性ルールを強制します:

```csharp
conf.Add<UserSetting>(model =>
{
    model.ValidateDataAnnotations = true;
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

クラスモデル登録には `IOptions<T>`、スコープ付き `IOptionsSnapshot<T>`、`IOptionsMonitor<T>` アダプターも付きます。同期の `Value`・`Get` 呼び出しは Configlue 状態を同期読みします。非同期フローでは `ReadAsync`・`GetValueAsync` を使ってください。

## 次のステップ

* プロバイダー登録は [ファイル・形式・セクション](../sources/files-and-sections.md)。
* ファイル安全と `ExplainAsync` は [バックアップ・ログ・診断](../advanced/backups-and-observability.md)。
