using SharpYaml.Serialization;

namespace Configlue.Tests;

[YamlSerializable(typeof(AppSettings))]
[YamlSerializable(typeof(AppSettings.Fragment))]
[YamlSerializable(typeof(DatabaseSettings.Fragment))]
[YamlSerializable(typeof(Dictionary<string, object?>))]
[YamlSerializable(typeof(IReadOnlyList<string>))]
[YamlSerializable(typeof(List<string>))]
[YamlSerializable(typeof(int))]
[YamlSerializable(typeof(string))]
internal partial class AppSettingsYamlContext : YamlSerializerContext { }
