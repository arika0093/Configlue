---
title: マウントと投影
description: ソースフラグメントを入れ子モデルに束縛する。必要なら可逆に。
---

# マウントと投影

ソースフラグメントが生成された入れ子モデルに一致する場合は、`StateSourceProjection.Mount<TSubtreeFragment, TRootFragment>(source, "Policy")` でマウントするか、型つきセレクター `sources.AddMounted<TModel, TRootFragment, TSubtreeModel, TSubtreeFragment>(source, model => model.Policy)` で登録します。セレクターはメンバーパスと部分木のモデル/フラグメント型の両方をコンパイル時に検査します。パスを動的生成する場合は文字列オーバーロードを使い、登録時に生成スキーマと照合します。

```csharp
model.Sources(sources =>
{
    sources.FromJsonFile(new() { Id = "settings", Path = "settings.json", Priority = 10 });
    sources.AddMounted<AppSettings, AppSettings.Fragment, PolicySettings, PolicySettings.Fragment>(
        policyHttpSource,
        model => model.Policy,
        root => root.Policy.Value!);
});
```

JSON 全体ソースとマウントした HTTP ソースは `AppSettings.Policy` の別項目を分担でき、HTTP に無い項目は JSON にフォールスルーします。部分的なマウントフラグメントは存在項目だけ寄与します。マウントはソース ID・優先度・リビジョン・リソース同一性・物理出どころを保持します。`toSource` を渡さない限り読み取り専用で、渡すと疎ルート寄与をソースフラグメントに戻す写像になります。コールバックはソースフラグメントの部分的形状を保ってください。

異なるソース DTO には、まず `StateSourceProjection.Project` (ソーススキーマ移行を含め、書き込み可能にする場合は明示の逆投影つき) で入れ子モデルフラグメントに写像し、その投影ソースをマウントします。逆投影が投影外フィールド保持のために現行ソース契約を要する場合は `ProjectWithUpdate` を使います。コールバックは更新前後の投影値と現行ソース契約を受け取るため、unset と非投影を区別できます。現行認識つき書き込みはソースを再読・リビジョンチェックし、リソース対応時はバッチ変異を用意します。`Unset` 操作はソースの削除表現に写像するか、表現不能なら逆コールバックから例外にします。

`RetryCount` だけ投影し `Endpoint` などソース固有項目を残す HTTP DTO には `ProjectWithUpdate` で現行 DTO を更新して保持します。JSON セクションは `JsonSectionResource(resource, "App:Policy")` でマウントでき、そのリソースを共有する互いに重ならないセクションマウントは1回の物理書き込みにまとめられます。

ソース固有フラグメントを入れ子モデルフラグメントへ移行・マップする用途にも `StateSourceProjection.Project` を使います。書き込み可能にするには逆投影を付けます。

## 次のステップ

* [ファイルとセクション](../sources/files-and-sections.md)。
* 名前ごとのソース構築 `SourcesForOptions` は [プロファイル](../profiles/profiles.md)。
