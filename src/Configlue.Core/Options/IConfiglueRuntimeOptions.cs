namespace Configlue;

// Core's aggregate implementation contract is not an application service.
internal interface IConfiglueRuntimeOptions<T>
    : IWritableOptions<T>,
        IConfiglueInspection<T>,
        IConfiglueEditSessions<T>,
        IConfiglueDiagnostics<T>,
        IConfiglueSources<T> { }
