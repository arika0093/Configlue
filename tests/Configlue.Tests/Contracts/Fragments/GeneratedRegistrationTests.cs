using Configlue.CompilerServices;
using Configlue.Provider.Json;

namespace Configlue.Tests;

[ConfiglueModel("registration.contract-settings", Version = 1)]
public partial class RegistrationContractSettings
{
    public string Name { get; set; } = string.Empty;

    public int Count { get; set; }
}

public sealed class GeneratedRegistrationTests
{
    [Test]
    public void DescriptorRegistration_IsAvailableOnFirstUseWithoutModuleInitializer()
    {
        var descriptor = ConfiglueModelDescriptor<RegistrationContractSettings>.Current;

        (descriptor).ShouldNotBeNull();
        (descriptor.Schema.Id).ShouldBe("registration.contract-settings");
        (ConfiglueModelDescriptor<RegistrationContractSettings>.Current).ShouldBeSameAs(descriptor);
    }

    [Test]
    public void JsonConverterRegistration_IsAvailableAfterDescriptorInitialization()
    {
        _ = ConfiglueModelDescriptor<RegistrationContractSettings>.Current;

        var registered =
            ConfiglueJsonFragmentRegistry<RegistrationContractSettings.Fragment>.TryGetConverter(
                out var converter
            );

        (registered).ShouldBeTrue();
        (converter).ShouldNotBeNull();
    }
}
