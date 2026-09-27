---
title: 書き込み経路指定
description: WriteRoute 既定、パス単位 WritePlan、競合、複数書き込み。
---

# 書き込み経路指定

書き込みは読み優先度と独立に宛先を選べます。`OpenEditSessionAsync` による編集は生成された意味的差分を使います。生成 Patch の `SaveAsync` は指定した操作だけを設定済み write source に適用します。

## 既定経路

`WriteRoute` が既定の保存先を選びます。パスに所有者がなければ `WriteRoute` (または最優先の書き込み可能ソース) を使います:

```csharp
model.WriteRoute = StateWriteRoute.To("user-settings");
```

`WriteRoute` を設定していない場合、明示的なパス所有者がない変更パスについて書き込み可能ソースを読み取り優先度順に評価します。明示経路に振り分けたパッチと候補を全ソースに適用した状態をシミュレーションし、要求された実効モデルになる最初の候補を使います。評価中は書き込まず、編集基準の全 revision を再確認してから選択先へ書き込みます。設定済み `WriteRoute` と明示的なパス経路は固定され、要求値を実現できなければ `StateConflictException` になります。`SaveAsync(value)` は引き続き選択した書き込み先の寄与全体を置き換えます。

## パス単位プラン

`ConfiglueModelBuilder<T>.WritePlan` でパス/部分木ごとの既定所有者を宣言します。例えば `Database` を書き込み可能なユーザーオーバーレイに寄せつつ、無関係の値は下位ソースに残せます。最も具体的なパスが勝ちます。操作単位の `StateWritePlan` は一致パスについて登録経路を置き換え、入れ子モデル変更を複数ソースフラグメントに分割できます:

```csharp
// 1回の編集でモデルパスごとに書き込み先を変える。
var writePlan = new StateWritePlan(new Dictionary<string, string>
{
    ["Database"] = "database-settings",
    ["Database.Password"] = "secrets",
});
using var routedEdit = await options.OpenEditSessionAsync(writePlan);
routedEdit.Value.Database!.Password = "updated";
var writeResult = await routedEdit.CommitAsync();
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
