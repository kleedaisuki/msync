namespace Msync.Cli;

internal static class ArtifactCommands
{
    internal static bool IsArtifact(string file) => file.EndsWith(".mso", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".mexe", StringComparison.OrdinalIgnoreCase);
    internal static async Task<byte[]> ReadArtifactInput()
    {
        using MemoryStream bytes = new();
        await Console.OpenStandardInput().CopyToAsync(bytes);
        return bytes.ToArray();
    }
}
