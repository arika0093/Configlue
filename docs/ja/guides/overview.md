---
title: 機能説明について
description: やりたいことから記事を探す案内板。
---

# 機能説明について

機能説明は、辞書的に使うための記事集です。チュートリアルを終えたあと、やりたいことから探してください。順番に読む必要はありません。

## 構成と読み書き

| やりたいこと | 記事 |
| --- | --- |
| DI なし・ありの登録と寿命・所有権を知る（構成方法） | [アプリケーション構成](../basic-usage/app-setup.md) |
| 値の読み書き・編集セッション・パッチを使い分ける | [読み書き・編集セッション・パッチ](../basic-usage/reading-and-writing.md) |
| 共通・ローカル・指定・環境の定番構成を使う（保存場所のプリセット） | [共通レイヤーソース](../basic-usage/common-sources.md) |

## 保存場所と形式

| やりたいこと | 記事 |
| --- | --- |
| JSON・YAML・XML の形式とファイル保存・セクション名を使い分ける | [ファイル・形式・セクション](../sources/files-and-sections.md) |
| 環境変数・コマンドラインの読み取り専用ソースを重ねる | [環境変数とコマンドライン](../sources/environment-and-commandline.md) |
| HTTP・ZIP の資源をソースにする | [HTTP と ZIP](../sources/http-and-zip.md) |
| 正規ファイルと旧形式の読み替え・独自ソースを組む | [フォールバックと独自ソース](../sources/fallback-and-custom.md) |

## 重ね合わせ

| やりたいこと | 記事 |
| --- | --- |
| 優先度・解決・マージ方式を理解する | [解決とマージ](../layering/resolution-and-merge.md) |
| 保存先を既定と項目単位で決める（書き込み経路） | [書き込み経路指定](../layering/write-routing.md) |
| モデルの一部分を別ソースに預ける | [マウントと投影](../layering/mount-and-project.md) |

## 名前付きと検証

| やりたいこと | 記事 |
| --- | --- |
| 同じ型の設定を名前（インスタンス名）で使い分ける・実行時に増減する | [名前付きインスタンスと動的オプション](../profiles/dynamic-options.md) |
| 永続化されるプロファイルと利用中の切り替え | [プロファイル](../profiles/profiles.md) |
| 外部変更の検出・デバウンス・検証の付け方（変更検出・検証） | [変更と検証](../basic-usage/changes-and-validation.md) |

## スキーマと移行

| やりたいこと | 記事 |
| --- | --- |
| JSON Schema の出力とテストのダブル | [JSON Schema とテスト](../advanced/json-schema-and-testing.md) |
| 版を上げて古い形を読み替える（スキーマ移行） | [スキーマ移行](../migration/schema-migration.md) |
| 保存場所の引っ越しと旧ソースの退役（保存場所移行） | [保存場所移行](../migration/storage-migration.md) |
| 既存の `Configuration.Writable` ファイルを取り込む | [Configuration.Writable の取り込み](../migration/adopting-configuration-writable.md) |

## 運用と診断

| やりたいこと | 記事 |
| --- | --- |
| バックアップ世代・復元・ログ記録・出どころ説明と診断 | [バックアップ・ログ・診断](../advanced/backups-and-observability.md) |
| トリミング安全・NativeAOT で動かす | [NativeAOT](../advanced/native-aot.md) |

仕組みから理解したいときは [設計の全体像](../design/overview.md) をどうぞ。
