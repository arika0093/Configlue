using System.Buffers;

namespace Configlue.Codecs;

/// <summary>Reads an embedded schema identifier from a serialized payload.</summary>
/// <remarks>Advanced provider SPI for codecs with embedded schema metadata.</remarks>
[System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Advanced)]
public interface IStateSchemaMetadataReader
{
    /// <summary>Returns schema metadata embedded in the payload, if present.</summary>
    StateSchemaMetadata? ReadSchemaMetadata(in ReadOnlySequence<byte> source);
}
