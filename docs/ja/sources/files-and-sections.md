---
title: ファイル・形式・セクション
description: JSON・YAML・XML の形式とファイルソース、入れ子セクションビュー。
---

# ファイル・形式・セクション

プロバイダーパッケージは共有の `Sources` ビルダーに一発登録を追加します。JSON・YAML・XML ファイルは同じオプション形状です。

```csharp
using Configlue.Provider.Json;
using Configlue.Provider.Yaml;

model.Sources(sources =>
{
    sources.FromJsonFile(new()
    {
        Path = "settings.json",
        SectionPath = "Application:User",
        Priority = 100,
    });
    sources.FromYamlFile(new()
    {
        Path = "defaults.yaml",
        Priority = 10,
        ReadOnly = true,
    });
});
```

ファイルソースは正規化したリソースとセクションから安定した不透明 ID を受け取ります。高度な移行や診断で安定 ID が必要な場合のみ `Id` を指定してください。

`FromXmlFile(new() { ... })` も XML ファイルと任意の要素パスに同じオプションを使います。ファイルヘルパーは非 DI・DI の両方で動き、生成されたファイルリソースはコンテキスト所有でウォッチャー停止後に破棄されます。直接渡したクライアントは呼び出し側所有のままです。

## 解決ルール

* 読み取りは各ソースの存在項目をマージし、項目ごとに `Priority` が高い方が勝ちます。
* ファイルがない場合ファイルソースはフォールスルーし、それ以外の読み取り失敗は伝播します。
* `ReadOnly = true` のソースは書き込み計画から除外されます。

## セクションリソース

`JsonSectionResource` は `App:Settings` のような入れ子 JSON パスを独立リソースとして公開し、書き込み時に兄弟値を保持します。`XmlSectionResource`・`YamlSectionResource` は XML 要素・YAML マッピングに同じ入れ子セクションビューを提供し、兄弟保持とリソース全体のリビジョンチェックを含みます。

1つのリソースを共有する互いに重ならないセクションマウントは1回の物理書き込みにまとめられます。分割ファイルにはファイルごとに1つの `FileResource` を使い、各ソースを論理モデルパスにマウントします。各ファイルは独立に書き込まれます。セクションリソースは物理 `ResourceId` を保ちつつ論理ソース ID と優先度は別に持ちます。

JSON ファイルソースは `.json` と `.jsonc` のどちらでもコメント・末尾カンマを含む内容を読み書きします。JSON と YAML のファイル書き込みは対象値だけを更新し、無関係なコメント・空白・引用・スカラースタイルを保ちます。モデル項目の Unset はその項目を削除し、未知の項目は維持します。不正 UTF-8 の YAML 入力は置換デコードされず失敗します。この構造編集は JSON/YAML ファイル登録で提供し、それ以外の Codec は従来の全置換動作を維持します。

YAML のメンバー名付けには命名ポリシーと生成フラグメントスキーマをコーデックに渡します:

```csharp
new YamlStateCodec<SampleSetting.Fragment>(JsonNamingPolicy.CamelCase, SampleSetting.FragmentSchema)
```

## 次のステップ

* [環境変数とコマンドライン](./environment-and-commandline.md)。
* 入れ子モデルへのセクション合成は [マウントと投影](../layering/mount-and-project.md)。
