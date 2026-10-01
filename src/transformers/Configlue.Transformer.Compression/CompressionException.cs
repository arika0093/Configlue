namespace Configlue.Transformer.Compression;

/// <summary>Represents malformed or unreadable compressed state content.</summary>
/// <remarks>
/// The transformer raises this exception instead of leaking the underlying native compression
/// library failure, so hosts can classify recoverable read failures through
/// <see cref="IStateByteTransformerRecoveryPolicy"/> without depending on the implementation library.
/// The original failure is available as <see cref="Exception.InnerException"/>.
/// </remarks>
public sealed class CompressionException : Exception
{
    /// <summary>Creates a compression failure.</summary>
    public CompressionException(string message)
        : base(message) { }

    /// <summary>Creates a compression failure with the underlying native failure.</summary>
    public CompressionException(string message, Exception innerException)
        : base(message, innerException) { }
}
