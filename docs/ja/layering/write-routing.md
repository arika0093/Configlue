---
title: 書き込み経路指定
description: WriteRoute 既定、パス単位 WritePlan、競合、複数書き込み。
---

# 書き込み経路指定

書き込みは読み優先度と独立に宛先を選べます。`BeginConfigureAsync` や updater オーバーロードの編集は生成された意味的差分を使います。

## 既定経路

`WriteRoute` が既定の保存先を選びます。パスに所有者がなければ `WriteRoute` (または最優先の書き込み可能ソース) を使います:

```csharp
model.WriteRoute = StateWriteRoute.To("user-settings");
```

## パス単位プラン

`ConfiglueModelBuilder<T>.WritePlan` でパス/部分木ごとの既定所有者を宣言します。例えば `Database` を書き込み可能なユーザーオーバーレイに寄せつつ、無関係の値は下位ソースに残せます。最も具体的なパスが勝ちます。操作単位の `StateWritePlan` は一致パスについて登録経路を置き換え、入れ子モデル変更を複数ソースフラグメントに分割できます:

```csharp
// 1回の編集でモデルパスごとに書き込み先を変える。
var writePlan = new StateWritePlan(new Dictionary<string, string>
{
    ["Database"] = "database-settings",
    ["Database.Password"] = "secrets",
});
using var routedEdit = await options.BeginConfigureAsync(writePlan);
routedEdit.Value.Database!.Password = "updated";
var writeResult = await routedEdit.SaveAsync();
var sourceWrites = writeResult.MultiWriteResult;
```

`SaveAsync(value, writePlan)` オーバーロードは値を解決済み基準と比較し、変更パスのみを振り分けます。

## 検証と競合

プランは編集前にパスと宛先を検証し、書き込み前に完全解決モデルと全ソースリビジョンを検証します。戻り値の `StateWriteResult.MultiWriteResult` はソース単位のリビジョンと物理書き込み回数を報告します。異なるリソース間の書き込みはアトミックではありません。

競合は `StateConflictException` で失敗します:

* 読み取り専用寄与が要求値を覆っている — 例外に変更パスと寄与した読み取り専用ソースが入ります。
* 他ソース所有の値変更が必要、または上位ソースに隠される編集。
* `Append`/`SetUnion` 編集は各対象ソースのコレクション断片にリベースされ、リベース不能は失敗します。

## 次のステップ

* [マウントと投影](./mount-and-project.md)。
* 検証済み引っ越し後のソース退役は [保存場所移行](../migration/storage-migration.md)。
