---
title: "設計: Codec"
description: バイトと値の相互変換。形式ごとの注意点。
---

# 設計: Codec（変換）

Codec は Resource の I/O なしに、バイトと型付き値を相互変換します。「どう置くか」ではなく「どう読むか」の担当です。

## 形式ごとの Codec

- JSON: `JsonStateCodec`。セクション Resource・ファイル登録と組み合わせます。ソース生成の `JsonSerializerContext` を渡すとトリミング安全・NativeAOT 対応になります。JSON Schema 出力は独立パッケージ `Configlue.JsonSchema` が担います。
- XML: XML 用 Codec。セクション Resource とファイル登録があります。
- YAML: YAML 用 Codec。セクション Resource とファイル登録があります。キャメルケース名の例は `example/Example.ConsoleApp.Yaml` を見てください。
- ドキュメントレイアウト: JSON・YAML コーデックはシンプルレイアウト (`{ "$version": 1, ... }`、書き込み既定) と詳細 `$configlue`/`$value` エンベロープの両方を読みます。書き込みレイアウトはコーデックやファイルオプションの `DocumentLayoutOptions` で選びます。旧来 `Configuration.Writable` のファイルはシンプルドキュメントとして読みます。詳しくは[取り込みガイド](../migration/adopting-configuration-writable.md)を見てください。

## Byte transformer と state middleware

`IStateByteTransformer` は Resource と Codec の間で保存バイトを変換します。読み取りは登録順、書き込みは逆順です。任意パッケージ `Configlue.Transformer.AES` の `AesGcmStateByteTransformer` は AES-GCM で暗号化・認証し、認証に失敗した入力を backup recovery 対象として分類します。鍵はアプリケーション側で安全に管理し、不要になった transformer は破棄してください。`SerializedStateSource.FromResource` の `transformers` に渡せます。

Codec の後には `IStateMiddleware<T>` を置けます。これは型付き reader/writer を包み、監査・検証・正規化などを実装します。`middlewares` の先頭が外側になります。middleware が writer を包む場合、batch write にも参加させるなら、戻り値の writer で `IStateWriteBatchParticipant<T>` を引き継いでください。

```csharp
using Configlue.Transformer.AES;

using var encryption = new AesGcmStateByteTransformer(key);
var source = SerializedStateSource.FromResource<AppSettings.Fragment>(
    "remote",
    resource,
    new JsonStateCodec<AppSettings.Fragment>(),
    transformers: [encryption],
    middlewares: [new AuditMiddleware()]);
```

## Codec の選び方

迷ったら、ファイルの形式に合わせるだけで構いません。読みたい形式ごとに Codec を足し、書き込み先はひとつに絞ります（STEP 10 の YAML 主・JSON 従の形が典型です）。形式を混ぜても、Source の優先度と `WriteRoute` の考え方は変わりません。
