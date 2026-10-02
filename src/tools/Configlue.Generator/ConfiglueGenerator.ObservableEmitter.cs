using System.Collections.Immutable;
using Microsoft.CodeAnalysis.CSharp;

namespace Configlue.Generator;

public sealed partial class ConfiglueGenerator
{
    // Emits a host-neutral, ComponentModel-facing bindable proxy over a generated model.
    //
    // The proxy wraps a live model instance. Scalar property setters mutate the
    // underlying model and raise PropertyChanged, nested generated models are exposed
    // through cached child proxies (so `Value.Database.Host = ...` is observable), and
    // collections expose the underlying value with explicit replace-only semantics.
    //
    // No user model changes are required: the proxy is generated from the same member
    // graph the rest of the generator already knows about.
    private static void AppendObservableModel(
        IndentedStringBuilder code,
        string valueType,
        bool valueIsReferenceType,
        ImmutableArray<MemberModel> members
    )
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        code.AppendLineAt(
            1,
            "/// <summary>A bindable ComponentModel proxy over this configuration model; nested structural models and collections are exposed through generated members.</summary>"
        );
        code.AppendLineAt(
            1,
            "public sealed class Observable : global::System.ComponentModel.INotifyPropertyChanged"
        );
        code.AppendLineAt(1, "{");
        code.AppendLineAt(2, "private readonly " + valueType + " __value;");
        code.AppendLineAt(2, "private readonly global::System.Action? __onChanged;");
        code.AppendLineAt(
            2,
            "/// <summary>Creates a bindable proxy over the supplied model.</summary>"
        );
        code.AppendLineAt(
            2,
            "public Observable(" + valueType + " value, global::System.Action? onChanged = null)"
        );
        code.AppendLineAt(2, "{");
        if (valueIsReferenceType)
        {
            code.AppendLineAt(3, "if (value is null)");
            code.AppendLineAt(3, "{");
            code.AppendLineAt(4, "throw new global::System.ArgumentNullException(nameof(value));");
            code.AppendLineAt(3, "}");
        }

        code.AppendLineAt(3, "__value = value;");
        code.AppendLineAt(3, "__onChanged = onChanged;");
        code.AppendLineAt(2, "}");
        code.AppendLineAt(2, "/// <inheritdoc />");
        code.AppendLineAt(
            2,
            "public event global::System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;"
        );
        foreach (var member in members)
        {
            AppendObservableMember(code, member);
        }

