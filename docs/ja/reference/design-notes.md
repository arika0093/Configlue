---
title: 設計メモ
description: プロジェクト境界、実装状況、既知の制限。
---

# 設計メモ

Configlue は Configuration.Writable 向けに検討されたソース&フラグメントの方向に沿います: バックエンド中立な状態、疎な生成フラグメント、論理スキーマと物理保存構成の分離です。

## 境界

* **ソース**は論理的な構成スナップショットを提供し、読み・書き・監視の機能を独立に公開できます。
* **リソース**はファイル・ZIP エントリ・HTTP 応答などの物理端点を表します。
* **コーデック**はリソース I/O なしにバイトと型つき値を相互変換します。
* 生成された**フラグメント**は各モデル項目の有無を保持し、存在する `null` や既定値も区別します。
* 解決・移行・投影・書き込み計画はフラグメント上で動き、アプリコードは通常のモデル値を編集します。

複数ソースが1つのモデル部分木に寄与でき、複数束縛が1つのリソースを共有できます。各論理ソースはその物理リソースの `ResourceId` を公開できます。セクションビュー・ZIP エントリビュー・投影ソースはこの同一性を保つため、後の書き込み調整で保存場所を共有する論理更新をまとめられます。共有リソースをバッチ化できないバックエンドはどのグループ書き込みよりも先に失敗し、範囲の重なりも計画時に失敗します。

## プロジェクト構成

プロジェクトは `src/` 直下にあります。`Configlue` は Core・DI 統合・JSON プロバイダー・JSON Schema 出力・HTTP リソース・共通レイヤーソース・環境変数ソース・ソースジェネレーターアナライザーを束ねるアセンブリなしメタパッケージです。`Configlue.Abstraction` が契約、`Configlue.Core` が解決・永続化ランタイム (汎用ファイルリソース含む) を持ちます。`Configlue.Extensions.DI` に DI 登録があり、任意の `Configlue.Extensions.MSOptions` に Microsoft options アダプターがあります。`Configlue.Generator` が疎フラグメントとパッチを生成します。`Configlue.Provider.Json`・`.Xml`・`.Yaml` に形式コーデック・セクションリソース・ファイル登録があり、`Configlue.JsonSchema` がモデルから JSON Schema を生成します。`Configlue.Source.Environment` はプロセス変数をフラグメントに写像し、`Configlue.Source.CommandLine` は `System.CommandLine` パース結果を写像し、`Configlue.Source.Presets` は形式 Provider に依存せず定番の重ね合わせプリセットを合成します。共通プリセットと XML/YAML ファイル層の統合は、それぞれ任意の `Configlue.Source.Presets.Xml`・`.Yaml` アダプターが提供します。`Configlue.Resource.Http` は ETag リビジョンでリソースバイトを運び、`Configlue.Resource.Dapr` は Core に SDK 依存を追加せず任意の Dapr State Management store を接続し、`Configlue.Resource.S3` は Core に AWS SDK 依存を追加せず Amazon S3 object の byte と ETag revision を対応づけます。`Configlue.Resource.Http.AspNetCore` が HTTP resource を配信し、`Configlue.Resource.Zip` がアーカイブ内エントリを公開します。`Configlue.Testing` はインメモリダブルです。

## 実装状況

基盤は揃っています: バックエンド中立な読み/書き/監視契約、優先度つきソース解決、ソース契約の移行と投影、項目単位の出どころ説明、ソース構成の診断スナップショット、設定値を含まない任意の構造化ログ、JSON/XML/YAML セクションリソース、ZIP エントリリソース、選択ソース間の移行、宛先単位の投影と検証つきの再試行可能な複数→複数保存場所移行、検証済み論理ソース退役、リソース単位に束ねる明示的ソースローカル複数書き込みパッチ、バックアップ世代と復元つきのリビジョン認識ファイルリソース、マージ・意味的差分・パッチ操作つきの生成疎フラグメント、JSON/XML/YAML コーデック、JSON Schema 出力、ソース単位スキーマ移行連鎖、設定可能な保存検証、キー付き DI プロファイル、実行時プロファイル/オプション登録簿、永続化される名前付きプロファイルカタログ、Microsoft options アダプター、デバウンスつき変更通知、非同期読み・保存・リビジョンベクター検査つき編集セッションの登録。

## 既知の制限

* 異なるリソース間の書き込みはアトミックではありません。
* ソース退役は現行オプション実体の範囲で裏データを残します。将来の起動向けにアプリ登録の更新が必要です。
* オプション実体の source set は固定です。動的オプションと永続プロファイルは、それぞれ独自の source set を持つ実体全体を作成/削除できます。アプリの構成を変更する場合は、新しい context を構築してアプリ側で切り替えます。検証済み移行では現行実体から source を退役できます。
* `FileStateStorageMigrationJournal` は migration ID ごとに実行全体のプロセス間 lease を保持します。`IStateStorageMigrationLeaseProvider` を実装しない独自 journal は、呼び出し側で同時実行を調整してください。
* watcher は無効化シグナルを通知します。ポーリング・再試行・再接続の方針は provider 側の責務です。

## source 構成を実行時に変える場合の判断

既存 options identity の source set をその場で置き換える API は、現時点では提供しません。動的オプションと `SourcesForOptions` で名前ごとに独立構成でき、アプリ全体の変更には新しい context への切り替えを使えます。同じ名前の実体を hot-swap して live handle を維持する要件は確認できていないため、現行 API の契約変更は保留します。

consumer を作り直さず、ひとつの options identity の source を変える具体的な要件が出た場合は、実体全体の遷移として実装します。candidate source set を公開前に構築・検証し、対象 identity のみに適用し、既返却 handle が旧実体で完了するのか置換先を追跡するのかを決めます。旧実体への新規操作を止め、実行中の操作と watcher の停止を待ち、ヘルパー生成リソースだけを破棄します。registry 通知と構成診断も同じ遷移として反映します。旧実体で始めた編集が旧/新の source set を混在して書かないよう revision を確認します。データ移動は暗黙に行わず、選択した source の寄与を明示的に移行し、宛先を検証してから旧 source を退役させます。共有リソースの `ResourceId` による書き込み集約と範囲重複検査も遷移中に保ちます。

具体的な要件が出るまでは、新しい context を構築し、必要なら明示的に移行して、アプリの consumer を切り替えてから旧 context を破棄します。現在の名前単位のライフサイクルは[動的オプション](../profiles/dynamic-options.md)を参照してください。

現行 API は、Configuration.Writable の機能完全な置換というより土台のアーキテクチャです。
