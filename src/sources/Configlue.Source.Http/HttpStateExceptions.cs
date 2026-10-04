namespace Configlue.Source.Http;

/// <summary>Base error for typed State HTTP failures. No ASP.NET types leak through this hierarchy.</summary>
public class HttpStateException : Exception
{
    /// <summary>Creates a State HTTP error.</summary>
    public HttpStateException(string message)
        : base(message) { }

    /// <summary>Creates a State HTTP error with an inner cause.</summary>
    public HttpStateException(string message, Exception innerException)
        : base(message, innerException) { }
}

/// <summary>The server rejected the payload as malformed (HTTP 400).</summary>
public sealed class HttpStateRequestException : HttpStateException
{
    /// <summary>Creates a malformed-payload error.</summary>
    public HttpStateRequestException(string message)
        : base(message) { }
}

/// <summary>The server rejected the value as invalid (HTTP 422).</summary>
public class HttpStateValidationException : HttpStateException
{
    /// <summary>Creates a validation error.</summary>
    public HttpStateValidationException(string message)
        : base(message) { }

    /// <summary>Creates a validation error with failure details.</summary>
    public HttpStateValidationException(string message, IReadOnlyList<string> failures)
        : base(message)
    {
        Failures = failures;
    }

    /// <summary>The validation failure messages, when provided by the server.</summary>
    public IReadOnlyList<string>? Failures { get; }
}

/// <summary>The effective state changed after it was read (HTTP 412). Derives from <see cref="StateConflictException"/> so Core retry paths keep working.</summary>
public sealed class HttpStateStaleException : StateConflictException
{
    /// <summary>Creates a stale-ETag error.</summary>
    public HttpStateStaleException(string message)
        : base(message) { }
}

/// <summary>A Core write conflict reported by the server (HTTP 409). Derives from <see cref="StateConflictException"/>.</summary>
public sealed class HttpStateWriteConflictException : StateConflictException
{
    /// <summary>Creates a write-conflict error.</summary>
    public HttpStateWriteConflictException(string message)
        : base(message) { }

    /// <summary>Creates a write-conflict error with an inner cause.</summary>
    public HttpStateWriteConflictException(string message, Exception innerException)
        : base(message + " " + innerException.Message) { }
}

/// <summary>The server requires authentication (HTTP 401).</summary>
public sealed class HttpStateUnauthorizedException : HttpStateException
{
    /// <summary>Creates an authentication error.</summary>
    public HttpStateUnauthorizedException(string message)
        : base(message) { }
}

/// <summary>The server denied the request (HTTP 403).</summary>
public sealed class HttpStateForbiddenException : HttpStateException
{
    /// <summary>Creates an authorization error.</summary>
    public HttpStateForbiddenException(string message)
        : base(message) { }
}

/// <summary>The State HTTP endpoint is temporarily unavailable (transport, timeout, or 5xx).</summary>
public sealed class HttpStateUnavailableException : HttpStateException
{
    /// <summary>Creates an unavailability error.</summary>
    public HttpStateUnavailableException(string message)
        : base(message) { }

    /// <summary>Creates an unavailability error with an inner cause.</summary>
    public HttpStateUnavailableException(string message, Exception innerException)
        : base(message, innerException) { }
}
