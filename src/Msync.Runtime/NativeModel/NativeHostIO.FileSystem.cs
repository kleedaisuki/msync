using System.Runtime.CompilerServices;
using Msync.Core;
using Msync.Core.Native;
using Msync.Core.Hpir;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static string Status(string path, bool noFollow)
    {
        if (!OperatingSystem.IsWindows()) return NativeFileIdentity.Kind(path, noFollow);
        FileAttributes? attributes = Attributes(path);
        if (attributes is null) return "not_found";
        bool link = NativeFileMetadata.IsLink(path, attributes.Value);
        if (noFollow && link) return "symlink";
        if (!noFollow && link && File.ResolveLinkTarget(path, true)?.Exists == false) return "not_found";
        if ((attributes.Value & FileAttributes.Directory) != 0) return "directory";
        return "regular";
    }

    internal static async ValueTask<string> ReadFileStatus(NativePathValue path, bool noFollow,
        NativeRuntimeContext context, CancellationToken token)
    {
        if (path.Connection is not { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote)
            return Status(context.LocalPath(path), noFollow);
        return await NativeHostIO.RunCommandAsync(remote, RemotePrelude + "\n" + RemoteStatus(noFollow),
            [path.Raw], context, token).ConfigureAwait(false);
    }

    private static string Canonical(string local, bool required)
    {
        string root = Path.GetPathRoot(local)!;
        string current = root;
        foreach (string component in local[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            FileAttributes? attributes = Attributes(current);
            if (attributes is null) { if (required) throw new FileNotFoundException($"Cannot canonicalize missing path '{current}'", current); continue; }
            if (!NativeFileMetadata.IsLink(current, attributes.Value)) continue;
            FileSystemInfo info = (attributes.Value & FileAttributes.Directory) != 0 ? new DirectoryInfo(current) : new FileInfo(current);
            current = info.ResolveLinkTarget(true)?.FullName ?? current;
        }
        return Path.GetFullPath(current);
    }

    internal static ValueTask<NativePathValue> ReadCanonicalPath(HpirFileMetadata _, NativePathValue path,
        NativeRuntimeContext context, CancellationToken token) => ReadCanonicalPath(path, true, context, token);

    internal static ValueTask<NativePathValue> ReadCanonicalPath(NativePathValue path, bool required,
        NativeRuntimeContext context, CancellationToken token)
    {
        if (path.Connection is { Kind: ConnectionKind.Ssh or ConnectionKind.Wsl } remote)
            return CanonicalRemote(path, remote, required, context, token);
        return new(path.WithRaw(Canonical(context.LocalPath(path), required)) with { WorkingDirectory = context.WorkingDirectory });
    }

    private static async ValueTask<NativePathValue> CanonicalRemote(NativePathValue path, NativeConnection remote,
        bool required, NativeRuntimeContext context, CancellationToken token)
    {
        string output = await NativeHostIO.RunCommandAsync(remote,
            RemotePrelude + "\n" + RemoteScript(required ? "canonical" : "weakly_canonical"),
            [path.Raw], context, token).ConfigureAwait(false);
        return path.WithRaw(output.EndsWith('\n') ? output[..^1] : output);
    }

    internal static IAsyncEnumerable<NativePathValue> OpenDirectory(NativePathValue path, bool recursive,
        NativeRuntimeContext context, CancellationToken token) => path.Connection?.Kind is ConnectionKind.Ssh or ConnectionKind.Wsl
        ? RemoteDirectory(path, recursive, context, token) : EnumerateDirectory(path, context.LocalPath(path), recursive, token);

    private static async IAsyncEnumerable<NativePathValue> EnumerateDirectory(NativePathValue path, string local, bool recursive,
        [EnumeratorCancellation] CancellationToken token)
    {
        string[] entries = Directory.GetFileSystemEntries(local);
        Array.Sort(entries, TextSemantics.Ordinal);
        bool windows = LocalWindows(path);
        bool projected = path.Connection is null && windows;
        NativePath rawParent = new(path.Raw, windows, projected ? '/' : null);
        NativePath localParent = projected ? new(path.NativeText, true) : default;
        foreach (string entry in entries)
        {
            token.ThrowIfCancellationRequested();
            var name = Path.GetFileName(entry.AsSpan());
            string raw = rawParent.Append(name);
            NativePathValue child = path.WithRaw(raw, projected ? localParent.Append(name) : null);
            yield return child;
            FileAttributes attributes = File.GetAttributes(entry);
            if (!recursive || (attributes & FileAttributes.Directory) == 0 || NativeFileMetadata.IsLink(entry, attributes)) continue;
            await foreach (NativePathValue value in EnumerateDirectory(child, entry, true, token)) yield return value;
        }
    }
    internal static NativePathValue MaterializeLexicalComponent(NativePathValue path, NativePathComponent component, bool windowsHost)
    {
        bool windows = LocalWindows(path, windowsHost);
        string raw = new NativePath(path.Raw, windows).MaterializeComponent(component);
        string? local = null;
        if (windows && path.Connection is null && path.LocalSpelling is { } spelling)
        {
            var projection = new NativePath(spelling, true).BorrowComponent(component);
            if (!projection.SequenceEqual(raw)) local = projection.ToString();
        }
        return path.WithRaw(raw, local);
    }

    internal static NativePathValue MaterializeLexicalComponent(string text, NativePathComponent component, NativeRuntimeContext context, bool windowsHost) =>
        new(new NativePath(text, windowsHost).MaterializeComponent(component), context.WorkingDirectory, HomeDirectory: context.HomeDirectory);

    internal static NativePath LexicalTextProjection(NativePathValue path, bool windowsHost)
    {
        bool windows = LocalWindows(path, windowsHost);
        // Path-returning lexical operations emit a second spelling only on an
        // unqualified Windows path; their String consumers see that spelling.
        return new(windows && path.Connection is null ? path.NativeText : path.Raw, windows);
    }

    private static bool LocalWindows(NativePathValue path) => LocalWindows(path, OperatingSystem.IsWindows());
    private static bool LocalWindows(NativePathValue path, bool windowsHost) =>
        windowsHost && path.Connection?.Kind is not (ConnectionKind.Ssh or ConnectionKind.Wsl);
}
