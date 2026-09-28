---
title: フォールバックと独自ソース
description: 等価な表現の束ねとリソース×コーデックの合成。
---

# フォールバックと独自ソース

## フォールバックソース

同じ論理状態の直列化表現を束ねるには、`FallbackStateSource<TFragment>` を使います（例: 正規の JSON ファイルと旧仕様の YAML ファイル）。
優先度順に最初の読み取り成功候補を選び、その候補を 1 つのソースとして公開します。別形式の値は重ね合わせません。
候補ごとのフォールバック条件に従って候補を切り替えます。

既定では、書き込みは現在アクティブな書き込み可能候補へ、無ければ最優先の書き込み可能候補へ行います。
正規ファイルのような固定候補に書き込みを寄せるには、`writeSourceId` を設定します。
読み取りによって書き込みや状態のコピーが発生することはありません。
候補ソースとリソースは呼び出し側所有のまま維持されます。

読み取り時の暗黙昇格は行いません。選択中の旧表現を正規候補へ明示的に materialize する場合は、固定書き込み先を指定したうえで読み取り結果を同じ fallback writer に渡します。revision check が選択状態と書き込み先候補の変更を検出します:

```csharp
using Configlue.State;

var snapshot = await fallback.ReadAsync();
if (snapshot.Status == StateReadStatus.Success)
{
    await fallback.WriteAsync(
        new StateWriteRequest<AppSettings.Fragment>(
            snapshot.Value!,
            snapshot.Revision,
            CheckRevision: true));
}
```

`writeSourceId` の候補は fallback 順で選択元より前に置いてください。書き込み後も旧候補は残り、正規候補が後で利用できなくなれば failback に使われます。別リソース間の操作は原子的ではなく、書き込み先の検証は writer の契約に従います。

状態を別の論理ソースや表現へ移す場合は、明示的な移行先 projection を指定して `IConfiglueSources<T>.MigrateSourcesToTargetsAsync` を使います。この移行 API は書き込み先を検証し、部分完了後の再試行にも対応します。

## 独自ソース

リソースとコーデックから型付きソースを合成するには、`SerializedStateSource.FromResource<T>` を使います。
ライターとウォッチャーは自動検出されます。

```csharp
using Configlue.Codecs;
using Configlue.Resources;
using Configlue.State;
using Configlue.Provider.Json;

var currentSource = SerializedStateSource.FromResource<AppSettings.Fragment>(
    "current",
    fileResource,
    new JsonStateCodec<AppSettings.Fragment>());
```

リソースは論理ソース ID や物理出どころラベルとは別に、安定した `ResourceId` を公開できます。
セクションビューは背後の同一性を継承し、独自リソースは `IResourceIdentity` 実装か `SerializedStateSource.FromResource`・`StateSource`・セクションビューへの引数で ID を供給できます。

完全手組みのソースは `IStateReader<TFragment>`（必要に応じて `IStateWriter<TFragment>` や `IStateWatcher`）を実装します。
登録時は `Sources(sources => sources.Add(existingSource))` または DI の `(provider, sources) => ...` オーバーロードで `sources.Add(id, reader, priority, fallbackCondition)` を呼び出します。

### 複数リソースを 1 つの論理ソースにまとめる

複数リソースが疎フラグメントを寄与し、options ランタイムには 1 つの論理ソースとして見せる場合は `CompositeStateSource<TFragment>` を使います。
書き込み先は既定のコンポーネントとメンバーパスごとのルーティングで明示します。

```csharp
using Configlue.Sources;
using Configlue.State;

var combined = new CompositeStateSource<AppSettings.Fragment>(
    new StateSourceSet<AppSettings.Fragment>([globalSource, localSource]),
    defaultWriteSourceId: "local",
    writePlan: new StateWritePlan(new Dictionary<string, string>
    {
        ["Policy.Endpoint"] = "global",
    }));
model.Sources(sources => sources.Add(combined.CreateSource("common-files", priority: 100)));
```

各コンポーネントは低優先度から高優先度へマージされます。
コンポーネントの `fallbackCondition` が、欠損または一時利用不可のフラグメントを省略できるかを決定します。
1 回の読み取りで成功したコンポーネントは同じスキーマメタデータを返す必要があり、結合後にスキーマ移行を 1 回実行します。
コンポーネントのリビジョンとウォッチャーは、論理ソースのリビジョンの内側に保持されます。
モデル編集では最も具体的なメンバーパスに従って入れ子メンバーをルーティングします。
Unset は所有コンポーネントの寄与を取り除き、下位優先度の値があれば再び見えるようにします。
生成された入れ子 Patch は、最も具体的なメンバーパスに従って変更をルーティングします。
書き込み先は書き込み可能なコンポーネントでなければなりません。
合成ソースは単一の `ResourceId` を持たず、コンポーネントの書き込みには既存のリソースバッチ処理が使われます。
異なるリソースへの書き込みは順次実行され、原子的ではありません。
後続リソースで失敗した場合は、`StateMultiWriteException` が完了済みソース、失敗したリソース・ソース、未実行ソース、および元例外（`InnerException`）を保持します。

## 次のステップ

* [解決とマージ](../layering/resolution-and-merge.md)。
* ソース間の引っ越しは [保存場所移行](../migration/storage-migration.md)。
