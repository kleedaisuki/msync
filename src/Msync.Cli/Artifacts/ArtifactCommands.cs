using Msync.Core;
using Msync.Core.Hpir;

namespace Msync.Cli;

internal static class ArtifactCommands
{
    internal static bool IsArtifact(string file) => file.EndsWith(".mso", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".mexe", StringComparison.OrdinalIgnoreCase);

    internal static HpirProgram ReadHpirArtifact(string file, TelemetrySession? telemetry)
    {
        using TelemetrySpan? span = telemetry?.Begin("artifact.read");
        HpirProgram program = span is null ? ReadHpirArtifact(file, (CompilationTrace?)null)
            : span.Measure((file, trace: span.CompilerTrace), static state => ReadHpirArtifact(state.file, state.trace));
        string kind = program.Stage == HpirStage.Executable ? "executable" : "object";
        span?.SetAttribute("artifact.kind", kind);
        span?.SetAttribute("artifact.representation", "hpir");
        telemetry?.SetAttribute("input.kind", kind);
        return program;
    }

    private static HpirProgram ReadHpirArtifact(string file, CompilationTrace? trace)
    {
        try
        {
            using FileStream stream = File.OpenRead(file);
            Span<byte> prefix = stackalloc byte[8];
            int read = stream.ReadAtLeast(prefix, prefix.Length, false);
            stream.Position = 0;
            if (HpirArtifact.HasMagic(prefix[..read])) return HpirArtifact.ReadTrusted(stream, trace);
            throw UnsupportedPhysicalArtifact(prefix[..read]);
        }
        catch (Exception error) when (error is IOException or MsyncException)
        {
            throw new MsyncException($"Cannot read artifact '{file}': {error.Message}", (error as MsyncException)?.Span, error);
        }
    }

    internal static MsyncException UnsupportedPhysicalArtifact(ReadOnlySpan<byte> prefix) =>
        new(prefix.StartsWith("MSYNCIR\0"u8) || prefix.StartsWith("MSYNCBC\0"u8)
            ? "Recompile this Native artifact as HPIR."
            : "The input is not an HPIR artifact.");

    internal static async Task<byte[]> ReadArtifactInput()
    {
        using MemoryStream bytes = new();
        await Console.OpenStandardInput().CopyToAsync(bytes);
        return bytes.ToArray();
    }
}
