using Configlue.CompilerServices;

namespace Configlue;

// Core's aggregate implementation contract is not an application service.
internal interface IConfiglueRuntimeState<T>
    : IWritableState<T>,
        IConfiglueEditSessions<T>,
        IConfiglueDiagnostics<T>,
        IConfiglueRuntimeDiagnostics,
        IConfiglueSources<T>,
        IConfiglueDetailsRuntime,
        IConfiglueReloadDiagnostics,
        IConfiglueStateSnapshotRuntime<T> { }
