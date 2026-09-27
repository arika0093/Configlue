---
title: 解決とマージ
description: 優先度読み、存在有無、マージ方式、出どころ説明。
---

# 解決とマージ

生成モデルオプションは優先度つき状態ソース集合で登録します。読み取りは各ソースの存在項目をマージし、書き込みは読み優先度と独立に宛先を選べます。

## 存在有無とマージ方式

生成メンバーは存在を明示します。`Fragment` は各メンバーを `Optional<T>` で公開し (`IsPresent` が「項目なし」と「`null`/既定値あり」を区別)、`ToBuilder()` は可変 `FragmentBuilder` を返し、`Fragment.FromPrevious` は宣言版間の同名・型互換メンバーをコピーします。

項目ごとの合成は `[ConfiglueMerge]` で制御します:

```csharp
[ConfiglueModel("app-settings", Version = 2)]
public partial class AppSettings
{
    public bool Enabled { get; set; } = true;

    [ConfiglueMerge(MergeMode.Append)]
    public IReadOnlyList<string> Plugins { get; set; } = [];
}
```

* `Replace` — 優先度上位が全体で勝つ。
* `Deep` — 入れ子メンバーを再帰マージ。
* `Append` — 順序付きコレクションを優先度の低い順につなぎ、重複を残す。set 型は重複と順序を保持できないため、generator がエラーにする。
* `SetUnion` — 低優先度から結合し、既定の等価性で最初に現れた要素を残す。配列/list は順序を保ち、set 型の列挙順は未規定。編集は各対象ソースのコレクション断片にリベースされる。

他ソース所有の値変更が必要な編集や、上位ソースに隠される編集は、黙って置換されず `StateConflictException` で失敗します。通常の編集はモデル型上の自然な C# のままです。生成メンバープロキシ (`builder.Value = 123`、`builder.Value.Set(123)`、`builder.Value.Unset()`、`builder.Value.CopyFrom(...)`) は上級のソースローカル表面です。

生成メンバー名、`Optional<T>`/`FragmentOperation<T>` 形状、存在意味論、`Fragment.FromPrevious`/`CreateSchemaDispatcher` の振る舞いが安定した公開契約です。生成ヘルパーコードの正確な配置は変わることがあります。

## 出どころ

`IConfiglueOptions<T>.ExplainAsync("Database.Host")` は実効値と、優先度順の各存在ソース寄与を返します。重ね合わせのトラブルシュートや設定 UI の「どこから来たか」表示に使います。

## 次のステップ

* [書き込み経路指定](./write-routing.md)。
* [マウントと投影](./mount-and-project.md)。
