---
title: クイックスタート
description: DI なしで重ね合わせ設定の読み書きを5分で試す。
---

# クイックスタート

このガイドでは DI コンテナーなしで、JSON ファイルから設定を読み、プロセス環境変数を上に重ね、編集を保存します。

## 1. モデルを宣言する

```csharp
using Configlue;

[ConfiglueModel("example.quick-settings", Version = 1)]
public partial class QuickSettings
{
    public string Name { get; set; } = "World";
    public int RunCount { get; set; }
}
```

## 2. 2つのソースでコンテキストを作る

複数ソースに存在する項目は `Priority` が高い方が勝つため、ここでは環境変数がファイルを上書きします。ファイルがない場合ファイルソースはフォールスルーするので、初回はモデルの既定値から始まります。

```csharp
using Configlue;
using Configlue.Provider.Json;
using Configlue.Source.Environment;

await using var context = ConfiglueApp.CreateContext(conf =>
{
    conf.Add<QuickSettings>(model =>
    {
        model.Sources(sources =>
        {
            sources.FromJsonFile(new()
            {
                Id = "settings",
                Path = "quicksettings.json",
                Priority = 100,
            });
            sources.Add(EnvironmentStateSource.FromEnvironment<QuickSettings, QuickSettings.Fragment>(
                "environment", "QUICK"));
        });
        model.WriteRoute = StateWriteRoute.To("settings");
    });
});

var options = context.GetOptions<QuickSettings>();
```

接頭辞 `QUICK` の場合、変数 `QUICK__NAME` が `Name` を上書きします。メンバー名は大文字小文字を区別せず、`__` で入れ子を区切ります。

## 3. 読み・監視・保存

```csharp
var current = await options.GetValueAsync();
Console.WriteLine($"Hello, {current.Name}! (run #{current.RunCount})");

using var subscription = options.OnChange(updated =>
    Console.WriteLine($"Settings changed: {updated.Name}"));

// 疎編集: Name だけ書き込まれ、RunCount はソースの値を保つ。
await options.SaveAsync(settings =>
{
    settings.Name = "Ada";
    settings.RunCount++;
});
```

実行後に `quicksettings.json` を開くと、このソースが持つ項目だけが入っています:

```json
{
  "$configlue": { "id": "example.quick-settings", "version": 1 },
  "$value": {
    "Name": "Ada",
    "RunCount": 1
  }
}
```

## 4. 値の出どころを見る

```csharp
var explanation = await options.ExplainAsync("Name");
Console.WriteLine(explanation);
```

説明には実効値と、優先度順の各ソース寄与が並びます。

## 次のステップ

* DI を使う場合は同じ `conf.Add<T>(...)` 定義を `services.AddConfiglue(...)` の中に入れます — [アプリケーション構成](../basic-usage/app-setup.md) 参照。
* 定番の層を手で組むのが面倒なら [共通レイヤーソース](../basic-usage/common-sources.md) 参照。
* 通しのサンプルは [サンプル集](./examples.md) で動かせます。
