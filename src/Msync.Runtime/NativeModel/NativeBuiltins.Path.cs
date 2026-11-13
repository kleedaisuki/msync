using Msync.Core;
using Msync.Core.Native;
using NativePathValue = Msync.Core.Native.NativePath;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static NativeValue PathOperation(string operation, IReadOnlyList<NativeValue> args, NativeRuntimeContext context) => operation switch
    {
        "identity" => NativeValue.FromPath(context.AsPath(args[0])),
        "join" => NativeValue.FromPath(JoinPath(context.AsPath(args[0]), args[1])),
        "qualify" => NativeValue.FromPath(QualifyPath(context.AsPath(args[1]), args[0].AsConnection(), context)),
        _ => throw new MsyncException($"Unknown path native '{operation}'")
    };

    public static NativePathValue JoinPath(NativePathValue left, NativeValue right) => right.Kind == NativeKind.Path
        ? PathSemantics.JoinValue(left, right.AsPath()) : PathSemantics.JoinValue(left, Text(right));

    public static NativePathValue QualifyPath(NativePathValue path, NativeConnection connection, NativeRuntimeContext context)
    {
        if (connection.Kind == ConnectionKind.Environment)
            return context.AsPath(NativeValue.FromString(Environment.GetEnvironmentVariable(path.Raw)
                ?? throw new MsyncException($"Environment variable '{path.Raw}' is not set")));
        if (connection.Kind == ConnectionKind.SearchPath)
            return new(ResolvePathName(path.Raw, context), context.WorkingDirectory, HomeDirectory: context.HomeDirectory);
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

    private static string ResolvePathName(string name, NativeRuntimeContext context)
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
