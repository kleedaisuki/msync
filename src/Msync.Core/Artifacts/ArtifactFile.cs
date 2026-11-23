namespace Msync.Core;

internal static class ArtifactFile
{
    internal static void Write<T>(string path, T state, Action<T, Stream> write)
    {
        string full = Path.GetFullPath(path);
        string temporary = Path.Combine(Path.GetDirectoryName(full)!,
            $".{Path.GetFileName(full)}.msync-{Guid.NewGuid():N}");
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) write(state, stream);
            File.Move(temporary, full, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
