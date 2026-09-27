---
title: フォールバックと独自ソース
description: 等価な表現の束ねとリソース×コーデックの合成。
---

# フォールバックと独自ソース

## フォールバックソース

同じ論理状態の直列化表現の束ねには `FallbackStateSource<TFragment>` を使います (例: 正規 JSON ファイルと旧 YAML ファイル)。優先度順に最初の読み取り成功候補を選び、それを1つのソースとして公開します — 別形式の値は重ね合わせません。各候補のフォールバック条件に従います。

既定では書き込みはアクティブな書き込み可能候補へ、無ければ最優先の書き込み可能候補へ行きます。正規ファイルのような固定候補に寄せるには `writeSourceId` を設定します。作成時に状態コピーも他表現の削除もしません。候補ソースとリソースは呼び出し側所有のままです。

選択中の値を欠損している上位の書き込み可能候補へ移すには `PromoteAsync("canonical")` を明示的に呼びます。昇格前に選択元の revision を再確認し、移行先の欠損 revision を条件にして書き込みます。移行先がすでに選択中なら `WasAlreadyPromoted` が立った結果を返します。移行先に既存状態があれば上書きせず、元の表現も削除しません。別リソース間の処理をトランザクション化できないため、最終 revision 確認の直後に起きた元データの変更とコピーが競合する可能性は残ります。

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

複数の読み取り専用リソースが疎フラグメントを寄与し、options runtime には1つの論理ソースとして見せる場合は `CompositeStateSource<TFragment>` を使います:

```csharp
var combined = new CompositeStateSource<AppSettings.Fragment>(
    new StateSourceSet<AppSettings.Fragment>([globalSource, localSource]));
model.Sources(sources => sources.Add(combined.CreateSource("common-files", priority: 100)));
```

各 component は低優先度から高優先度へマージされます。component の `fallbackCondition` が、欠損または一時利用不可の fragment を省略できるか決めます。1回の読み取りで成功した component は同じ schema metadata を返す必要があり、結合後に schema migration を1回実行します。component の revision と watcher は論理ソースの revision の内側に保持されます。この合成は読み取り専用で、単一の `ResourceId` を持ちません。書き込み先は別途登録した source に保持し、編集をそこへ routing します。

## 次のステップ

* [解決とマージ](../layering/resolution-and-merge.md)。
* ソース間の引っ越しは [保存場所移行](../migration/storage-migration.md)。
