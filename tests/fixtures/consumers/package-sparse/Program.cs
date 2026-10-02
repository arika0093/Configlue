using SparseFragments;

var original = Settings.Fragment.From(
    new Settings
    {
        Label = "original",
        Child = new Child { Count = 7, Host = "keep" },
    }
);
var patch = new Settings.Patch { Label = (string?)null };
patch.Child.Count = 9;
var result = original.Apply(patch);
Require(result.Label.IsPresent && result.Label.Value is null, "present null");
Require(result.Child.Value!.Count.Value == 9, "typed nested Set");
Require(result.Child.Value.Host.Value == "keep", "unchanged child member");
Require(original.Child.Value!.Count.Value == 7, "original fragment isolation");

patch = new Settings.Patch();
patch.Child.Unset();
Require(!original.Apply(patch).Child.IsPresent, "nested Unset");
patch = new Settings.Patch();
patch.Child.SetNull();
result = original.Apply(patch);
Require(result.Child.IsPresent && result.Child.Value is null, "nested SetNull");

var builder = original.ToBuilder();
builder.Label = Optional<string?>.Missing;
Require(!builder.Build().Label.IsPresent, "typed builder presence");
var clone = original.ToModel().DeepClone();
clone.Child!.Count = 42;
Require(original.ToModel().Child!.Count == 7, "structural deep clone isolation");
Console.WriteLine("SparseFragments packed consumer passed.");

static void Require(bool condition, string capability)
{
    if (!condition)
        throw new InvalidOperationException("Failed: " + capability);
}

[SparseFragmentModel]
public partial class Settings
{
    public string? Label { get; set; }
    public Child? Child { get; set; }
}

public class Child
{
    public int Count { get; set; }
    public string Host { get; set; } = "localhost";
}
