---
title: 読み書き
description: GetValueAsync、SaveAsync、編集セッション、パッチ。
---

# 読み書き

## 現在値を読む

```csharp
var setting = await options.GetValueAsync();
Console.WriteLine($">> Name: {setting.Name}");
```

読み取りは全ソースを優先度で解決し、ディープコピーを返します。DI では同期の `IOptions<T>.Value` / `IOptionsSnapshot<T>` / `IOptionsMonitor<T>` アダプターも使えますが、非同期フローでは async メソッドを使ってください。

## 疎に保存する

updater オーバーロードは現在値のディープクローンを編集し、変更パスのみを意味的差分として書き込みます:

```csharp
await options.SaveAsync(settings => settings.SomeSetting = newValue);
```

触っていない項目は既存の疎状態を保ちます。対照的に、値全体オーバーロードは書き込み先の寄与全体を置換します (モデルの既定値を含み、そのソースに無かった項目は保持されません):

```csharp
await options.SaveAsync(updatedConfig); // 書き込み先の全体置換
```

## 編集セッション

設定画面で複数変更をまとめて適用する場合は `BeginConfigureAsync` を使います。セッションは `SaveAsync` までインメモリで、破棄すれば変更は捨てられます。保存直前に全ソースのリビジョンベクターを比較し、参加ソースが変わっていれば `StateConflictException` で失敗します。

```csharp
using var edit = await options.BeginConfigureAsync();
edit.Value.SomeSetting = newValue;
await edit.SaveAsync();
```

操作単位の `StateWritePlan` でセッションを複数ソースに分割できます — [書き込み経路指定](../layering/write-routing.md) 参照。

## パッチ

生成された `TModel.Patch` 値は項目を個別に扱います。`Unset` は書き込みソースの寄与だけを取り下げ、下位の値を再び露出させます:

```csharp
var patch = new AppSettings.Patch();
patch.SomeSetting = newValue;   // 設定
// patch.SomeSetting.Unset();   // このソースの寄与を取り下げ
await options.ApplyPatchAsync(patch);
```

明示的なソースローカル複数書き込みには `StateSourcePatch` 付きの `ApplyPatchesAsync` を使います。`ResourceId` を共有する互いに重ならないセクション更新は1回の物理書き込みにまとめられ、重なる範囲は拒否されます。結果には各ソースのリビジョンと物理書き込み回数が報告されます。異なるリソース間の書き込みはアトミックではありません。

## 次のステップ

* DI/非 DI の寿命と所有権は [アプリケーション構成](./app-setup.md)。
* 複数ソース編集と競合は [書き込み経路指定](../layering/write-routing.md)。
