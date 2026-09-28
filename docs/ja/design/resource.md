---
title: "設計: Resource"
description: バイトの置き場所。ファイル・セクション・ZIP・HTTP・メモリ。
---

# 設計: Resource（置き場所）

Resource は「バイトがどこにあるか」だけを表します。値の意味も、どの項目に使うかも知りません。置き場所の種類は次のとおりです。

## ファイル

`FileResource` が中心です。原子的書き込みとバックアップ世代を持ちます。既定で1世代の `.bak` を保ち、`FileResourceOptions` で世代数や退避先を変えられます。壊れたときは `RestoreLatestBackupAsync` で最新の世代に戻します。詳しくは[バックアップと監視](../advanced/backups-and-observability.md)を見てください。

## セクション

ファイルの一部だけを切り出す見方です。`JsonSectionResource` が `App:Policy` のような入れ子パスを独立した Resource として扱い、書き込み時に兄弟項目を保ちます。XML 要素・YAML マッピングにも同様の見方があります。同じファイルを指す互いに重ならないセクションは、ひとつの物理書き込みに束ねられます。

JSON と YAML のセクション書き込みは元の文書テキストに局所編集を適用し、無関係なコメント・空白・引用符・スカラー形式を保ちます。JSON と JSONC の両方でコメントと末尾カンマを読み書きできます。構造編集に対応しない Codec は従来どおり文書全体を置き換えます。

## ZIP・HTTP・メモリ

- `ZipEntryResource` はアーカイブ内の1エントリを論理 Resource にします。アーカイブの物理同一性とリビジョンを保ち、無関係のエントリは壊しません。重ならない更新は1回のアーカイブ書き込みに束ねられます。
- `HttpResourceReader` は `{root}/get` から読みます。ETag による条件付き書き込みとポーリングに対応し、書き込みは `Writable = true` のときだけ有効です。ASP.NET Core 側の配信には `Configlue.Resource.Http.AspNetCore` を使います。
- `InMemoryResource` はテスト用のダブルです。ファイルに触れずに解決・書き込み・監視を試せます（`Configlue.Testing`）。

## ResourceId の約束

各論理 Source は、自分が使う物理 Resource の `ResourceId` を公開できます。セクション・ZIP・投影はこの同一性を保つため、後の書き込み調整で保存場所を共有する更新をまとめられます。共有 Resource を束ねられないバックエンドは、グループ書き込みより先に失敗します。
