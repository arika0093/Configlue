---
title: "設計: Options"
description: 読み書きの窓口。プロファイル・動的オプション・DI適合。
---

# 設計: Options（窓口）

Options はアプリから見える窓口です。Source や Fragment の詳細は隠し、読み・保存・監視・説明・診断だけを見せます。

## 読みと書きの窓口

- `IReadOnlyOptions<T>`: 同期読みの `CurrentValue`、非同期の読み（`GetValueAsync` / `ReadAsync`）、`OnChange`、`ExplainAsync`、`GetDiagnostics`。`CurrentValue` は非同期 source の完了までブロックするため、非同期処理では `GetValueAsync` を使います。
- `IWritableOptions<T>`: 上に保存（`SaveAsync`・`BeginConfigureAsync`）、`ApplyPatchAsync` / `ApplyPatchesAsync`、ソース間・保存場所の移行を足します。

保存の前には全 Source のリビジョンベクターを比べ、参加 Source が変わっていれば `StateConflictException` で止めます。読み取り専用に隠された値の変更もここで止まります。

## 名前付きの世界

- 動的オプション: `model.EnableDynamicOptions = true` で実行時に名前付き実体を増減できます。`GetOptionsRegistry<T>()` の `TryAdd` / `TryRemoveAsync` が入り口です。テナント別などの多文書運用に向いています。
- 永続プロファイル: `EnableProfiles` と `SourcesForOptions` で名前付きプロファイルを保存します。カタログ用 Source と選択中の名前を持ちます。取り除いても裏の実データは残ります。
- DI 適合: クラスモデル向けに `IOptions<T>` / `IOptionsSnapshot<T>` / `IOptionsMonitor<T>` のアダプターがあります。動的な名前解決は登録簿と `IOptionsMonitor` を通ります。

## 診断と記録

`GetDiagnostics()` はその Options 実体のソース構成のスナップショットを返します。ソース ID・優先度・読み書き監視の可否・物理出どころ・Resource 同一性・退役状態と、既定およびパス別の書き込み経路が分かります。設定値そのものはログに出ません。構造化ログにはモデル・オプション名・ソース ID などが付きます。

アプリ構成の全体は[アプリケーション構成](../basic-usage/app-setup.md)、プロファイルの運用は[プロファイル](../profiles/profiles.md)を見てください。
