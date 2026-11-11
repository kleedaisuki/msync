using System.Text;
using Msync.Core;

namespace Msync.Hir;

public static class HirArtifact
{
    private static ReadOnlySpan<byte> Magic => "MSYNHIR\0"u8;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    public const ushort FormatVersion = 1;
    public const string LanguageVersion = Artifact.LanguageVersion;

    public static bool IsHir(ReadOnlySpan<byte> bytes) => bytes.StartsWith(Magic);

    public static HirModule Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        try { return Read(stream); }
        catch (MsyncException error)
        { throw new MsyncException($"Cannot read Typed HIR artifact '{path}': {error.Message}", inner: error); }
    }

    public static HirModule Read(Stream stream)
    {
        using BinaryReader reader = new(stream, Utf8, leaveOpen: true);
        try
        {
            if (!reader.ReadBytes(Magic.Length).AsSpan().SequenceEqual(Magic))
                throw new MsyncException("Not an msync Typed HIR artifact.");
            ushort format = reader.ReadUInt16();
            if (format != FormatVersion)
                throw new MsyncException($"Unsupported Typed HIR format {format}; expected {FormatVersion}. Export the original program with this msync version.");
            HirBinaryReader decoder = new(reader);
            string language = decoder.String();
            if (language != LanguageVersion)
                throw new MsyncException($"Unsupported Typed HIR language semantics '{language}'; expected '{LanguageVersion}'.");
            HirModule module = decoder.Module();
            HirValidation.Validate(module);
            return module;
        }
        catch (Exception error) when (error is IOException or ArgumentException or FormatException or OverflowException)
        { throw new MsyncException($"Invalid or truncated Typed HIR artifact: {error.Message}", inner: error); }
    }

    public static void Write(HirModule module, string path)
    {
        HirValidation.Validate(module);
        WriteProduced(module, path);
    }

    /// <summary>Encode sealed compiler output or HIR already decoded at the input boundary.</summary>
    public static void WriteProduced(HirModule module, string path)
    {
        string destination = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, $".{Path.GetFileName(destination)}.msync-{Guid.NewGuid():N}");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) WriteProduced(module, stream);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static void Write(HirModule module, Stream stream)
    {
        HirValidation.Validate(module);
        WriteProduced(module, stream);
    }

    public static void WriteProduced(HirModule module, Stream stream)
    {
        using BinaryWriter writer = new(stream, Utf8, leaveOpen: true);
        writer.Write(Magic);
        writer.Write(FormatVersion);
        HirBinaryWriter encoder = new(writer);
        encoder.String(LanguageVersion);
        encoder.Module(module);
    }
}
