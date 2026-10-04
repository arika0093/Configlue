using System.Runtime.CompilerServices;
using Configlue.CompilerServices;

namespace Configlue.Tests;

[NotInParallel]
public sealed class GeneratedRegistrationConcurrencyTests
{
    [Test]
    public void OperationsFirstAccessWaitsForConcurrentModelInitialization() =>
        VerifyStartup(
            typeof(OperationsStartupModel),
            static () =>
                ConfiglueModelOperations<
                    OperationsStartupModel,
                    OperationsStartupModel.Fragment
                >.Current
        );

    [Test]
    public void DescriptorFirstAccessWaitsForConcurrentModelInitialization() =>
        VerifyStartup(
            typeof(DescriptorStartupModel),
            static () => ConfiglueModelDescriptor<DescriptorStartupModel>.Current
        );

    private static void VerifyStartup(Type model, Func<object> getRegistration)
    {
        RegistrationStartupGate.Entered.Reset();
        RegistrationStartupGate.Release.Reset();
        Exception? initializationError = null;
        Exception? accessError = null;
        object? registration = null;
        var initializer = new Thread(() =>
        {
            try
            {
                RuntimeHelpers.RunClassConstructor(model.TypeHandle);
            }
            catch (Exception exception)
            {
                initializationError = exception;
            }
        })
        {
            IsBackground = true,
        };
        var accessor = new Thread(() =>
        {
            try
            {
                registration = getRegistration();
            }
            catch (Exception exception)
            {
                accessError = exception;
            }
        })
        {
            IsBackground = true,
        };

        initializer.Start();
        try
        {
            RegistrationStartupGate.Entered.Wait(TimeSpan.FromSeconds(10)).ShouldBeTrue();
            accessor.Start();
            // Keep initialization paused while the accessor enters the registry.
            // CLR initializer waits are not exposed through ThreadState.
            accessor.Join(TimeSpan.FromMilliseconds(100)).ShouldBeFalse();
        }
        finally
        {
            RegistrationStartupGate.Release.Set();
        }

        initializer.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        accessor.Join(TimeSpan.FromSeconds(10)).ShouldBeTrue();
        initializationError.ShouldBeNull();
        accessError.ShouldBeNull();
        registration.ShouldNotBeNull();
        getRegistration().ShouldBeSameAs(registration);
    }
}

internal static class RegistrationStartupGate
{
    internal static readonly ManualResetEventSlim Entered = new();
    internal static readonly ManualResetEventSlim Release = new();

    internal static bool Pause()
    {
        Entered.Set();
        if (!Release.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new TimeoutException("Model initialization was not released.");
        }
        return true;
    }
}

[ConfiglueModel("operations-startup-race")]
public partial class OperationsStartupModel
{
    internal static readonly bool InitializationStarted = RegistrationStartupGate.Pause();
    public int Value { get; set; }
}

[ConfiglueModel("descriptor-startup-race")]
public partial class DescriptorStartupModel
{
    internal static readonly bool InitializationStarted = RegistrationStartupGate.Pause();
    public int Value { get; set; }
}
