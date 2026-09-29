---
title: Subject ごとの state
description: 1 つの model runtime でサーバー共通と subject ごとの state を扱い、logical key と物理 route を分離する。
---

1 つの Configlue model で、サーバー共通の設定と複数の user、tenant、その他アプリケーション固有 subject の設定を扱えます。model の source topology は固定のまま、各操作で subject を渡し、source ごとに異なる key と物理 route を選びます。

## Subject を定義する

アプリケーション側で `IConfiglueSubject` を実装します。`SubjectKey` は logical identity を表す opaque かつ canonical な値です。region や data residency などの配置ポリシーは subject の別の metadata として保持してください。

```csharp
public sealed record TenantSubject(
    string TenantId,
    string UserId,
    string Region,
    bool DataStrict) : IConfiglueSubject
{
    public SubjectKey Key =>
        SubjectKey.FromSegments("tenant", TenantId, "user", UserId);
}
```

`SubjectKey.FromSegments` は segment を曖昧さなく encode し、Unicode を正規化します。サーバー共通 state には `SubjectKey.Default` を使います。database 名、region、bucket 名など配置情報を logical key に含めないでください。

## 固定の優先順位と source ごとの key 選択

Source priority は model 登録時の metadata で、subject が変わっても同じです。source は subject から個別の key を選べます。user source は完全な key、tenant source は tenant 部分、server source は常に default key を使えます。

```csharp
model.Sources(sources =>
{
    sources.Add("server", serverReader, priority: 100)
        .KeyBy<TenantSubject>(_ => SubjectKey.Default);
    sources.Add("tenant", tenantReader, priority: 200)
        .KeyBy<TenantSubject>(subject =>
            SubjectKey.FromSegments("tenant", subject.TenantId));
    sources.Add("user", userReader, priority: 300)
        .KeyBy<TenantSubject>(subject => subject.Key);
});
```

runtime は subject に対して priority 順に source を解決します。subject 固有の値がなければ、tenant、server の順で残りの項目を補完できます。user ごとに source set を作成・登録する必要はありません。

## Logical key と物理 route を分ける

`model.Routing<TSubject>` でアプリケーション metadata から opaque な `RouteKey` を決めます。subject key は logical value を識別し、provider は route から database、Redis endpoint、region 別 client、bucket などの配置先を解決します。

```csharp
model.Routing<TenantSubject>(subject =>
    subject.DataStrict
        ? RouteKey.From(subject.Region)
        : RouteKey.Default);
```

Resource が受け取る `ConfiglueResourceContext` にはアプリケーション subject、source 固有の `Key`、物理的な `Route` が入ります。resource selector は key と保存先を独立して選べます。resource identity に対応する logical key と route が含まれ、別の物理配置への書き込みが同じ resource として調整されることはありません。

例えば、[PostgreSQL](../reference/postgresql-resource.md) は route ごとに共有する `NpgsqlDataSource` を解決し、source namespace と subject key ごとに row を保存します。[Redis](../reference/redis-resource.md) は route ごとに共有する multiplexer を解決し、subject row を provider 生成の hash key に保存します。[S3](../reference/s3-object-resource.md) は resource context から object key、bucket、client を選択できます。[HTTP / ZIP resource](../sources/http-and-zip.md) も context に応じて endpoint や entry を選べます。固定 file path は path 対応 resource を別途設定しない限り global です。

## current subject と明示 subject の view

request scope や circuit scope の consumer には、model 登録時に `PerSubject<TAccessor>()` を指定します。通常の `IReadOnlyState<T>` / `IWritableState<T>` view は各操作で現在の subject を解決します。accessor は model ごとに選べるため、1 つの model は tenant accessor、別の model はサーバー共通にできます。

```csharp
services.AddScoped<CurrentTenantAccessor>();
services.AddConfiglue(conf =>
{
    conf.Add<UserSettings>(model =>
    {
        model.PerSubject<CurrentTenantAccessor>();
        model.Sources(sources => ConfigureUserSources(sources));
    });

    conf.Add<ServerSettings>(model =>
        model.Sources(sources => ConfigureServerSources(sources)));
});
```

アプリケーション独自 accessor は `IConfiglueSubjectAccessor<TenantSubject>` を実装します。background job、管理画面、現在の request 以外の subject を扱う処理では `ISubjectState<T>` を受け取り、`ForSubject(subject)` を呼びます。戻り値の `IWritableState<T>` view はその subject に固定されます。どちらの view も同じ model runtime と source topology を使います。

scope の途中で subject が変わる可能性があれば accessor は `IConfiglueSubjectChangeSource` を実装できます。通知を受けた active change subscription は subject を再解決し、旧 watch 先を解除して新しい subject と route に接続し直し、実効値を読み込んで listener に通知します。変更通知を実装しない場合、subscription は最初に解決した subject を監視し続けます。

## ASP.NET Core と Blazor Server

任意の `Configlue.Resource.Http.AspNetCore` package には HTTP request と Blazor authentication context 用の adapter があります。subject 型はアプリケーション側で定義し、どの claim や service から key を決めるかもアプリケーションが選びます。

```csharp
services.AddHttpContextConfiglueSubjectAccessor<TenantSubject>(
    context => ResolveTenantSubject(context.User));

services.AddBlazorAuthenticationConfiglueSubjectAccessor<TenantSubject>(
    (principal, _) => ValueTask.FromResult(ResolveTenantSubject(principal)));

// 対応する model に指定する:
model.PerSubject<HttpContextConfiglueSubjectAccessor<TenantSubject>>();
// または BlazorAuthenticationConfiglueSubjectAccessor<TenantSubject>
```

HTTP adapter は各 scoped operation で request state を解決します。Blazor adapter は authentication state の変更を監視し、active な `OnChange` subscription を新しい subject に追従させます。Core は ASP.NET Core、Blazor、claims に依存しません。

## 共有 change watch

Watcher は invalidation signal です。通知後、Configlue は authoritative state を改めて読みます。resource provider は subject ごとに接続を開かず、物理 backend または route ごとに watch 基盤を共有します。PostgreSQL は data source ごとに listener を共有し、Redis は multiplexer と channel ごとに Pub/Sub subscription を共有します。remote notification のない provider は polling watcher を共有できます。current subject が変わると listener は新しい route に接続し直します。

model 登録は[アプリケーション構成](../basic-usage/app-setup.md)、`OnChange` の lifetime は[リアクティブ連携](./reactive-integration.md)を参照してください。