        code.AppendLineAt(
            2,
            "private void __RaisePropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new global::System.ComponentModel.PropertyChangedEventArgs(propertyName));"
        );
        code.AppendLineAt(2, "private void __NotifyChanged() => __onChanged?.Invoke();");
        code.AppendLineAt(1, "}");
    }

    private static void AppendObservableMember(IndentedStringBuilder code, MemberModel member)
    {
        code.CancellationToken.ThrowIfCancellationRequested();
        var name = EscapeIdentifier(member.Property.Name);
        var propertyName = SymbolDisplay.FormatLiteral(member.Property.Name, true);
        // TypeFormat (used by TypeModel.Name) is fully qualified and preserves nullable
        // annotations, which keeps the generated getters warning-free.
        var valueType = member.Property.Type.Name;

        if (member.Property.IsInitOnly || member.Property.IsReadOnly)
        {
            if (member.ChildModel is not null && member.ChildIsReferenceType)
                AppendObservableChildMember(code, member, name, propertyName, valueType);
            else
            {
                code.AppendLineAt(
                    2,
                    "/// <summary>Gets the underlying init-only model value. Use a typed patch to replace its contribution.</summary>"
                );
                code.AppendLineAt(
                    2,
                    "public " + valueType + " " + name + " => __value." + name + ";"
                );
            }
            return;
        }

        if (member.Collection.Kind != CollectionKind.Unsupported)
        {
            code.AppendLineAt(
                2,
                "/// <summary>Gets or sets the underlying collection value. Element mutations are not tracked; assign a new collection to notify.</summary>"
            );
            code.AppendLineAt(2, "public " + valueType + " " + name);
            code.AppendLineAt(2, "{");
            code.AppendLineAt(3, "get => __value." + name + ";");
            code.AppendLineAt(3, "set");
            code.AppendLineAt(3, "{");
            code.AppendLineAt(
                4,
                "if (!global::System.Collections.Generic.EqualityComparer<"
                    + valueType
                    + ">.Default.Equals(__value."
                    + name
                    + ", value))"
            );
            code.AppendLineAt(4, "{");
            code.AppendLineAt(5, "__value." + name + " = value;");
            code.AppendLineAt(5, "__RaisePropertyChanged(" + propertyName + ");");
            code.AppendLineAt(5, "__NotifyChanged();");
            code.AppendLineAt(4, "}");
            code.AppendLineAt(3, "}");
            code.AppendLineAt(2, "}");
            return;
        }

        // Only reference-type generated models get a nested proxy. Struct children have
        // value semantics and are exposed as a replaceable scalar instead.
        if (member.ChildModel is not null && member.ChildIsReferenceType)
        {
            AppendObservableChildMember(code, member, name, propertyName, valueType);
            return;
        }

        code.AppendLineAt(2, "/// <summary>Gets or sets the underlying scalar value.</summary>");
        code.AppendLineAt(2, "public " + valueType + " " + name);
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "get => __value." + name + ";");
        code.AppendLineAt(3, "set");
        code.AppendLineAt(3, "{");
        code.AppendLineAt(
            4,
            "if (!global::System.Collections.Generic.EqualityComparer<"
                + valueType
                + ">.Default.Equals(__value."
                + name
                + ", value))"
        );
        code.AppendLineAt(4, "{");
        code.AppendLineAt(5, "__value." + name + " = value;");
        code.AppendLineAt(5, "__RaisePropertyChanged(" + propertyName + ");");
        code.AppendLineAt(5, "__NotifyChanged();");
        code.AppendLineAt(4, "}");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(2, "}");
    }

    private static void AppendObservableChildMember(
        IndentedStringBuilder code,
        MemberModel member,
        string name,
        string propertyName,
        string valueType
    )
    {
        var childObservableType = member.ChildSchemaType! + ".Observable";
        code.AppendLineAt(2, "private " + valueType + " __source_" + name + " = null!;");
        code.AppendLineAt(2, "private " + childObservableType + "? __proxy_" + name + ";");
        code.AppendLineAt(
            2,
            "/// <summary>Gets a bindable proxy over the nested model, or null when it is not set.</summary>"
        );
        code.AppendLineAt(2, "public " + childObservableType + "? " + name);
        code.AppendLineAt(2, "{");
        code.AppendLineAt(3, "get");
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "var current = __value." + name + ";");
        code.AppendLineAt(4, "if (current is null)");
        code.AppendLineAt(4, "{");
        code.AppendLineAt(5, "return null;");
        code.AppendLineAt(4, "}");
        code.AppendLineAt(
            4,
            "if (!global::System.Object.ReferenceEquals(__source_" + name + ", current))"
        );
        code.AppendLineAt(4, "{");
        code.AppendLineAt(5, "__source_" + name + " = current;");
        code.AppendLineAt(
            5,
            "__proxy_" + name + " = new " + childObservableType + "(current, __onChanged);"
        );
        code.AppendLineAt(4, "}");
        code.AppendLineAt(4, "return __proxy_" + name + ";");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(2, "}");
        if (!member.Property.IsInitOnly && !member.Property.IsReadOnly)
            AppendObservableChildReplaceMethod(code, name, propertyName, valueType);
    }

    private static void AppendObservableChildReplaceMethod(
        IndentedStringBuilder code,
        string name,
        string propertyName,
        string valueType
    )
    {
        code.AppendLineAt(
            2,
            "/// <summary>Replaces the nested model value and notifies bindings.</summary>"
        );
        code.AppendLineAt(2, "public void Set" + name + "(" + valueType + " value)");
        code.AppendLineAt(2, "{");
        code.AppendLineAt(
            3,
            "if (global::System.Collections.Generic.EqualityComparer<"
                + valueType
                + ">.Default.Equals(__value."
                + name
                + ", value))"
        );
        code.AppendLineAt(3, "{");
        code.AppendLineAt(4, "return;");
        code.AppendLineAt(3, "}");
        code.AppendLineAt(3, "__value." + name + " = value;");
        code.AppendLineAt(3, "__source_" + name + " = null!;");
        code.AppendLineAt(3, "__proxy_" + name + " = null;");
        code.AppendLineAt(3, "__RaisePropertyChanged(" + propertyName + ");");
        code.AppendLineAt(3, "__NotifyChanged();");
        code.AppendLineAt(2, "}");
    }
}
