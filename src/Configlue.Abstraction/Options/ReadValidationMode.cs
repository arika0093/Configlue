namespace Configlue;

/// <summary>How validation failures are handled when configuration state is read.</summary>
public enum ReadValidationMode
{
    /// <summary>Throw when the finally resolved value fails validation.</summary>
    EffectiveThrow = 0,

    /// <summary>Throw as soon as any source contributes a value that fails validation.</summary>
    StrictThrow = 1,

    /// <summary>Drop invalid contributed members and resolve the remaining values.</summary>
    IgnoreValue = 2,
}
