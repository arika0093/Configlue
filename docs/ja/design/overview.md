---
title: 設計の全体像
description: Resource・Source・Codec・Fragment・Patch・Stateの関係を掴む。
---

Configlue は登場人物が6つだけです。関係は一直線で、覚える順番も決まっています。

```text
Resource（置き場所） → Codec（変換） → Source（寄与） → Fragment（差分） → State（窓口）
                                              ↘ Patch（編集の断片）
```

## ひとことで言うと

| 概念 | ひとこと | 例 |
| --- | --- | --- |
| Resource | バイトの置き場所 | ファイル、ZIP 内エントリ、HTTP 応答、メモリ |
| Codec | バイトと値の変換 | JSON / XML / YAML の読み書き |
| Source | 論理的な寄与 | 「ユーザー設定ファイルの `Server` 部分」 |
| Fragment | 項目の有無を持った差分 | 「`Port` だけある」状態 |
| Patch | 一項目の編集の断片 | 「`Port` を 9000 に」 |
| State | アプリが使う窓口 | 読み・保存・監視・説明・診断 |

読みの流れはこうです。各 Source が Resource からバイトを取り、Codec で Fragment に変えます。ランタイムは存在する項目だけを優先度順に重ね、ひとつのモデルにします。

書きの流れは逆です。アプリは普通のモデル値を編集します。裏側では変更が Fragment の差分になり、`WriteRoute` や `WritePlan` の指す Source にだけ届きます。関係ない Source は汚しません。

置き場所・変換・寄与を分けておくと、それぞれを別々に進化させられます。ファイルから HTTP に変えても、JSON から YAML に変えても、モデルの読み書きは変わりません。形の変更は版管理で、置き場所の引っ越しは検証付きコピーで扱います。読み書きの窓口は [State](./state.md) で別に説明します。

## Resource: バイトの置き場所

Resource は「バイトがどこにあるか」だけを表します。値の意味も、どの項目に使うかも知りません。

* **ファイル。** `FileResource` が中心です。原子的書き込みとバックアップ世代を持ちます。既定で1世代の `.bak` を保ち、`FileResourceOptions` で世代数や退避先を変えられます。壊れたときは `RestoreLatestBackupAsync` で最新の世代に戻します。詳しくは[バックアップと監視](../advanced/backups-and-observability.md)を見てください。
* **セクション。** ファイルの一部だけを切り出す見方です。`JsonSectionResource` が `App:Policy` のような入れ子パスを独立した Resource として扱い、書き込み時に兄弟項目を保ちます。XML 要素・YAML マッピングにも同様の見方があります。同じファイルを指す互いに重ならないセクションは、ひとつの物理書き込みに束ねられます。JSON と YAML のセクション書き込みは元の文書テキストに局所編集を適用し、無関係なコメント・空白・引用符・スカラー形式を保ちます。JSON と JSONC の両方でコメントと末尾カンマを読み書きできます。構造編集に対応しない Codec は従来どおり文書全体を置き換えます。
* **ZIP・HTTP・メモリ。** `ZipEntryResource` はアーカイブ内の1エントリを論理 Resource にし、アーカイブの物理同一性とリビジョンを保ちます。無関係のエントリは壊さず、重ならない更新は1回のアーカイブ書き込みに束ねられます。`HttpResourceReader` は `{root}/get` から読み、ETag による条件付き書き込みとポーリングに対応します。書き込みは `Writable = true` のときだけ有効で、ASP.NET Core 側の配信には `Configlue.Resource.Http.AspNetCore` を使います。`InMemoryResource` はテスト用のダブルです（`Configlue.Testing`）。

各論理 Source は、自分が使う物理 Resource の `ResourceId` を公開できます。セクション・ZIP・投影はこの同一性を保つため、後の書き込み調整で保存場所を共有する更新をまとめられます。共有 Resource を束ねられないバックエンドは、グループ書き込みより先に失敗します。

## Codec: バイトと値の変換

Codec は Resource の I/O なしに、バイトと型付き値を相互変換します。「どう置くか」ではなく「どう読むか」の担当です。

