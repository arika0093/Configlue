using System.ComponentModel;

#if CONFIGLUE_FRAGMENT_RUNTIME
namespace Configlue;

#else
namespace SparseFragments;

#endif

/// <summary>Distinguishes JSON Patch interop failures.</summary>
[EditorBrowsable(EditorBrowsableState.Advanced)]
public enum JsonPatchErrorKind
{
    /// <summary>The patch document is not a valid JSON Patch array.</summary>
    MalformedDocument = 0,

    /// <summary>The operation name is unknown.</summary>
    UnknownOperation = 1,

    /// <summary>A JSON Pointer is malformed.</summary>
    MalformedPointer = 2,

    /// <summary>A JSON property does not map to a fragment member.</summary>
    UnmappedProperty = 3,

    /// <summary>A remove, replace, or test target does not exist.</summary>
    MissingTarget = 4,

    /// <summary>A parent required for a nested add does not exist.</summary>
    MissingParent = 5,

    /// <summary>An array index is invalid.</summary>
    InvalidArrayIndex = 6,

    /// <summary>A test operation failed.</summary>
    TestFailed = 7,

    /// <summary>A value could not be deserialized to a fragment member.</summary>
    DeserializationFailed = 8,
}
