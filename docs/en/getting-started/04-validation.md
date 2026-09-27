---
title: "STEP 4: Add validation"
description: Reject bad values with DataAnnotations and custom validators.
---

import { Tabs, TabItem } from '@astrojs/starlight/components';

# STEP 4: Add validation

Once files split, the next fear is "weird values": an out-of-range port, a required field left empty. Configlue validates model updates before saving them.

## Declare with DataAnnotations

Attributes are the quickest route:

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

DataAnnotations validation is enabled by default, so the registration needs no extra setting:

<Tabs syncKey="di">
<TabItem label="Without DI">

```csharp
conf.Add<AppSettings>(model =>
{
    model.Sources(sources => sources.FromJsonFile(new()
    {
        Id = "settings",
        Path = "settings.json",
    }));
    model.WriteRoute = StateWriteRoute.To("settings");
});
```

</TabItem>
<TabItem label="With DI">

```csharp
builder.Services.AddConfiglue(conf =>
{
    conf.Add<AppSettings>(model =>
    {
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

## Add a custom validator

Rules that attributes cannot express go into an `IConfiglueValidator<T>`:

Add the validator to the existing model registration:

<Tabs syncKey="di">
<TabItem label="Without DI">

```csharp
model.AddValidator(new AppSettingsValidator());
```

</TabItem>
<TabItem label="With DI">

```csharp
builder.Services.AddSingleton<IConfiglueValidator<AppSettings>, AppSettingsValidator>();
```

</TabItem>
</Tabs>

The validator implements `IConfiglueValidator<T>` and returns failures for invalid values:

```csharp
public sealed class AppSettingsValidator : IConfiglueValidator<AppSettings>
{
    public IReadOnlyList<string> Validate(AppSettings value) =>
        value.Port is >= 1 and <= 65535 && !string.IsNullOrWhiteSpace(value.Name)
            ? []
            : ["Name is required and Port must be between 1 and 65535."];
}
```

See [changes and validation](../basic-usage/changes-and-validation.md) for more options.

## When validation runs

It guards both reads and saves. A save that breaks the rules fails before anything reaches the file; a broken external file is caught at read time. Hearing about it early beats silently running on bad values.

Next: [STEP 5: Support environment variables](./05-environment.md). Learn how read-only sources behave.
