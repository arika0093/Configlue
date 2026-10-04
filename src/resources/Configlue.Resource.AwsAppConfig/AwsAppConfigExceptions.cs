namespace Configlue.Resource.AwsAppConfig;

/// <summary>Failure information for AWS AppConfig resource operations.</summary>
public class AwsAppConfigException : Exception
{
    /// <summary>Creates an AppConfig resource failure.</summary>
    public AwsAppConfigException(string message)
        : base(message) { }

    /// <summary>Creates an AppConfig resource failure with an inner cause.</summary>
    public AwsAppConfigException(string message, Exception? innerException)
        : base(message, innerException) { }
}

/// <summary>A transient AppConfig failure. The last successfully loaded state is retained.</summary>
public sealed class AwsAppConfigTransientException : AwsAppConfigException
{
    /// <summary>Creates a transient AppConfig failure.</summary>
    public AwsAppConfigTransientException(string message)
        : base(message) { }

    /// <summary>Creates a transient AppConfig failure with an inner cause.</summary>
    public AwsAppConfigTransientException(string message, Exception? innerException)
        : base(message, innerException) { }
}

/// <summary>The AppConfig session token is no longer valid and the session must be restarted.</summary>
public sealed class AwsAppConfigSessionExpiredException : AwsAppConfigException
{
    /// <summary>Creates a session-expired failure.</summary>
    public AwsAppConfigSessionExpiredException(string message)
        : base(message) { }

    /// <summary>Creates a session-expired failure with an inner cause.</summary>
    public AwsAppConfigSessionExpiredException(string message, Exception? innerException)
        : base(message, innerException) { }
}
