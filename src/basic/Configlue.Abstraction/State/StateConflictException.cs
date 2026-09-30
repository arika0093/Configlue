namespace Configlue.State;

/// <summary>Raised when a conditional state write targets a stale backend revision.</summary>
public sealed class StateConflictException : Exception
{
    /// <summary>Creates a state conflict exception.</summary>
    public StateConflictException(string message)
        : base(message) { }
}
