---
title: ブラウザー WebStorage
description: Blazor から localStorage / sessionStorage に状態を保存する方法。スコープ付き lifetime と prerender 時の意味を明示します。
---

`Configlue.Resource.WebStorage` は 1 つのシリアル化値をブラウザーの `localStorage` または `sessionStorage` に保存します。Blazor Server では circuit スコープの `IJSRuntime` を通じて動作し、Blazor WebAssembly / Hybrid では JavaScript runtime がアプリケーション lifetime のときにそのまま動作します。

## storage source を登録する

モデル単位ヘルパーは source を登録し、書き込み先に設定します:

```csharp
using Configlue.Resource.WebStorage;

services.AddConfiglue(builder =>
{
    builder.Add<UiPreferences>(model =>
    {
        model.UseLocalStorage("ui-preferences");
    });

    // タブ単位の領域:
    builder.Add<DraftState>(model =>
    {
        model.UseSessionStorage("draft");
    });
});
```

合成や独自 codec には低レベル API を使います:

```csharp
model.Sources(sources =>
{
    sources.FromLocalStorage("ui-preferences")
        .Named("ui")
        .Priority(10);
    sources.FromSessionStorage("ui-preferences-session");
});
```

`UseLocalStorage` / `UseSessionStorage` は既定で JSON fragment codec を使います。別の codec を使う場合は options 経由で `Codec` を指定してください。

## スコープ付き runtime

browser storage source はスコープ付き `IJSRuntime` を利用するため、scoped runtime 要件を宣言します。Configlue はそのモデルの runtime 全体を DI スコープごとに生成・破棄し、共有 runtime にしません。Blazor Server では circuit ごとに固有の runtime と `IJSRuntime` を持つため、共有 runtime を経由して circuit 間で state が漏れることはありません。

これは subject バインディングとは独立です。`PerSubject` の accessor は current-subject ビューだけをスコープ化し、共有可能な source を使うモデルは per-subject でも共有 runtime を保ちます。[subject スコープ状態](../advanced/subject-scoped-state.md)を参照してください。

## Blazor Server での挙動

- **prerender。** 対話的レンダリング開始前は JavaScript を利用できません。読み取りは `Unavailable` を返し、書き込みは `WebStorageUnavailableException` を投げます。Configlue が偽のサーバー側ストアに置き換えることはありません。
- **切断された circuit。** circuit 切断後は `IJSRuntime` が例外を投げます。同じ availability の意味に従うため、呼び出し側はハングせずフォールバックまたは保存のスキップを選べます。
- **分離。** runtime と storage resource は circuit スコープに所有されるため、circuit 間・ユーザー間で state は漏れません。

## subject ごとの key

per-subject モデルでは storage key は既定で `{key}:{subjectKey}` となり、subject ごとに別エントリを使います。別のマッピングが必要なら `KeySelector` を指定します。

## 書き込みとリビジョン

値は payload と revision を持つ小さなエンベロープとして保存されます。条件付き書き込みは JavaScript 経由で現在の revision を読み、不一致なら `StateConflictException` を投げます。これは best-effort な楽観的並行性です。ブラウザー storage は origin 単位でシングルスレッドですが、トランザクションなバックエンドではありません。

## セキュリティ

ブラウザー storage はクライアントが制御します。権威ある認可・セキュリティ状態として扱わず、`localStorage` から来たという理由で値を信頼しないでください。キーがサーバー側に留まる場合、Configlue transformer は機密性・完全性を保護できますが、クライアント側/WASM 暗号化は鍵素材もクライアントが利用できるなら信頼の根拠にはなりません。
