---
title: 設計の全体像
description: Resource・Source・Codec・Fragment・Patch・Optionsの関係を掴む。
---

# 設計の全体像

Configlue は登場人物が6つだけです。関係は一直線で、覚える順番も決まっています。

```text
Resource（置き場所） → Codec（変換） → Source（寄与） → Fragment（差分） → Options（窓口）
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
| Options | アプリから見える窓口 | 読み・保存・監視・説明・診断 |

読みの流れはこうです。各 Source が Resource からバイトを取り、Codec で Fragment に変えます。ランタイムは存在する項目だけを優先度順に重ね、ひとつのモデルにします。

書きの流れは逆です。アプリは普通のモデル値を編集します。裏側では変更が Fragment の差分になり、`WriteRoute` や `WritePlan` の指す Source にだけ届きます。関係ない Source は汚しません。

## なぜ分けるのか

置き場所（Resource）と変換（Codec）と寄与（Source）を分けておくと、それぞれを別々に進化させられます。ファイルから HTTP に変えても、JSON から YAML に変えても、モデルの読み書きは変わりません。形の変更は版管理で、置き場所の引っ越しは検証付きコピーで扱います。詳しくは各ページを見てください。

- [Resource](./resource.md)：物理的な端点。ファイル・セクション・ZIP・HTTP・メモリ。
- [Source](./source.md)：論理的な寄与。優先度・フォールバック・投影・マウント。
- [Codec](./codec.md)：バイトと値の相互変換。形式ごとの注意点。
- [Fragment と Patch](./fragment-patch.md)：疎な差分と一項目の編集。
- [Options](./options.md)：読み書きの窓口。プロファイル・動的オプション・DI 適合。

より厳密な境界や実装状況のメモは[設計メモ](../reference/design-notes.md)にも残っています。
