---
title: "設計: Source"
description: 論理的な寄与。優先度・フォールバック・投影・マウント。
---

# 設計: Source（寄与）

Source は「どの項目を、どの優先度で出すか」という論理的な寄与です。読み・書き・監視の機能を独立に公開できます。Resource が置き場所なら、Source はその置き場所の「使い方」です。

## 優先度とフォールバック

複数 Source に同じ項目があるとき、数字の大きい `Priority` が勝ちます。`FallbackStateSource` は同じ論理状態の別表現（正規 JSON と旧 YAML など）を束ね、先に読めた候補をその Source の顔として出します。形式違いの値を重ねることはしません。

ファイルがないときの素通りと、それ以外の読み取り失敗の伝播は Source の約束です。詳しい組み立ては[ファイル・形式・セクション](../sources/files-and-sections.md)や[環境変数とコマンドライン](../sources/environment-and-commandline.md)を見てください。

## 読み取り専用という性質

環境変数・コマンドライン・既定の HTTP は読み取り専用です。読み取り専用の寄与が隠している値を書き込み側から変えようとすると、黙って無視するのではなく競合で失敗します。保存の前に `ExplainAsync` で出どころを確かめる癖が効きます。

## 投影とマウント

モデルの部分木を別 Source に預ける仕組みがふたつあります。

- **投影:** 既存 Source の値を別モデルの形に写す。宛先単位の検証と再試行可能な移行に使います。
- **マウント:** モデルの入れ子パスに別 Source を取り付ける（`AddMounted`）。たとえば `Policy` だけを HTTP 層に預けられます。詳しくは[マウントと投影](../layering/mount-and-project.md)を見てください。

`UseCommonSources` のようなプリセットは、この Source の組み立てを定番形に畳んだものです（[共通レイヤーソース](../basic-usage/common-sources.md)）。
