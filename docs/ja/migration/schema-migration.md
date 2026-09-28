---
title: スキーマ移行
description: モデルに版をつけ、生成サポートで旧フラグメントを移行する。
---

# スキーマ移行

設定ファイルは育ちます。項目の追加・削除は素直で、クラスを変えて新規項目に既定値を持たせるだけです。ほとんどのプロバイダーは問題なく扱えます。非互換な変更には版上げが必要です。

## 非互換な変更

版を上げ、旧形状を別名で残し、以前の版として宣言します。移行メソッドを実装すれば、インターフェイスは自動提供されます。

```csharp
// Version 2 (新)
[ConfiglueModel("UserSetting", Version = 2)]
public partial class UserSetting
{
    public string Name { get; set; } = "default name";
    public int Age { get; set; } = 20;
}

// Version 1 (旧)
[ConfiglueModel("UserSetting", Version = 1)]
public partial class UserSettingV1
{
    public string FirstName { get; set; } = "first";
    public string LastName { get; set; } = "last";
    public int Age { get; set; } = 20;
}

[ConfigluePreviousVersion(typeof(UserSettingV1))]
public partial class UserSetting
{
    public UserSetting Migrate(UserSettingV1 source) => new()
    {
        // FirstName と LastName を Name に結合。
        // 他の項目も忘れず写す。
        Name = $"{source.FirstName} {source.LastName}",
        Age = source.Age,
    };
}
```

ライブラリはファイルを読み、版を見て、`Version = 1` なら `UserSettingV1` で読んで `Migrate` を呼び、`Version = 2` なら直接読みます。保存は常に最新版で書きます。多版にわたる連鎖は順に移行されます。

改名・削除された履歴項目には、現行モデルに `[ConfigluePreviousVersion(typeof(SettingsV1))]` を宣言し、`Settings.CreateSchemaDispatcher(...)` を `SerializedStateSource.FromResource` に渡します。現行フラグメント変換の前にスキーマメタデータでデコードし、疎の存在有無を保持します。同じ生成形状の旧フラグメント移行には `IStateSchemaMigration<TFragment>` 実装をサービス登録します。

## 存在を意識した移行

生成された `FragmentBuilder`・`Patch` メンバーは `ref` プロパティです: `builder.Value = 123`、`builder.Value.Set(123)`、`builder.Value.Unset()`、`builder.Value.CopyFrom(source.OldValue)`。生成 Patch は `IConfiglueMemberPatch.SelectMembers(memberIds)` も実装し、指定した安定 schema member ID の操作だけを `Unchanged`・`Set`・`Unset` の状態のままコピーします。これにより、ソースルーターは明示的な null/default 値や削除操作を通常のフラグメント値へ変換せずに patch を分割できます。既存の独自 `IConfigluePatch` 実装はそのまま利用でき、member 単位のルーティングを行う場合に `IConfiglueMemberPatch` を実装します。改名メンバーは `builder.NewName.CopyFrom(previous.OldName)` と明示代入します。生成子型が変わった入れ子メンバーは存在値を変換します (例: `builder.Database.CopyFrom(previous.Database, static value => value is null ? null : NewDatabase.Fragment.FromPrevious(value))`)。ソースローカル Patch でも変換 overload を使えます: `patch.RetryCount.CopyFrom(previous.LegacyCount, static value => value?.Length ?? 0)` は、source が Missing なら `Unset`、存在する場合は `null`・default を含め変換後の値を `Set` にします。`Fragment.FromPrevious` は現行生成モデルが以前の子型を宣言する同名入れ子子に再帰し、Missing・存在 `null`・存在値を保持します。`CreateSchemaDispatcher` も既定で同じ移行を使います。

## 次のステップ

* [保存場所移行](./storage-migration.md)。
* [Configuration.Writable の取り込み](./adopting-configuration-writable.md)。
