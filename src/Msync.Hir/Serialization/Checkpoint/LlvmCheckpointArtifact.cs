using System.Globalization;
using Msync.Core;

namespace Msync.Hir;

public enum LlvmCheckpointPhase : byte { Lowered = 1, Optimized = 2 }

public sealed record LlvmCheckpoint(LlvmHirLoweringResult Graph, LlvmCheckpointPhase Phase, int Optimization)
{
    public static LlvmCheckpoint Lowered(LlvmHirLoweringResult graph) => new(graph, LlvmCheckpointPhase.Lowered, 0);
    public string PhaseName => Phase == LlvmCheckpointPhase.Lowered ? "lowered" : "post-middle-end";
}

public static partial class LlvmCheckpointArtifact
{
    private static ReadOnlySpan<byte> Magic => "MSYNLLVM"u8;
    public const ushort FormatVersion = 1;
    // Bump on changes to the producer contracts or middle-end pipeline that invalidate saved facts.
    private const ushort ProducerVersion = 1;

    public static bool HasMagic(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Magic);

    public static LlvmCheckpoint Read(string path, CompilationTrace? trace = null)
    {
        using FileStream stream = File.OpenRead(path);
        return Read(stream, trace);
    }

    public static LlvmCheckpoint Read(Stream stream, CompilationTrace? trace = null)
    {
        if (!stream.CanSeek)
        {
            using MemoryStream buffer = new();
            stream.CopyTo(buffer);
            buffer.Position = 0;
            return Read(buffer, trace);
        }
        return trace is null ? Decode(stream, null)
            : trace.Measure("compiler.mllvm.decode", (stream, trace), static state => Decode(state.stream, state.trace));
    }

    public static void Write(LlvmCheckpoint checkpoint, string path, CompilationTrace? trace = null) =>
        ArtifactFile.Write(path, (checkpoint, trace), static (state, stream) => Write(state.checkpoint, stream, state.trace));

    public static void Write(LlvmCheckpoint checkpoint, Stream stream, CompilationTrace? trace = null)
    {
        if (trace is null) Encode(checkpoint, stream, null);
        else
        {
            var attributes = new Dictionary<string, string>
            {
                ["checkpoint.phase"] = checkpoint.PhaseName,
                ["optimization.level"] = checkpoint.Optimization.ToString(CultureInfo.InvariantCulture),
                ["function.name"] = checkpoint.Graph.Entry.Name
            };
            if (checkpoint.Graph.Entry.BasicBlocks.FirstOrDefault()?.FirstInstruction?.Source is { } source)
                attributes["source.file"] = source.Source;
            trace.Run("compiler.mllvm.encode", (checkpoint, stream, trace),
                static state => Encode(state.checkpoint, state.stream, state.trace), attributes);
        }
    }

