using System.ComponentModel.DataAnnotations;

namespace Configlue.Examples.Shared;

/// <summary>
/// Local writable settings demonstrated by the Playground local-settings scenario.
/// Backed by a single JSON file via <c>UseLocalJson</c>.
/// </summary>
[ConfiglueModel("examples.local-settings", Version = 1)]
public partial class LocalSettings
{
    public string Name { get; set; } = "World";

    public string Theme { get; set; } = "System";

    [Range(0, 100)]
    public int RetryCount { get; set; } = 3;
}

/// <summary>
/// Layered settings demonstrated by the Playground layered-configuration,
/// provenance, and write-behavior scenarios.
/// The local file layer is writable; the environment layer overrides it read-only.
/// </summary>
[ConfiglueModel("examples.layered-settings", Version = 1)]
public partial class LayeredSettings
{
    public string Theme { get; set; } = "System";

    [Range(0, 100)]
    public int RetryCount { get; set; } = 3;

    public string Label { get; set; } = "default";
}

/// <summary>
/// Small shared document demonstrated by the HTTP client/server scenario and the
/// PostgreSQL-backed state scenario. The same shape is served by the HttpServer
/// state endpoint and persisted in PostgreSQL by the Playground.
/// </summary>
[ConfiglueModel("examples.shared-board", Version = 1)]
public partial class SharedBoardSettings
{
    public string Title { get; set; } = "Playground board";

    public string Note { get; set; } = "hello";
}
