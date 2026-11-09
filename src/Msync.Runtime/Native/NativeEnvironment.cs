using System.Collections;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    private static Value EnvironmentOperation(string name, IReadOnlyList<Value> arguments, RuntimeContext context)
    {
        switch (name)
        {
            case "get":
                return Environment.GetEnvironmentVariable(Text(arguments[0])) is { } value ? new StringValue(value) : Value.Null;
            case "set":
                Environment.SetEnvironmentVariable(Text(arguments[0]), Text(arguments[1]));
                return Value.Null;
            case "unset":
                Environment.SetEnvironmentVariable(Text(arguments[0]), null);
                return Value.Null;
            case "variables":
                return EnvironmentVariables();
            case "working_directory":
                return new PathValue(context.WorkingDirectory, context.WorkingDirectory, HomeDirectory: context.HomeDirectory);
            case "set_working_directory":
                return context.ChangeWorkingDirectory(arguments[0]);
            case "home_directory":
                return new PathValue(context.HomeDirectory, context.WorkingDirectory, HomeDirectory: context.HomeDirectory);
            case "operating_system":
                return new StringValue(OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux");
            case "newline":
                return new StringValue(Environment.NewLine);
            case "path_separator":
                return new StringValue(Path.PathSeparator.ToString());
            default:
                throw new MsyncException($"Unknown environment native '{name}'");
        }
    }

    private static RangeValue EnvironmentVariables()
    {
        var values = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>()
            .OrderBy(entry => (string)entry.Key, StringComparer.Ordinal)
            .Select(entry => (Value)new ScopeValue(new Dictionary<string, Value>(StringComparer.Ordinal)
            {
                ["name"] = new StringValue((string)entry.Key),
                ["value"] = new StringValue((string)entry.Value!)
            })).ToArray();
        return new RangeValue(values, TypeRef.Scope);
    }
}
