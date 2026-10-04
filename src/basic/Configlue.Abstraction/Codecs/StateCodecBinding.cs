namespace Configlue.Codecs;

/// <summary>Selects either a state-typed codec or the explicitly dynamic codec contract.</summary>
/// <remarks>
/// Use <see cref="Typed{T}(IStateCodec{T})"/> for a codec tied to one state type. Use
/// <see cref="Dynamic(IStateCodec)"/> only when serialization intentionally dispatches on a runtime type.
/// The explicit factories keep codecs implementing both contracts unambiguous and prevent arbitrary objects
/// from entering serialized source configuration. The binding is consumed by the canonical
/// provider composition object (<c>Configlue.Extensibility.SerializedSource{T}</c>); provider
/// options expose it so ordinary application code keeps passing codecs without touching
/// composition types.
/// </remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public sealed class StateCodecBinding
{
    private readonly object _codec;
    private readonly Type? _typedStateType;
    private readonly bool _isDynamic;

    private StateCodecBinding(object codec, Type? typedStateType, bool isDynamic)
    {
        _codec = codec;
        _typedStateType = typedStateType;
        _isDynamic = isDynamic;
    }

    /// <summary>Gets the state type associated with a typed binding, or <see langword="null"/> for a dynamic binding.</summary>
    public Type? StateType => _typedStateType;

    /// <summary>Creates a binding for a codec specialized for <typeparamref name="T"/>.</summary>
    public static StateCodecBinding Typed<T>(IStateCodec<T> codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return new StateCodecBinding(codec, typeof(T), isDynamic: false);
    }

    /// <summary>Creates an explicit binding for runtime-type codec dispatch.</summary>
    public static StateCodecBinding Dynamic(IStateCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return new StateCodecBinding(codec, typedStateType: null, isDynamic: true);
    }

    /// <summary>Gets the typed codec when this binding is typed for <typeparamref name="T"/>.</summary>
    public bool TryGetTyped<T>(out IStateCodec<T>? codec)
    {
        if (!_isDynamic && _typedStateType == typeof(T))
        {
            codec = (IStateCodec<T>)_codec;
            return true;
        }

        codec = null;
        return false;
    }

    /// <summary>Gets the dynamic codec when this binding explicitly selects runtime-type dispatch.</summary>
    public IStateCodec? DynamicCodec => _isDynamic ? (IStateCodec)_codec : null;

    /// <summary>Gets an additional capability implemented by the selected codec, when present.</summary>
    public TCapability? GetCapability<TCapability>()
        where TCapability : class => _codec as TCapability;
}
