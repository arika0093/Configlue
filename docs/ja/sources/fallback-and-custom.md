---
title: フォールバックと独自ソース
description: 等価な表現の束ねとリソース×コーデックの合成。
---

# フォールバックと独自ソース

## フォールバックソース

同じ論理状態の直列化表現の束ねには `FallbackStateSource<TFragment>` を使います (例: 正規 JSON ファイルと旧 YAML ファイル)。優先度順に最初の読み取り成功候補を選び、それを1つのソースとして公開します — 別形式の値は重ね合わせません。各候補のフォールバック条件に従います。

既定では書き込みはアクティブな書き込み可能候補へ、無ければ最優先の書き込み可能候補へ行きます。正規ファイルのような固定候補に寄せるには `writeSourceId` を設定します。作成時に状態コピーも他表現の削除もしません。候補ソースとリソースは呼び出し側所有のままです。

状態を別の論理ソースへ移す場合は、明示的な移行先 projection を指定して `IWritableOptions<T>.MigrateSourcesToTargetsAsync` を使います。この移行 API は書き込み先を検証し、部分完了後の再試行にも対応します。`FallbackStateSource<T>` は等価な表現の選択に専念します。

## 独自ソース

リソースとコーデックから型つきソースを合成するには `SerializedStateSource.FromResource<T>` を使います。ライターとウォッチャーは自動検出されます:

```csharp
var currentSource = SerializedStateSource.FromResource<AppSettings.Fragment>(
    "current",
    fileResource,
    new JsonStateCodec<AppSettings.Fragment>());
```

リソースは論理ソース ID や物理出どころラベルとは別に安定した `ResourceId` を公開できます。セクションビューは背後の同一性を継承し、独自リソースは `IResourceIdentity` 実装か `SerializedStateSource.FromResource`・`StateSource`・セクションビューへの引数で ID を供給できます。

完全手組みのソースは `IStateReader<TFragment>` (必要に応じ `IStateWriter<TFragment>` / `IStateWatcher`) を実装し、`Sources(sources => sources.Add(existingSource))` か DI の `(provider, sources) => ...` オーバーロードで `sources.Add(id, reader, priority, fallbackCondition)` します。

### 複数リソースを1つの論理ソースにまとめる

複数リソースが疎フラグメントを寄与し、options runtime には1つの論理ソースとして見せる場合は `CompositeStateSource<TFragment>` を使います。書き込み先は既定の component と member path ごとの routing で明示します:

```csharp
var combined = new CompositeStateSource<AppSettings.Fragment>(
    new StateSourceSet<AppSettings.Fragment>([globalSource, localSource]),
    defaultWriteSourceId: "local",
    writePlan: new StateWritePlan(new Dictionary<string, string>
    {
        ["Policy.Endpoint"] = "global",
    }));
model.Sources(sources => sources.Add(combined.CreateSource("common-files", priority: 100)));
```

各 component は低優先度から高優先度へマージされます。component の `fallbackCondition` が、欠損または一時利用不可の fragment を省略できるか決めます。1回の読み取りで成功した component は同じ schema metadata を返す必要があり、結合後に schema migration を1回実行します。component の revision と watcher は論理ソースの revision の内側に保持されます。モデル編集では最も具体的な member path に従って nested member を routing します。Unset は所有 component の寄与を取り除き、下位優先度の値があれば再び見えるようにします。生成 Patch の nested member 操作は一体の操作として扱われ、子 path の route へ分割できません。書き込み先は書き込み可能な component でなければなりません。合成 source は単一の `ResourceId` を持たず、component の書き込みには既存の resource batching が使われます。異なる resource への書き込みは順次実行され、原子的ではありません。後続 resource で失敗した場合は `StateMultiWriteException` が完了済み source、失敗した resource/source、未実行 source、元例外 (`InnerException`) を保持します。

## 次のステップ

* [解決とマージ](../layering/resolution-and-merge.md)。
* ソース間の引っ越しは [保存場所移行](../migration/storage-migration.md)。
