using System.Buffers;

namespace Configlue.Codecs;

/// <summary>Reads an embedded schema identifier from a serialized payload.</summary>
public interface IStateSchemaMetadataReader
{
    /// <summary>Returns schema metadata embedded in the payload, if present.</summary>
    StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source);
}
