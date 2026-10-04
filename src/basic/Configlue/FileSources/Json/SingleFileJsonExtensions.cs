using System.Text.Json;
using Configlue.CompilerServices;

namespace Configlue.Provider.Json;

/// <summary>Registers one local JSON settings file with ordinary application-settings defaults.</summary>
/// <remarks>
/// <para>
/// This is the zero-ceremony single-file path: one model declaration plus one file declaration,
/// with no additional concepts to learn. Reads return model defaults when the file is missing;
/// the file (including its directory) is created on the first save.
/// </para>
/// <para>Single-file defaults:</para>
/// <list type="bullet">
/// <item>Plain simple-layout JSON documents (<c>{ "$version": 1, ... }</c>); comments are accepted on read.</item>
/// <item>Writes replace the file atomically (temporary file plus rename) and keep one backup generation.</item>
/// <item>The file is watched for external changes; listeners observe the reloaded value.</item>
/// <item>A malformed document fails reads (the codec's format exception propagates) instead of silently falling back to defaults. The watcher reports the failure and recovers on the next valid write.</item>
/// <item>Validation failures throw; concurrent write conflicts fail instead of overwriting.</item>
/// </list>
/// <para>
/// When the application outgrows one file, keep the same <c>IWritableState&lt;T&gt;</c> consumer
/// code and add layered sources instead of rewriting it.
/// </para>
/// </remarks>
public static class SingleFileJsonExtensions
{
    /// <summary>Stores this model in one local JSON file at the supplied path.</summary>
    /// <param name="model">The model registration being configured.</param>
    /// <param name="path">The settings file path. Relative paths resolve against the current directory.</param>
    /// <param name="serializerOptions">Optional JSON options (for example a source-generated resolver for NativeAOT). The default favors hand-editable JSON.</param>
    public static void UseLocalJson<TModel>(
        this ConfiglueModelBuilder<TModel> model,
        string path,
        JsonSerializerOptions? serializerOptions = null
    )
        where TModel : IConfiglueFacadeModel<TModel>
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        model.UseJsonFile(
            new JsonFileSourceOptions
            {
                Path = path,
                SerializerOptions =
                    serializerOptions ?? new JsonSerializerOptions { WriteIndented = true },
            }
        );
    }
}