* **JSON**: `JsonStateCodec`。セクション Resource・ファイル登録と組み合わせます。ソース生成の `JsonSerializerContext` を渡すとトリミング安全・NativeAOT 対応になります。JSON Schema 出力は独立パッケージ `Configlue.JsonSchema` が担います。
* **XML**: XML 用 Codec。セクション Resource とファイル登録があります。
* **YAML**: YAML 用 Codec。セクション Resource とファイル登録があります。キャメルケース名の例は `example/Example.ConsoleApp.Yaml` を見てください。
* **ドキュメントレイアウト**: JSON・YAML コーデックはシンプルレイアウト (`{ "$version": 1, ... }`、書き込み既定) と詳細 `$configlue`/`$value` エンベロープの両方を読みます。書き込みレイアウトはコーデックやファイルオプションの `DocumentLayoutOptions` で選びます。旧来 `Configuration.Writable` のファイルはシンプルドキュメントとして読みます。詳しくは[取り込みガイド](../migration/adopting-configuration-writable.md)を見てください。

### Byte transformer と state middleware

`IStateByteTransformer` は Resource と Codec の間で保存バイトを変換します。読み取りは登録順、書き込みは逆順です。任意パッケージ `Configlue.Transformer.AES` の `AesGcmStateByteTransformer` は AES-GCM で暗号化・認証し、認証に失敗した入力を backup recovery 対象として分類します。鍵はアプリケーション側で安全に管理し、不要になった transformer は破棄してください。`SerializedStateSource.FromResource` の `transformers` に渡せます。

Codec の後には `IStateMiddleware<T>` を置けます。これは型付き reader/writer を包み、監査・検証・正規化などを実装します。`middlewares` の先頭が外側になります。middleware が writer を包む場合、batch write にも参加させるなら、戻り値の writer で `ISourceWriteBatchParticipant<T>` を引き継いでください。

```csharp
using Configlue.Codecs;
using Configlue.State;
using Configlue.Sources;
using Configlue.Transformer.AES;

using var encryption = new AesGcmStateByteTransformer(key);
var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
    "remote",
    resource,
    new JsonStateCodec<AppSettings.Fragment>(),
    transformers: [encryption],
    middlewares: [new AuditMiddleware()]);
```

迷ったら、ファイルの形式に合わせるだけで構いません。読みたい形式ごとに Codec を足し、書き込み先はひとつに絞ります（YAML 主・JSON 従のガイドは典型的な構成例です）。形式を混ぜても、Source の優先度と `WriteRoute` の考え方は変わりません。

## Source: 論理的な寄与

Source は「どの項目を、どの優先度で出すか」という論理的な寄与です。型付きの読み書き監視契約は `Configlue.Sources.ISourceReader<T>`・`ISourceWriter<T>`・`ISourceWatcher` です。物理リソースの I/O には別の `Configlue.Resources.IResourceReader`・`IResourceWriter` 契約を使います。Resource が置き場所なら、Source は state への寄与の仕方です。

* **優先度とフォールバック。** 複数 Source に同じ項目があるとき、数字の大きい `Priority` が勝ちます。`FallbackStateSource` は同じ論理状態の別表現（正規 JSON と旧 YAML など）を束ね、先に読めた候補をその Source の顔として出します。形式違いの値を重ねることはしません。ファイルがないときの素通りと、それ以外の読み取り失敗の伝播は Source の約束です。詳しい組み立ては[ファイル・形式・セクション](../sources/files-and-sections.md)や[環境変数とコマンドライン](../sources/environment-and-commandline.md)を見てください。
* **読み取り専用という性質。** 環境変数・コマンドライン・既定の HTTP は読み取り専用です。読み取り専用の寄与が隠している値を書き込み側から変えようとすると、黙って無視するのではなく競合で失敗します。保存の前に `GetDetailsAsync` で出どころを確かめる癖が効きます。
* **投影とマウント。** モデルの部分木を別 Source に預ける仕組みがふたつあります。**投影**は既存 Source の値を別モデルの形に写し、宛先単位の検証と再試行可能な移行に使います。**マウント**はモデルの入れ子パスに別 Source を取り付け（`AddMounted`）、たとえば `Policy` だけを HTTP 層に預けられます。詳しくは[マウントと投影](../layering/mount-and-project.md)を見てください。

`UseCommonSources` のようなプリセットは、この Source の組み立てを定番形に畳んだものです（[共通レイヤーソース](../basic-usage/common-sources.md)）。

## Fragment と Patch: 差分と編集

Fragment と Patch は、解決・移行・投影・書き込み計画が動く土台です。アプリコードは普通のモデル値を触り、裏側でこのふたりが差分を受け持ちます。

