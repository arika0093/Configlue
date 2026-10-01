using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using MessagePack.Formatters;

namespace Configlue.Provider.MessagePack;

/// <summary>Provides generated MessagePack formatters for generated fragments without reflection.</summary>
/// <remarks>
/// Generated model code registers its fragment formatter here from the model's own type initializer.
/// The non-generic registry is consulted by <see cref="ConfiglueMessagePackResolver"/>, so fragments
/// remain serializable on trimming and NativeAOT hosts.
/// </remarks>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ConfiglueMessagePackFragmentRegistry
{
    private static readonly ConcurrentDictionary<Type, object> Formatters = new();

    /// <summary>Registers the generated formatter for one fragment type.</summary>
    public static void Register(Type fragmentType, object formatter)
    {
        ArgumentNullException.ThrowIfNull(fragmentType);
        ArgumentNullException.ThrowIfNull(formatter);
        if (!Formatters.TryAdd(fragmentType, formatter))
        {
            throw new InvalidOperationException(
                $"A MessagePack formatter for fragment '{fragmentType}' is already registered."
            );
        }
    }

    /// <summary>Tries to get the registered formatter for a fragment type.</summary>
    public static bool TryGetFormatter<T>(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out IMessagePackFormatter<T>? formatter
    )
    {
        if (Formatters.TryGetValue(typeof(T), out var registered))
        {
            formatter = (IMessagePackFormatter<T>)registered;
            return true;
        }

        formatter = null;
        return false;
    }

    /// <summary>
    /// Gets the registered formatter for a type, initializing generated model registration on first use.
    /// </summary>
    /// <returns>The generated formatter, or <see langword="null"/> when the type is not a generated fragment.</returns>
    public static IMessagePackFormatter<T>? GetOrNull<T>()
    {
        if (TryGetFormatter<T>(out var formatter))
        {
            return formatter;
        }

        if (
            typeof(IConfiglueFragment).IsAssignableFrom(typeof(T))
            && typeof(T).DeclaringType is { } declaringType
        )
        {
            // Running the model's class constructor triggers the generated registration without a
            // module initializer, which Unity does not support.
            RuntimeHelpers.RunClassConstructor(declaringType.TypeHandle);
            if (TryGetFormatter<T>(out formatter))
            {
                return formatter;
            }
        }

        return null;
    }
}

/// <summary>Provides generated MessagePack formatters for one fragment type.</summary>
/// <typeparam name="TFragment">The generated fragment type.</typeparam>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ConfiglueMessagePackFragmentRegistry<TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    /// <summary>Gets the generated formatter registered for this fragment.</summary>
    public static IMessagePackFormatter<TFragment> Formatter =>
        ConfiglueMessagePackFragmentRegistry.GetOrNull<TFragment>()
        ?? throw new InvalidOperationException(
            $"A generated MessagePack formatter for fragment '{typeof(TFragment)}' has not been registered."
        );

    /// <summary>Tries to get the generated formatter registered for this fragment.</summary>
    public static bool TryGetFormatter(
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
            out IMessagePackFormatter<TFragment>? formatter
    ) => ConfiglueMessagePackFragmentRegistry.TryGetFormatter(out formatter);

    /// <summary>Registers the generated formatter for this fragment.</summary>
    public static void Register(IMessagePackFormatter<TFragment> formatter)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        ConfiglueMessagePackFragmentRegistry.Register(typeof(TFragment), formatter);
    }
}
