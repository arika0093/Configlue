using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Configlue.Provider.Xml;

/// <summary>Exposes a nested XML element as a resource while preserving sibling elements.</summary>
public sealed class XmlSectionResource : IResourceReader, IResourceWriter, IStateWatcher
{
    private readonly IResourceReader _reader;
    private readonly IResourceWriter? _writer;
    private readonly IStateWatcher? _watcher;
    private readonly string[] _path;

    /// <summary>Creates an XML section resource over a resource with inferred write and watch capabilities.</summary>
    public XmlSectionResource(IResourceReader resource, string sectionPath)
        : this(resource, resource as IResourceWriter, sectionPath, resource as IStateWatcher)
    {
    }

    /// <summary>Creates an XML section resource with separate read, write, and watch capabilities.</summary>
    public XmlSectionResource(
        IResourceReader reader,
        IResourceWriter? writer,
        string sectionPath,
        IStateWatcher? watcher = null)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionPath);
        _reader = reader;
        _writer = writer;
        _watcher = watcher;
        _path = ParsePath(sectionPath);
    }

    /// <summary>Whether a physical writer was supplied.</summary>
    public bool CanWrite => _writer is not null;

    /// <inheritdoc />
    public async ValueTask<ResourceReadResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        var resource = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (resource.Status != StateReadStatus.Success)
        {
            return new ResourceReadResult(resource.Status, default, resource.Revision);
        }

        var document = LoadDocument(resource.Content.Span);
        var current = document.Root ?? throw new XmlException("The XML resource has no root element.");
        foreach (var name in _path)
        {
            var matches = current.Elements().Where(element => element.Name.LocalName == name).Take(2).ToArray();
            if (matches.Length == 0)
            {
                return ResourceReadResult.NotFound(resource.Revision);
            }

            if (matches.Length > 1)
            {
                throw new XmlException($"Section path '{string.Join(':', _path)}' is ambiguous at '{name}'.");
            }

            current = matches[0];
        }

        var payload = current.Elements().Take(2).ToArray();
        var value = payload.Length == 1 && payload[0].Name.LocalName == "configlue"
            ? payload[0]
            : current;
        return ResourceReadResult.Success(Encoding.UTF8.GetBytes(value.ToString(SaveOptions.DisableFormatting)), resource.Revision);
    }

    /// <inheritdoc />
    public async ValueTask<StateWriteResult> WriteAsync(
        ResourceWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var writer = _writer ?? throw new NotSupportedException("This XML section resource is read-only.");
        var current = await _reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        XDocument document;
        if (current.Status == StateReadStatus.Success)
        {
            document = LoadDocument(current.Content.Span);
        }
        else if (current.Status == StateReadStatus.NotFound)
        {
            document = new XDocument(new XElement("configuration"));
        }
        else
        {
            throw new IOException("The XML resource is unavailable and cannot be updated safely.");
        }

        var root = document.Root ?? throw new XmlException("The XML resource has no root element.");
        var container = root;
        for (var index = 0; index < _path.Length - 1; index++)
        {
            var name = _path[index];
            var matches = container.Elements().Where(element => element.Name.LocalName == name).Take(2).ToArray();
            if (matches.Length > 1)
            {
                throw new XmlException($"Section path '{string.Join(':', _path)}' is ambiguous at '{name}'.");
            }

            if (matches.Length == 0)
            {
                var child = new XElement(container.Name.Namespace + name);
                container.Add(child);
                container = child;
            }
            else
            {
                container = matches[0];
            }
        }

        var updatedSection = XElement.Parse(Encoding.UTF8.GetString(request.Content.Span), LoadOptions.PreserveWhitespace);
        var existing = container.Elements().Where(element => element.Name.LocalName == _path[^1]).Take(2).ToArray();
        if (existing.Length > 1)
        {
            throw new XmlException($"Section path '{string.Join(':', _path)}' is ambiguous at '{_path[^1]}'.");
        }

        if (existing.Length == 0)
        {
            container.Add(new XElement(container.Name.Namespace + _path[^1], updatedSection));
        }
        else
        {
            existing[0].RemoveNodes();
            existing[0].Add(updatedSection);
        }

        var updated = Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        var expectedRevision = request.ExpectedRevision ?? current.Revision;
        var checkRevision = request.CheckRevision || request.ExpectedRevision is not null || current.Revision is not null;
        return await writer.WriteAsync(
            new ResourceWriteRequest(updated, expectedRevision, request.Schema, checkRevision),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async ValueTask WaitForChangeAsync(string? observedRevision, CancellationToken cancellationToken = default)
    {
        if (_watcher is not null)
        {
            await _watcher.WaitForChangeAsync(observedRevision, cancellationToken).ConfigureAwait(false);
            return;
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = await ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.Revision, observedRevision, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    private static string[] ParsePath(string sectionPath)
    {
        var path = sectionPath.Replace("__", ":", StringComparison.Ordinal)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return path.Length == 0
            ? throw new ArgumentException("The section path must contain at least one element name.", nameof(sectionPath))
            : path;
    }

    private static XDocument LoadDocument(ReadOnlySpan<byte> content)
    {
        using var memory = new MemoryStream(content.ToArray(), writable: false);
        using var reader = XmlReader.Create(memory, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        return XDocument.Load(reader, LoadOptions.PreserveWhitespace);
    }
}
