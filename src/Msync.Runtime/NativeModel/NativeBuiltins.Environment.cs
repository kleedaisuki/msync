using System.Collections;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static readonly NativeLayout EnvironmentRowLayout = NativeBuiltinLayouts.EnvironmentVariable.Layout;

    private static NativeValue EnvironmentOperation(string operation, IReadOnlyList<NativeValue> args, NativeRuntimeContext context)
    {
        switch (operation)
        {
            case "get":
                return Environment.GetEnvironmentVariable(Text(args[0])) is { } value ? NativeValue.FromString(value) : NativeValue.Null;
            case "set": Environment.SetEnvironmentVariable(Text(args[0]), Text(args[1])); return NativeValue.Null;
            case "unset": Environment.SetEnvironmentVariable(Text(args[0]), null); return NativeValue.Null;
            case "variables":
                NativeValue[] rows = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
                    .OrderBy(entry => (string)entry.Key, StringComparer.Ordinal)
                    .Select(entry => Record(context, EnvironmentRowLayout,
                        NativeValue.FromString((string)entry.Key), NativeValue.FromString((string)entry.Value!))).ToArray();
                return NativeValue.FromRange(context.Snapshot(rows, TypeRef.Scope));
            case "working_directory": return NativeValue.FromPath(new(context.WorkingDirectory, context.WorkingDirectory, HomeDirectory: context.HomeDirectory));
            case "set_working_directory": return NativeValue.FromPath(context.ChangeWorkingDirectory(args[0]));
            case "home_directory": return NativeValue.FromPath(new(context.HomeDirectory, context.WorkingDirectory, HomeDirectory: context.HomeDirectory));
            case "operating_system": return NativeValue.FromString(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux");
            case "newline": return NativeValue.FromString(Environment.NewLine);
            case "path_separator": return NativeValue.FromString(Path.PathSeparator.ToString());
            default: throw new MsyncException($"Unknown environment native '{operation}'");
        }
    }
}
