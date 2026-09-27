---
title: "設計: Codec"
description: バイトと値の相互変換。形式ごとの注意点。
---

# 設計: Codec（変換）

Codec は Resource の I/O なしに、バイトと型付き値を相互変換します。「どう置くか」ではなく「どう読むか」の担当です。

## 形式ごとの Codec

- JSON: `JsonStateCodec`。セクション Resource・ファイル登録・JSON Schema 出力と組み合わせます。ソース生成の `JsonSerializerContext` を渡すとトリミング安全・NativeAOT 対応になります。
- XML: XML 用 Codec。セクション Resource とファイル登録があります。
- YAML: YAML 用 Codec。セクション Resource とファイル登録があります。キャメルケース名の例は `example/Example.ConsoleApp.Yaml` を見てください。
- 旧資産: `ConfigurationWritableJsonStateCodec` / `ConfigurationWritableYamlStateCodec`。`Configuration.Writable` のファイルを読み取り専用の移行入力として読むための Codec です。詳しくは[取り込みガイド](../migration/adopting-configuration-writable.md)を見てください。

## Codec の選び方

迷ったら、ファイルの形式に合わせるだけで構いません。読みたい形式ごとに Codec を足し、書き込み先はひとつに絞ります（STEP 10 の YAML 主・JSON 従の形が典型です）。形式を混ぜても、Source の優先度と `WriteRoute` の考え方は変わりません。
