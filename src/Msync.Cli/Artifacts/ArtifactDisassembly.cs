using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Msync.Core;

namespace Msync.Cli;

internal static class ArtifactDisassembly
{
    internal static ArtifactDisassemblyResult Read(byte[] bytes, string sourceUri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using MemoryStream stream = new(bytes, writable: false);
        Module module;
        try { module = Artifact.Read(stream); }
        catch (Exception error) when (error is MsyncException or IOException or ArgumentException or OverflowException or InvalidOperationException)
        { throw new MsyncException($"Cannot disassemble {sourceUri}: {error.Message}", inner: error); }
        int version = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2));
        return new ArtifactRenderer(module, version, sourceUri, cancellationToken).Render();
    }

    internal static string Quote(string value)
    {
        StringBuilder text = new("\"");
        foreach (char character in value)
            switch (character)
            {
                case '"': text.Append("\\\""); break;
                case '\\': text.Append("\\\\"); break;
                case '\n': text.Append("\\n"); break;
                case '\r': text.Append("\\r"); break;
                case '\t': text.Append("\\t"); break;
                default:
                    if (char.IsControl(character)) text.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else text.Append(character);
                    break;
            }
        return text.Append('"').ToString();
    }
}
