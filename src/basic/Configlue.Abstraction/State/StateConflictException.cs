namespace Configlue.State;

/// <summary>Raised when a conditional state write targets a stale backend revision.</summary>
public class StateConflictException : Exception
{
    /// <summary>Creates a state conflict exception.</summary>
    public StateConflictException(string message)
        : base(message) { }

    /// <summary>Creates a state conflict exception with an inner cause.</summary>
    protected StateConflictException(string message, Exception innerException)
        : base(message, innerException) { }
}
