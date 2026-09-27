---
title: "STEP 4: バリデーションを付ける"
description: DataAnnotationsと独自バリデーターで間違った値を止める。
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 4: バリデーションを付ける

ファイルが分かれると、次に怖くなるのは「変な値」です。ポート番号が範囲外、必須項目が空。Configlue では保存と読み取りの手前で検証できます。

## DataAnnotations で宣言する

いちばん手軽なのは属性です。

```csharp
using System.ComponentModel.DataAnnotations;

[ConfiglueModel("tutorial.settings", Version = 1)]
public partial class AppSettings
{
    [Range(1, 65535)]
    public int Port { get; set; } = 8080;

    [Required, MinLength(1)]
    public string Name { get; set; } = "ExampleApp";
}
```

登録で検証を有効にします。

<Tabs syncKey="di">
<TabItem label="DI なし">

```csharp
conf.Add<AppSettings>(model =>
{
    model.ValidateDataAnnotations = true;
    model.Sources(sources => sources.FromJsonFile(new()
    {
        Id = "settings",
        Path = "settings.json",
    }));
    model.WriteRoute = StateWriteRoute.To("settings");
});
```

</TabItem>
<TabItem label="DI あり">

```csharp
builder.Services.AddConfiglue(conf =>
{
    conf.Add<AppSettings>(model =>
    {
        model.ValidateDataAnnotations = true;
        model.Sources(sources => sources.FromJsonFile(new()
        {
            Id = "settings",
            Path = "settings.json",
        }));
        model.WriteRoute = StateWriteRoute.To("settings");
    });
});
```

</TabItem>
</Tabs>

## 独自バリデーターを足す

属性では表せない規則は `IConfiglueValidator<T>` で足します。

前の登録に検証器を追加します。

<Tabs syncKey="di">
<TabItem label="DI なし">

```csharp
model.AddValidator(new AppSettingsValidator());
```

</TabItem>
<TabItem label="DI あり">

```csharp
builder.Services.AddSingleton<IConfiglueValidator<AppSettings>, AppSettingsValidator>();
```

</TabItem>
</Tabs>

検証器は `IConfiglueValidator<T>` を実装し、不正値の理由を返します。

```csharp
public sealed class AppSettingsValidator : IConfiglueValidator<AppSettings>
{
    public IReadOnlyList<string> Validate(AppSettings value) =>
        value.Port is >= 1 and <= 65535 && !string.IsNullOrWhiteSpace(value.Name)
            ? []
            : ["Name は必須で、Port は 1〜65535 の範囲です。"];
}
```

詳しくは[変更と検証](../basic-usage/changes-and-validation.md)を参照してください。

## いつ検証されるか

検証は読み取りと保存の両方で効きます。保存しようとした値が規則に反すれば、ファイルに書かれる前に失敗します。外部ファイルが壊れていた場合も、読み取り時に気づけます。壊れた値を黙って使い続けるより、早めに知る方が直しやすいはずです。

次: [STEP 5: 環境変数に対応する](./05-environment.md)。読み取り専用ソースの扱いを覚えます。
