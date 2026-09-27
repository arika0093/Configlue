---
title: 読み書き・編集セッション・パッチ
description: GetValueAsync、SaveAsync、編集セッション、パッチ。
---

# 読み書き・編集セッション・パッチ

## 現在値を読む

```csharp
var setting = await options.GetValueAsync();
Console.WriteLine($">> Name: {setting.Name}");
```

読み取りは全ソースを優先度で解決し、ディープコピーを返します。DI では同期の `IOptions<T>.Value` / `IOptionsSnapshot<T>` / `IOptionsMonitor<T>` アダプターも使えますが、非同期フローでは async メソッドを使ってください。

同期処理では `options.CurrentValue` の初回アクセスでソースを解決し、ディープコピーを取得できます。以降はキャッシュ値の clone を返します。watcher による再読み込みが成功するとキャッシュを更新し、書き込み成功後はキャッシュを無効化して次回アクセスで再読み込みします。watcher がない場合、外部変更は自動検出されないため、最新値には `GetValueAsync` を使います。初回 getter は非同期 source の読み取り完了までブロックします。DI の `IOptionsMonitor<T>.CurrentValue` は独自の watcher 対応キャッシュを持ちます。

生成 clone は入れ子の Configlue model、一般的なコレクション、public parameterless constructor があり public instance state が public get/set property で構成される通常の POCO を複製します。POCO 間の共有参照と循環参照も維持します。public field・read-only property・constructor 引数・required/init-only property がある型、および未対応の collection に含まれる値は参照のまま残るため、そのような値を複製する場合は options runtime ごとに clone 戦略を設定します:

```csharp
config.Add<AppSettings>(model =>
{
    model.UseCloneStrategy(static original =>
    {
        var clone = original.DeepClone();
        clone.CustomState = original.CustomState?.DeepClone();
        return clone;
    });
});
```

この戦略は公開 read の戻り値、編集セッションの draft と baseline、各変更通知の値を複製し、model 全体の保存では source fragment を作る前にも複製します。入力を変更せず、入力と別の model を返し、その可変メンバーも入力と共有しないようにしてください。省略時は生成 clone を使います。`ApplyPatchAsync` と `ApplyPatchesAsync` は fragment を直接受け取るため、fragment 内の独自可変値は呼び出し側で複製してください。

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

設定画面で複数変更をまとめて適用する場合は `BeginConfigureAsync` を使います。セッションは `SaveAsync` までインメモリで、破棄すれば未保存の変更は捨てられます。保存中に破棄した場合、その保存は完了し、以後の編集や保存はできません。保存直前に全ソースのリビジョンベクターを比較し、参加ソースが変わっていれば `StateConflictException` で失敗します。

同期処理からは `options.BeginConfigure()` も使えます。非同期 source の読み込み中は呼び出し元をブロックするため、非同期処理では `BeginConfigureAsync` を使ってください。

```csharp
using var edit = await options.BeginConfigureAsync();
edit.Update(value => value.SomeSetting = newValue);
// edit.ResetToLoaded();  // セッション開始時に読み込んだ値へ戻す
// edit.ResetToDefault(); // モデルの既定値へ戻す
await edit.SaveAsync();
```

`Value` と `CurrentValue` で編集途中の値を参照できます。`Update` はその値を直接編集します。リセット API は全体、または選択したメンバーだけを読み込み時点のスナップショットか新しいモデル既定値へ戻します。保存に成功した後もセッションは再利用でき、次の保存では直前の成功以降の変更を記録します。`BeginConfigureAsync` が返すセッションは両方のリセット基準値を持ちます。2引数コンストラクターで直接生成したセッションにはモデル既定値がないため、`ResetToDefault` を使うには既定値を渡すオーバーロードが必要です。

操作単位の `StateWritePlan` でセッションを複数ソースに分割できます — [書き込み経路指定](../layering/write-routing.md) 参照。

## パッチ

生成された `TModel.Patch` 値は項目を個別に扱います。`Unset` は書き込みソースの寄与だけを取り下げ、下位の値を再び露出させます:

```csharp
await options.SavePatchAsync(patch => patch.Database.Host = "db.example.test");
await options.SavePatchAsync(patch => patch.Database.Password.Unset());

var patch = new AppSettings.Patch();
patch.SomeSetting = newValue;   // 設定
// patch.SomeSetting.Unset();   // このソースの寄与を取り下げ
await options.ApplyPatchAsync(patch);
```

明示的なソースローカル複数書き込みには `StateSourcePatch` 付きの `ApplyPatchesAsync` を使います。`ResourceId` を共有する互いに重ならないセクション更新は1回の物理書き込みにまとめられ、重なる範囲は拒否されます。結果には各ソースのリビジョンと物理書き込み回数が報告されます。異なるリソース間の書き込みはアトミックではありません。

## 次のステップ

* DI/非 DI の寿命と所有権は [アプリケーション構成](./app-setup.md)。
* 複数ソース編集と競合は [書き込み経路指定](../layering/write-routing.md)。
