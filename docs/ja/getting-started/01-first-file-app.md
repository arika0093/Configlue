---
title: "STEP 1: はじめてのファイル設定"
description: JSONファイルの読み書きと変更通知。いちばん小さなアプリを作る。
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 1: はじめてのファイル設定

このチュートリアルは順番に進める作りです。STEP 1 では、いちばん小さな形――JSON ファイルをひとつ使った読み書きと、外部変更の通知だけを扱います。DI を使うかどうかでタブを切り替えられます。選んだ表示はこの後の STEP でも引き継がれます。

## モデルを宣言する

設定の入れ物は、普通の C# クラスです。`[ConfiglueModel]` を付けて `partial` にする点だけが約束事です。Generator が差分管理用の `Fragment` / `Patch` を作ります。

```csharp
using Configlue;

[ConfiglueModel("tutorial.settings", Version = 1)]
public partial class AppSettings
{
    public string Name { get; set; } = "World";
    public int RunCount { get; set; }
}
```

最初の文字列はスキーマ ID です。アプリ内で一意なドット区切り名を付けてください。後で JSON Schema を出したり、版管理をしたりするときの目印になります。

## コンテキストを作る

<Tabs syncKey="di">
<TabItem label="DI なし">

```csharp
using Configlue;
using Configlue.Provider.Json;

await using var context = ConfiglueApp.CreateContext(conf =>
{
    conf.Add<AppSettings>(model =>
    {
        model.UseDefaultJsonFile();
    });
});

var options = context.GetOptions<AppSettings>();
```

</TabItem>
<TabItem label="DI あり">

```csharp
// Program.cs
using Configlue;
using Configlue.Provider.Json;

builder.Services.AddConfiglue(conf =>
{
    conf.Add<AppSettings>(model =>
    {
        model.UseDefaultJsonFile();
    });
});
```

```csharp
// 使う側
using Configlue;

public class Greeter(IWritableOptions<AppSettings> options)
{
    public async Task RunAsync()
    {
        var settings = await options.GetValueAsync();
        Console.WriteLine($"Hello, {settings.Name}!");
    }
}
```

</TabItem>
</Tabs>

`UseDefaultJsonFile()` は実行ファイルの隣に書き込み可能な `settings` source を登録し、既定の `usersettings.json` を使います。`UseJsonFile("settings.json")` は現在のディレクトリからの相対パスを使います。完全な `JsonFileSourceOptions` を渡せば細かく設定できます。複数 source を重ねる場合は `Sources(...FromJsonFile...)` と `WriteRoute` を直接使ってください。

## 読む・監視する・保存する

<Tabs syncKey="di">
<TabItem label="DI なし">

```csharp
var current = await options.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}! (run #{current.RunCount})");

// ファイルを外から書き換えると通知が来ます。
using var subscription = options.OnChange(updated =>
    Console.WriteLine($"変わりました: {updated.Name}"));

await options.SaveAsync(settings =>
{
    settings.Name = "Ada";
    settings.RunCount++;
});
```

</TabItem>
<TabItem label="DI あり">

```csharp
var current = await options.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}! (run #{current.RunCount})");

using var subscription = options.OnChange(updated =>
    Console.WriteLine($"変わりました: {updated.Name}"));

await options.SaveAsync(settings =>
{
    settings.Name = "Ada";
    settings.RunCount++;
});
```

読み書きのコードは DI の有無で変わりません。変わるのはコンテキストの作り方だけです。

</TabItem>
</Tabs>

`SaveAsync` に渡すラムダは「疎編集」です。触った項目だけが保存され、触っていない項目はソースの値を保ちます。保存後に `settings.json` を開くと、このソースが持つ項目だけが入っています。

```json
{
  "$configlue": { "id": "tutorial.settings", "version": 1 },
  "$value": {
    "Name": "Ada",
    "RunCount": 1
  }
}
```

ファイルが存在しない初回は、モデルの既定値から始まります。ファイルソースは「ないときは素通りする」ので、例外にはなりません。実行中にファイルを手で書き換えると、`OnChange` に通知が来ます（約 300ms のデバウンス付き）。

## つまずきどころ

- モデルに `partial` を付け忘れると Generator が動きません。ビルドエラーの内容で気づけます。
- `GetOptions` の前に登録が必要です。「登録してから取得」の順番だけ覚えてください。
- 保存先が分からなくなったら `WriteRoute` を見返します。書き込みの宛先は常に明示されています。

次: [STEP 2: 現実的なモデルに育てる](./02-real-world-model.md)。項目と入れ子を増やし、実世界の設定ファイルらしい形にします。
