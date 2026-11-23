using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    public static NativePathValue QualifyPath(NativePathValue path, NativeConnection connection, NativeRuntimeContext context) =>
        context.Connections.Get(connection).Qualify(path, context);

    internal static NativePathValue QualifyLocal(NativePathValue path, NativeConnection connection, NativeRuntimeContext context)
    {
        if (connection.Kind == ConnectionKind.Local && connection.Address == "pwd" && path.Raw == "pwd")
            return new(context.WorkingDirectory, context.WorkingDirectory, HomeDirectory: context.HomeDirectory);
        if (connection.Kind == ConnectionKind.Local && connection.Address == "file")
        {
            string source = context.SourceFile ?? throw new MsyncException("$file requires a named source file");
            path = path.WithRaw(path.Raw switch
            {
                "this" => source,
                "parent" => Path.GetDirectoryName(source)!,
                _ => Path.Combine(Path.GetDirectoryName(source)!, path.NativeText)
            });
        }
        NativePathValue qualified = PathSemantics.Qualify(path, connection);
        return connection.Kind == ConnectionKind.Local ? context.Capture(qualified) : qualified;
    }

    internal static string ResolvePathName(string name, NativeRuntimeContext context)
    {
        // Qualification resolves a pathname; executable admission belongs to invocation/which.
        if (name.Contains(Path.DirectorySeparatorChar) || name.Contains(Path.AltDirectorySeparatorChar))
            return Path.GetFullPath(name, context.WorkingDirectory);
        string[] extensions = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';') : [""];
        foreach (string directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (string extension in extensions)
            {
                string parent = Path.IsPathRooted(directory) ? directory : Path.Combine(context.WorkingDirectory, directory);
                string candidate = Path.Combine(parent, name + extension);
                if (File.Exists(candidate)) return candidate;
            }
        throw new MsyncException($"Executable '{name}' was not found on PATH");
    }
}