* **Fragment** は、各モデル項目の有無を保持します。大事なのは「項目がない」と「`null` や既定値が設定されている」を区別する点です。重ね合わせで「未設定」が「既定値に設定」を上書きしません。解決は Fragment の上で動き、各 Source が持ち寄った Fragment のうち、存在する項目だけを優先度順に合成します。移行も Fragment の上で動きます。`Fragment.FromPrevious` が同名・同型の項目を版を越えて写し、改名分だけ明示的に書きます。
* **Patch** は生成された `TModel.Patch`、つまり一項目の編集の断片です。`SaveAsync` で単一項目を、明示的な宛先指定では `StateSourcePatch` の列挙と `ApplyPatchesAsync` で複数 Source への分割書き込みを行います。`Unset` は書き込み Source の寄与だけを取り除きます。
* **項目ごとのマージ方式**は `[ConfiglueMerge]` で変わります。`Append`・`Deep`・`Replace`・`SetUnion` の組み込みに加え、独自戦略型も指定できます。コレクションの重ね方や並べ替えの意味がここで決まります。詳しくは[解決とマージ](../layering/resolution-and-merge.md)を見てください。

## 境界・プロジェクト構成・実装状況

**resource** はファイル・ZIP エントリ・HTTP 応答のような物理的な端点を表します。**codec** は Resource I/O を行わずにバイトと型付き値を相互変換します。**source** は論理的な設定スナップショットを寄与し、読み・書き・監視の機能を独立に公開できます。生成された **fragment** は各モデル項目が「無い」か「ある（`null` や既定値を含む）」かを保持します。解決・移行・投影・書き込み計画は fragment の上で動き、アプリコードは普通のモデル値を編集します。

プロジェクトは `src/` 直下にあります。`Configlue` は DI 登録を含む Core・DI HTTP client adapter・JSON プロバイダー・JSON Schema 出力・HTTP リソース・共通レイヤーソース・環境変数ソース・ソースジェネレーターアナライザーを束ねるアセンブリなしメタパッケージです。`Configlue.Abstraction` が契約、`Configlue.Core` が解決・DI 登録・永続化ランタイム (汎用ファイルリソース含む) を持ちます。`Configlue.Extensibility` はシリアル化・変換リソース・マウント登録を行うプロバイダー SDK です。`Configlue.Extensions.DI` は名前付き `IHttpClientFactory` source adapter を追加し、任意の `Configlue.Extensions.MSOptions` は Microsoft options アダプターを提供します。`Configlue.Generator` が疎フラグメントとパッチを生成します。`Configlue.Provider.Json`・`.Xml`・`.Yaml` に形式コーデック・セクションリソース・ファイル登録があり、`Configlue.JsonSchema` がモデルから JSON Schema を生成します。`Configlue.Source.Environment`・`.CommandLine`・`.Presets` がソースと定番の重ね合わせプリセットを提供します（任意の `.Presets.Yaml`・`.Presets.Xml` アダプターつき）。`Configlue.Resource.Http`・`.S3`・`.Zip`・`.Http.AspNetCore` が転送と保存を担い、`Configlue.Testing` はインメモリダブルです。

基盤は揃っています。バックエンド非依存の読み書き監視契約、優先度つき解決、スキーマ移行とストレージ移行、投影、provenance details、トポロジ診断、任意の構造化ログ、セクションと ZIP のリソース、バックアップ世代と復元、生成された疎フラグメント、形式コーデック、JSON Schema 出力です。現在の API は完成品というより建築上の基盤です。

既知の制限:

* 異なる Resource をまたぐ書き込みは原子的ではありません。
* Source の退役は現在の state 実体に限定され、実データは残ります。次回起動以降に備えて登録を更新してください。
* Source 集合は state ランタイムに対して固定です。動的 state と永続プロファイルは、それぞれ独自の Source 集合を持つランタイムを丸ごと作成・削除できます。
* `FileStateStorageMigrationJournal` は移行 ID の実行中、プロセス間リースを保持します。`IStateStorageMigrationLeaseProvider` を実装しない独自 journal では、呼び出し側が同時実行を調整する必要があります。
* ウォッチャーは無効化シグナルを提供します。ポーリング・再試行・再接続の方針は各プロバイダーの責務です。

Configlue は既存 state 実体の Source 集合をその場で差し替えません。新しい Context を組み、必要なら明示的に移行し、アプリの利用側を切り替えてから古い Context を破棄してください。名前単位のライフサイクルは[動的 state](../profiles/dynamic-states.md)を見てください。