    private static void Encode(LlvmCheckpoint checkpoint, Stream stream, CompilationTrace? trace)
    {
        var graph = checkpoint.Graph;
        if (graph.SessionGeneration is not null || graph.SessionCells.Count != 0 || graph.SessionDispatches.Count != 0 ||
            graph.SessionSnapshotReads is not null || graph.SessionScopeLayouts is not null || graph.SessionStreamsRange)
            throw new MsyncException("Cannot save a REPL compiler checkpoint. Lower a standalone source or HIR file instead.");
        ValidatePhase(checkpoint.Phase, checkpoint.Optimization);
        using var counted = trace is not null && !stream.CanSeek ? new CountedOutput(stream) : null;
        Stream output = counted ?? stream;
        long start = trace is null ? 0 : output.Position;
        using BinaryWriter writer = new(output, HirArtifact.Utf8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        writer.Write(ProducerVersion);
        writer.Write((byte)checkpoint.Phase);
        writer.Write((byte)checkpoint.Optimization);
        Encoder encoder = new(writer);
        encoder.Text(HirArtifact.LanguageVersion);
        if (trace?.Detail is { } detail)
        {
            detail.Run("compiler.mllvm.encode.graph", (encoder, graph, detail),
                static state => state.encoder.Graph(state.graph.Module, state.detail));
            detail.Run("compiler.mllvm.encode.producer", (encoder, graph, detail),
                static state => state.encoder.Producer(state.graph, state.detail));
        }
        else
        {
            encoder.Graph(graph.Module, trace);
            encoder.Producer(graph, trace);
        }
        if (trace is not null)
        {
            trace.Metric("artifact.bytes", output.Position - start);
            trace.Metric("graph.values", encoder.ValueCount);
            trace.Metric("graph.types", encoder.TypeCount);
            trace.Metric("graph.functions", graph.Module.Functions.Count);
        }
    }

    private static LlvmCheckpoint Decode(Stream stream, CompilationTrace? trace)
    {
        using BinaryReader reader = new(stream, HirArtifact.Utf8, leaveOpen: true);
        try
        {
            trace?.Metric("artifact.bytes", stream.Length - stream.Position);
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic)) throw Invalid("not an msync .mllvm checkpoint");
            ushort format = reader.ReadUInt16();
            trace?.Metric("checkpoint.format", format);
            if (format != FormatVersion) throw Invalid($"unsupported checkpoint format {format}; expected {FormatVersion}");
            ushort producer = reader.ReadUInt16();
            trace?.Metric("checkpoint.producer", producer);
            if (producer != ProducerVersion) throw Invalid($"incompatible producer contract {producer}; expected {ProducerVersion}");
            var phase = (LlvmCheckpointPhase)reader.ReadByte();
            int optimization = reader.ReadByte();
            trace?.Metric("checkpoint.phase", (byte)phase);
            trace?.Metric("optimization.level", optimization);
            ValidatePhase(phase, optimization);
            Decoder decoder = new(reader);
            string language = decoder.Text();
            if (language != HirArtifact.LanguageVersion) throw Invalid($"incompatible language semantics '{language}'");
            var module = trace?.Detail is { } detail
                ? detail.Measure("compiler.mllvm.decode.graph", (decoder, detail), static state => state.decoder.Graph(state.detail))
                : decoder.Graph(trace);
            var graph = trace?.Detail is { } producerTrace
                ? producerTrace.Measure("compiler.mllvm.decode.producer", (decoder, module, producerTrace),
                    static state => state.decoder.Producer(state.module, state.producerTrace))
                : decoder.Producer(module, trace);
            if (stream.Position != stream.Length) throw Invalid("unexpected data after producer tables");
            trace?.Metric("graph.values", decoder.ValueCount);
            trace?.Metric("graph.types", decoder.TypeCount);
            trace?.Metric("graph.functions", module.Functions.Count);
            return new(graph, phase, optimization);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or FormatException or
            OverflowException or InvalidOperationException or InvalidCastException or IndexOutOfRangeException or NotSupportedException)
        {
            throw new MsyncException($"Cannot read .mllvm checkpoint: {error.Message.TrimEnd('.')}. " +
                "Regenerate it with 'msync lower' and, if needed, 'msync optimize'.", inner: error);
        }
    }

    private static void ValidatePhase(LlvmCheckpointPhase phase, int optimization)
    {
        if (phase is not (LlvmCheckpointPhase.Lowered or LlvmCheckpointPhase.Optimized) ||
            optimization is < 0 or > 3 || phase == LlvmCheckpointPhase.Lowered && optimization != 0)
            throw Invalid($"invalid phase {(byte)phase} or optimization level {optimization}");
    }

    private static InvalidDataException Invalid(string reason) => new($"Invalid checkpoint: {reason}.");

    private sealed class CountedOutput(Stream stream) : Stream
    {
        private long count;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => count;
        public override long Position { get => count; set => throw new NotSupportedException(); }
        public override void Flush() => stream.Flush();
        public override void Write(byte[] buffer, int offset, int length) => Write(buffer.AsSpan(offset, length));
        public override void Write(ReadOnlySpan<byte> buffer) { stream.Write(buffer); count += buffer.Length; }
        public override void WriteByte(byte value) { stream.WriteByte(value); ++count; }
        public override int Read(byte[] buffer, int offset, int length) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
