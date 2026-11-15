using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Msync.Core;
using Msync.Core.Native;
using Msync.Hir;

namespace Msync.Cli;

internal static class ArtifactDisassembly
{
    internal static JsonObject Position(int line, int column) => new() { ["line"] = line, ["character"] = column };
    internal static JsonObject Range(int line, int column, int length) =>
        new() { ["start"] = Position(line, column), ["end"] = Position(line, column + length) };

    internal static string? SourceUri(string source, string? bound = null)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.IsFile || uri.Scheme is "msync-stdlib" or "vscode-remote")) return uri.AbsoluteUri;
        if (source.StartsWith('<')) return null;
        if (bound is not null && Path.IsPathFullyQualified(bound)
            && (source == bound || Path.GetFileName(source) == Path.GetFileName(bound))) return new Uri(bound).AbsoluteUri;
        return Path.IsPathFullyQualified(source) ? new Uri(source).AbsoluteUri : null;
    }

    internal static ArtifactDisassemblyResult Read(byte[] bytes, string sourceUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HirArtifact.IsHir(bytes) || sourceUri.EndsWith(".mhir", StringComparison.OrdinalIgnoreCase))
            return HirArtifactRenderer.Read(bytes, cancellationToken);
        using MemoryStream stream = new(bytes, writable: false);
        try
        {
            NativeProgram program = NativeArtifact.Read(stream);
            cancellationToken.ThrowIfCancellationRequested();
            int version = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
            return new NativeArtifactRenderer(program, version, sourceUri, cancellationToken).Render();
        }
        catch (Exception error) when (error is MsyncException or IOException or ArgumentException or OverflowException or InvalidOperationException)
        { throw new MsyncException($"Cannot disassemble {sourceUri}: {error.Message}", inner: error); }
    }

    internal static string Quote(string value)
    {
        StringBuilder text = new("\"");
        for (int index = 0; index < value.Length; index++)
            switch (value[index])
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    char character = value[index];
                    bool invalidSurrogate = char.IsHighSurrogate(character)
                        ? index + 1 == value.Length || !char.IsLowSurrogate(value[index + 1])
                        : char.IsLowSurrogate(character) && (index == 0 || !char.IsHighSurrogate(value[index - 1]));
                    if (char.IsControl(character) || invalidSurrogate)
                        text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else text.Append(character);
                    break;
            }
        return text.Append('"').ToString();
    }
}
