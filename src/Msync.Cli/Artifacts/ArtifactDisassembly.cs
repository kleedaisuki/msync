using System.Text.Json.Nodes;
using Msync.Core;
using Msync.Core.Hpir;
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
            if (HpirArtifact.HasMagic(bytes))
                return HpirArtifactRenderer.Render(HpirArtifact.ReadTrusted(stream), cancellationToken);
            throw ArtifactCommands.UnsupportedPhysicalArtifact(bytes);
        }
        catch (Exception error) when (error is MsyncException or IOException or ArgumentException or OverflowException or InvalidOperationException)
        { throw new MsyncException($"Cannot disassemble {sourceUri}: {error.Message}", inner: error); }
    }
}
