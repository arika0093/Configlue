using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace Configlue.Extensions.ComponentModel;

/// <summary>
/// Creates generated <c>Observable</c> proxies for Configlue models.
/// </summary>
/// <remarks>
/// The Configlue source generator emits a nested <c>Observable</c> type for every
/// generated model. The adapters in this package locate that type at runtime so that
/// user models do not have to implement <see cref="System.ComponentModel.INotifyPropertyChanged"/>
/// themselves and this package does not need a compile-time reference to user models.
/// When a model has no generated proxy (for example a hand-written fake), the raw model
/// is used instead and only root-level notifications are available.
/// </remarks>
internal static class ConfiglueBindableProxy
{
    private static readonly ConcurrentDictionary<Type, ConstructorInfo?> Constructors = new();

    public static object Create(Type modelType, object value, Action? onChanged)
    {
        var constructor = Constructors.GetOrAdd(
            modelType,
            static type =>
            {
                var nested = type.GetNestedType("Observable", BindingFlags.Public);
                return nested?.GetConstructor(new[] { type, typeof(Action) });
            }
        );

        if (constructor is null)
        {
            return value;
        }

        return constructor.Invoke(new[] { value, onChanged });
    }
}
