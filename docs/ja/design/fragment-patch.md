---
title: "設計: Fragment と Patch"
description: 疎な差分と一項目の編集。存在の有無を保つ仕組み。
---

# 設計: Fragment と Patch（差分と編集）

Fragment と Patch は、解決・移行・投影・書き込み計画が動く土台です。アプリコードは普通のモデル値を触り、裏側でこのふたりが差分を受け持ちます。

## Fragment: 「あるものだけ」を保つ

生成された Fragment は、各モデル項目の有無を保持します。大事なのは「項目がない」と「`null` や既定値が設定されている」を区別する点です。重ね合わせで「未設定」が「既定値に設定」を上書きしません。

解決は Fragment の上で動きます。各 Source が持ち寄った Fragment のうち、存在する項目だけを優先度順に合成します。移行も Fragment の上で動きます。`Fragment.FromPrevious` が同名・同型の項目を版を越えて写し、改名分だけ明示的に書きます。

## Patch: 一項目の編集

生成された `TModel.Patch` は一項目の編集の断片です。`ApplyPatchAsync` で単一項目を、明示的な宛先指定では `StateSourcePatch` の列挙と `ApplyPatchesAsync` で複数 Source への分割書き込みを行います。`Unset` は書き込み Source の寄与だけを取り除きます。

## マージ方式の指定

項目ごとの合成の癖は `[ConfiglueMerge]` で変えられます。`Append`・`Deep`・`Replace`・`SetUnion` の組み込みに加え、独自戦略型も指定できます。コレクションの重ね方や並べ替えの意味がここで決まります。詳しくは[解決とマージ](../layering/resolution-and-merge.md)を見てください。
