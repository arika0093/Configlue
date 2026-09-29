---
title: HTTP と ZIP
description: HTTP 越しのリモートポリシーソースとアーカイブ内エントリのリソース化。
---

## HTTP リソース

`HttpResourceReader` は `{root}/get` から読み、任意の状態コーデックと組み合わせられます。HTTP リクエストは条件つき書き込みとポーリング (既定5秒間隔) に ETag を使います。

同じ reader と endpoint を共有する複数 subject の watcher は、1 つのポーリングループを共有します。物理リソースの変更時に各 waiter が起床し、それぞれ authoritative state を読み直します。1 つの waiter をキャンセルしても他の waiter がある間はループを維持し、最後の waiter が外れると停止します。

```csharp
using Configlue.Provider.Json;
using Configlue.Resource.Http;

var reader = new HttpResourceReader(httpClient, new Uri("https://config.example/api/settings/"));
model.Sources(sources => sources.FromHttp(new HttpSourceOptions
{
    Id = "policy",
    EndPoint = "https://config.example/api/settings/",
    Client = httpClient,
    Codec = new JsonStateCodec<AppSettings.Fragment>(),
    Priority = 400,
}));
```

HTTP 書き込みは `Writable = true` のときだけ有効で、コーデックの明示が必要です。DI では直接クライアントではなく `ClientFactory = provider => provider!.GetRequiredService<IHttpClientFactory>().CreateClient("settings")` を渡し、ハンドラー寿命をファクトリーに任せます。DI 外では渡したクライアントは呼び出し側所有のままです。

```csharp
sources.FromHttp(new HttpSourceOptions
{
    Id = "remote",
    EndPoint = "https://config.example/api/settings/",
    ClientFactory = provider => provider!.GetRequiredService<IHttpClientFactory>().CreateClient("settings"),
    Codec = new JsonStateCodec<AppSettings.Fragment>(),
    Priority = 400,
    Writable = true,
});
```

エンドポイントが更新に対応するときだけ `writer: reader.CreateWriter()` を `SerializedStateSource.FromResource` に渡します。任意パッケージ `Configlue.Resource.Http.AspNetCore` は同じプロトコルをユーザー提供のリソースハンドラー上にマップします — [HTTP リソースプロトコル](../reference/http-resource-protocol.md) 参照。読みの不達 (`404`) や一時的利用不可はフォールスルー/未検出意味にマップされ、恒久的エラーはアプリに伝わります。

ホストアプリは標準の `AddHttpClient` API で名前つきクライアントを登録し、`FromHttpClientFactory` でファサードソースに渡せます。JSON エンドポイントには `FromJsonHttp`・`FromJsonHttpClientFactory` が JSON コーデックを自動生成します。`Writable = true` はエンドポイントが更新対応のときだけ設定します。これらのソースは既定で読み取り専用です。クライアントは Configlue コンテキスト生成時に解決され、`IHttpClientFactory` が背後のハンドラーを管理し、ソースは返却クライアントを破棄しません。JSON 以外のコーデックには `FromHttp` を使います。

## ZIP エントリ

`ZipEntryResource` はアーカイブ内の1エントリを論理リソースとして公開しつつ、アーカイブの物理同一性とリビジョンを保ちます。互いに重ならないエントリ更新は1回のアーカイブ書き込みにまとめるか、別エントリへの同時更新に対して安全に再適用できます。同じエントリへの同時更新は `StateConflictException` となり、無関係のエントリは無傷です。

アーカイブリソースが `IStateWatcher` を実装する場合 (例: `FileResource`)、エントリの変更監視はその watcher に委譲され、アーカイブファイルの編集を検知します。watcher がないリソースだけリビジョンを既定250ms間隔でポーリングします。`ZipEntryResource` の `pollingInterval` でこのフォールバック間隔を変更できます。

## 複数モデルを1つのバイナリファイルに保存

`UseSingleBinary` はモデルと名前付きオプションを、それぞれ別の JSON エントリとして1つのローカル ZIP アーカイブに保存します。CLR型名の変更後も保存先を維持したい場合は、安定した `storageKey` を指定してください。

```csharp
using Configlue.Source.Presets;

await using var context = ConfiglueApp.CreateContext(config =>
    config.UseSingleBinary(binary =>
    {
        binary.WithLocal("savedata.bin")
            .WithPassphrase("秘密のパスフレーズ")
            .WithProfiles();
        binary.Add<AppSettings>(storageKey: "app");
        binary.Add<GameSettings>(storageKey: "game");
    }));
```

`WithAesKey` は16・24・32バイトのAES鍵を受け取ります。名前付きオプションやプロファイルのランタイムを作成できる間は、渡した鍵メモリを変更しないでください。`WithEncryption` は呼び出し側所有の `IStateByteTransformer` を受け取るため、コンテキストの利用中は破棄しないでください。`WithPassphrase` (別名 `WithEncrypted(string)`) は PBKDF2-HMAC-SHA-256 で鍵を導出し、AES-256-GCM で暗号化します。ランダムsaltはファイルに保存されます。鍵やパスフレーズはセーブファイルとは別に管理してください。

`WithProfiles` を使うと、モデルごとのプロファイルカタログも同じアーカイブに保存されます。プロファイル、通常の名前付きオプション、名前なしの既定状態は別エントリです。別エントリへの同時書き込みはマージされ、同じエントリへの古い状態からの書き込みは上書きせず競合として通知されます。

## 次のステップ

* [フォールバックと独自ソース](./fallback-and-custom.md)。
* 線上仕様は [HTTP リソースプロトコル](../reference/http-resource-protocol.md)。
