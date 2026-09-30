using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using Configlue.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Configlue;

internal sealed partial class ConfiglueRuntime<TModel, TFragment>
    where TModel : IConfiglueModel<TModel, TFragment>
    where TFragment : class, IConfiglueFragment<TFragment>
{
    private async ValueTask<TFragment> MigrateAsync(
        TFragment value,
        StateSchemaMetadata sourceSchema,
        CancellationToken cancellationToken
    ) =>
        await _migrationChain
            .MigrateAsync(value, sourceSchema, cancellationToken)
            .ConfigureAwait(false);
}
