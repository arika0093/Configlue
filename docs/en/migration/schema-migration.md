---
title: Schema migration
description: Version models and migrate older fragments with generated support.
---

# Schema migration

Configuration files evolve. Adding or removing properties is straightforward — just change the class and give new properties default values. Most providers handle this without issues. Incompatible changes need a version step.

## Incompatible changes

Increment the version, keep the old shape under a new name, and declare it as a previous version. Then implement the migration method; the interface is provided automatically.

```csharp
// Version 2 (new)
[ConfiglueModel("UserSetting", Version = 2)]
public partial class UserSetting
{
    public string Name { get; set; } = "default name";
    public int Age { get; set; } = 20;
}

// Version 1 (old)
[ConfiglueModel("UserSetting", Version = 1)]
public partial class UserSettingV1
{
    public string FirstName { get; set; } = "first";
    public string LastName { get; set; } = "last";
    public int Age { get; set; } = 20;
}

[ConfigluePreviousVersion(typeof(UserSettingV1))]
public partial class UserSetting
{
    public UserSetting Migrate(UserSettingV1 source) => new()
    {
        // combine FirstName and LastName into Name;
        // make sure to copy other properties as well.
        Name = $"{source.FirstName} {source.LastName}",
        Age = source.Age,
    };
}
```

The library loads the file, checks the version, loads `Version = 1` as `UserSettingV1` and calls `Migrate`, or loads `Version = 2` directly. Saving always writes the latest version. Chains across many versions migrate sequentially.

For renamed or removed historical fields, declare earlier model types with `[ConfigluePreviousVersion(typeof(SettingsV1))]` on the current model and pass `Settings.CreateSchemaDispatcher(...)` to `SerializedStateSource.FromResource`; this decodes by schema metadata before current-fragment conversion and preserves sparse presence. Register `IStateSchemaMigration<TFragment>` implementations as services to migrate older fragments with the same generated shape.

## Presence-aware migration

The generated `FragmentBuilder` and `Patch` members are `ref` properties: `builder.Value = 123`, `builder.Value.Set(123)`, `builder.Value.Unset()`, and `builder.Value.CopyFrom(source.OldValue)`. Generated patches also implement `IConfiglueMemberPatch.SelectMembers(memberIds)`, which copies only the requested stable schema member IDs while preserving each operation's `Unchanged`, `Set`, or `Unset` state. This lets source routers split a patch without converting explicit null/default values or removals into ordinary fragment values. Custom `IConfigluePatch` implementations remain compatible; implement `IConfiglueMemberPatch` when a custom patch supports member-level routing. For a renamed member, assign explicitly with `builder.NewName.CopyFrom(previous.OldName)`; for a nested member whose generated child type changed, convert a present value, for example `builder.Database.CopyFrom(previous.Database, static value => value is null ? null : NewDatabase.Fragment.FromPrevious(value))`. `Fragment.FromPrevious` already recurses into a same-name nested child whose current generated model declares the previous child type, preserving Missing, present `null`, and present values, and `CreateSchemaDispatcher` uses the same migration by default.

## Next steps

* [Storage migration](./storage-migration.md).
* [Adopting Configuration.Writable](./adopting-configuration-writable.md).
