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

読み取りは全ソースを優先度順に解決し、ディープコピーを返します。
DI では同期の `IOptions<T>.Value` / `IOptionsSnapshot<T>` / `IOptionsMonitor<T>` アダプターも使えますが、非同期フローでは async メソッドを使ってください。

`IReadOnlyOptions<T>` の通常の読み取り API は `GetValueAsync` と `OnChange` です。高度な利用者は `IConfiglueOptions<T>` の `ReadAsync` から状態とリビジョンのメタデータを取得できます。Core に同期 `CurrentValue` プロパティはありません。
DI では opt-in の `Configlue.Extensions.MSOptions` パッケージが `IOptions<T>`、`IOptionsSnapshot<T>`、`IOptionsMonitor<T>` アダプターを提供します。
同期 getter は非同期ソースの読み取り中にブロックするため、非同期処理では `GetValueAsync` を使用してください。

自動生成される clone は、入れ子の Configlue モデル、一般的なコレクション、引数なし public コンストラクターを持ち public プロパティで構成される通常の POCO を複製します。
POCO 間の共有参照と循環参照も維持します。
public フィールド・読み取り専用プロパティ・コンストラクター引数・required/init-only プロパティがある型、および未対応のコレクションに含まれる値は参照のまま残るため、そのような値を複製する場合は options ランタイムごとに clone 戦略を設定します。

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

この戦略は公開 read の戻り値、編集セッションの draft と baseline、各変更通知の値を複製します。
入力を変更せず、入力と別のモデルを返し、その可変メンバーも入力と共有しないようにしてください。
省略時は自動生成された clone を使用します。
`SaveAsync` と `ApplyPatchesAsync` はフラグメントを直接受け取るため、フラグメント内の独自可変値は呼び出し側で複製してください。

## 疎に保存する

生成された Patch オーバーロードは、指定した項目だけを設定済み write ソースに書き込みます。

```csharp
await options.SaveAsync(patch => patch.SomeSetting = newValue);
```

触っていない項目は既存の疎状態を保ちます。
ソースの寄与を破壊的に置換する場合は、型付きソースハンドルを使います。

```csharp
var userKey = SourceKey<AppSettings>.Create(); // ユーザーソース登録時に再利用
var replacement = new AppSettings.Patch();
replacement.Name = "new-name";
await options.Source(userKey).ReplaceAsync(replacement);
```

## 編集セッション

設定画面で複数変更をまとめて適用する場合は、`IConfiglueOptions<T>` の `OpenEditSessionAsync` を使います。
`IWritableOptions<T>` は Patch 保存に特化しています。
セッションは `CommitAsync` までインメモリで管理され、破棄すれば未保存の変更は破棄されます。
コミット中に破棄した場合、そのコミットは完了し、以後の編集やコミットはできません。
書き込み前に最新状態を解決し、開始時の基準値からセッションの変更を rebase します。
互いに異なる項目への変更は保持されます。
同じ項目への同時変更は既定では競合として拒否されます。
モデル登録で `WriteConflictResolution = WriteConflictResolution.LastWriteWins` を設定すると、競合した項目にはセッション側の値を優先できます。
書き込み時にも宛先の revision を確認するため、最新状態の読み取り後に宛先が変わると `StateConflictException` が発生することがあります。

同期処理からは `options.OpenEditSession()` も使えます。
非同期ソースの読み込み中は呼び出し元をブロックするため、非同期処理では `OpenEditSessionAsync` を使ってください。

```csharp
using var edit = await options.OpenEditSessionAsync();
edit.Update(value => value.SomeSetting = newValue);
// edit.ResetToLoaded();  // セッション開始時に読み込んだ値へ戻す
// edit.ResetToDefault(); // モデルの既定値へ戻す
await edit.CommitAsync();
```

`Value` と `CurrentValue` で編集途中の値を参照できます。`Update` はその値を直接編集します。リセット API は全体、または選択したメンバーだけを読み込み時点のスナップショットか新しいモデル既定値へ戻します。コミットに成功した後もセッションは再利用でき、次のコミットでは直前の成功以降の変更を記録します。`OpenEditSessionAsync` が返すセッションは両方のリセット基準値を持ちます。2引数コンストラクターで直接生成したセッションにはモデル既定値がないため、`ResetToDefault` を使うには既定値を渡すオーバーロードが必要です。

操作単位の `StateWritePlan` でセッションを複数ソースに分割できます — [書き込み経路指定](../layering/write-routing.md) 参照。

## パッチ

生成された `TModel.Patch` 値は項目を個別に扱います。`Unset` は書き込みソースの寄与だけを取り下げ、下位の値を再び露出させます:

```csharp
await options.SaveAsync(patch => patch.Database.Host = "db.example.test");
await options.SaveAsync(patch => patch.Database.Password.Unset());

var patch = new AppSettings.Patch();
patch.SomeSetting = newValue;   // 設定
// patch.SomeSetting.Unset();   // このソースの寄与を取り下げ
await options.SaveAsync(patch);
```

明示的なソースローカル複数書き込みには `StateSourcePatch` 付きの `ApplyPatchesAsync` を使います。`ResourceId` を共有する互いに重ならないセクション更新は1回の物理書き込みにまとめられ、重なる範囲は拒否されます。結果には各ソースのリビジョンと物理書き込み回数が報告されます。異なるリソース間の書き込みはアトミックではありません。

1つのソースには、型付きキーとハンドルを使えます。`SaveAsync` は指定されていない寄与を保持し、`ReplaceAsync` は明示的な Set を残したうえで未指定メンバーを取り下げます:

```csharp
var userKey = SourceKey<AppSettings>.Create(); // same key used by source registration
var userSource = options.Source(userKey);
await userSource.SaveAsync(patch => patch.Database.Host = "db.example.test");
await userSource.ReplaceAsync(patch => patch.Database.Host = "db.example.test");
```

JSON・YAML・XML ファイルソースは正規化されたパスと、必要に応じてドキュメント内の section からも選択できます:

```csharp
using Configlue.Provider.Json;
using Configlue.Provider.Xml;
using Configlue.Provider.Yaml;

await options.Source(JsonFileSource.At("./settings.json")).SaveAsync(patch);
await options.Source(YamlFileSource.At("./settings.yaml", "App:Settings")).SaveAsync(patch);
await options.Source(XmlFileSource.At("./settings.xml", "App:Settings")).SaveAsync(patch);
```

これらのパス由来 selector は明示的な `Id` を指定せずに登録したソースに対応します。JSON の mount したソースでは `mountPath` にモデルパスを渡します (例: `JsonFileSource.At("./secrets.json", mountPath: "Secrets")`)。明示 ID を指定した場合は、対応する `SourceKey<TModel>` で選択します。

## 次のステップ

* DI/非 DI の寿命と所有権は [アプリケーション構成](./app-setup.md)。
* 複数ソース編集と競合は [書き込み経路指定](../layering/write-routing.md)。
