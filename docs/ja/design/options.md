---
title: "設計: Options"
description: 読み書きの窓口。プロファイル・動的オプション・DI適合。
---

Options はアプリから見える窓口です。通常の読み書きを小さく保ち、高度な操作は目的別のインターフェースで提供します。

## 読みと書きの窓口

- `IReadOnlyOptions<T>`: 非同期読み（`GetValueAsync`）と `OnChange`。
- `IWritableOptions<T>`: Patch による保存を追加します。
- `IConfiglueInspection<T>`: 状態と生成された Details の取得。
- `IConfiglueEditSessions<T>`: 編集セッションの開始。
- `IConfiglueSources<T>`: ソースローカルの保存・置換・バッチ・移行。
- `IConfiglueDiagnostics<T>`: 構成の診断と reload failure 通知。

Core に同期 `CurrentValue` プロパティはありません。Context の `GetInspection<T>()`・`GetEditSessions<T>()`・`GetSources<T>()`・`GetDiagnostics<T>()`、または対応する DI サービスを利用します。名前付き登録では keyed service が提供されます。Microsoft の同期 options interface への適合は opt-in の `Configlue.Extensions.MSOptions` package が提供します。

保存の前には全 Source のリビジョンベクターを比べ、参加 Source が変わっていれば `StateConflictException` で止めます。読み取り専用に隠された値の変更もここで止まります。

ファイル・HTTP ソースは `Id` を省略すると、正規化したリソース記述子から安定した不透明 ID を生成します。移行や外部の provenance 参照で ID の継続性が必要な場合のみ明示してください。

## 名前付きの世界

- 動的オプション: `model.EnableDynamicOptions = true` で実行時に名前付き実体を増減できます。`GetOptionsRegistry<T>()` の `TryAdd` / `TryRemoveAsync` が入り口です。テナント別などの多文書運用に向いています。
- 永続プロファイル: `EnableProfiles` と `ConfigureSources` で名前付きプロファイルを保存します。カタログ用 Source と選択中の名前を持ちます。取り除いても裏の実データは残ります。
- DI 適合: クラスモデル向けに `IOptions<T>` / `IOptionsSnapshot<T>` / `IOptionsMonitor<T>` のアダプターがあります。動的な名前解決は登録簿と `IOptionsMonitor` を通ります。

## 診断と記録

`GetDiagnostics()` はその Options 実体のソース構成のスナップショットを返します。ソース ID・優先度・読み書き監視の可否・物理出どころ・Resource 同一性・アクティブ状態と、既定およびパス別の書き込み経路が分かります。設定値そのものはログに出ません。構造化ログにはモデル・オプション名・ソース ID などが付きます。

アプリ構成の全体は[アプリケーション構成](../basic-usage/app-setup.md)、プロファイルの運用は[プロファイル](../profiles/profiles.md)を見てください。
