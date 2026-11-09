using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    public static async ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments, RuntimeContext context,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!NativeSignatures.TryGet(name, out NativeSignature? signature))
            throw new MsyncException($"Unknown native function '{name}'");
        if (!signature.Variadic && arguments.Count != signature.Parameters.Count)
            throw new MsyncException($"Native '{name}' expects {signature.Parameters.Count} arguments, received {arguments.Count}");
        if (context.DryRun && signature.Effect == NativeEffect.Write) return signature.Result == TypeRef.Bool ? Value.True : Value.Null;
        if (name == "cast") return CastOperation(arguments, context);
        int separator = name.IndexOf('.');
        string group = separator < 0 ? "" : name[..separator];
        string operation = separator < 0 ? name : name[(separator + 1)..];
        return group switch
        {
            "fs" => await FileSystemAsync(operation, arguments, context, cancellationToken, signature.Effect),
            "io" => await IOOperationAsync(operation, arguments, context, cancellationToken),
            "math" => MathOperation(operation, arguments),
            "string" => await StringOperationAsync(operation, arguments, cancellationToken),
            "bool" => BooleanOperation(operation, arguments),
            "path" => PathOperation(operation, arguments, context),
            "ranges" => await RangeOperationAsync(operation, arguments, invoke, cancellationToken),
            "process" => await ProcessOperationAsync(operation, arguments, cancellationToken),
            "environment" => EnvironmentOperation(operation, arguments, context),
            "regex" => RegexOperation(operation, arguments),
            _ => throw new MsyncException($"Native namespace '{group}' is not registered")
        };
    }

    private static string Text(Value value) => value switch
    {
        StringValue text => text.Data,
        PathValue path => path.NativeText,
        _ => throw new MsyncException($"Expected String or Path, received {value.Type}")
    };
    private static bool Boolean(Value value) => value is BoolValue boolean ? boolean.Data : throw new MsyncException($"Expected Bool, received {value.Type}");
    private static Value Bool(bool value) => value ? Value.True : Value.False;

    private static Value StringOperation(string name, IReadOnlyList<Value> args)
    {
        string first = Text(args[0]);
        return name switch
        {
            "contains" => Bool(first.Contains(Text(args[1]), StringComparison.Ordinal)),
            "starts_with" => Bool(first.StartsWith(Text(args[1]), StringComparison.Ordinal)),
            "ends_with" => Bool(first.EndsWith(Text(args[1]), StringComparison.Ordinal)),
            "concat" => new StringValue(first + Text(args[1])),
            "trim" => new StringValue(first.Trim()),
            "trim_start" => new StringValue(first.TrimStart()),
            "trim_end" => new StringValue(first.TrimEnd()),
            "to_lowercase" => new StringValue(UnicodeCasing.ToLower(first)),
            "to_uppercase" => new StringValue(UnicodeCasing.ToUpper(first)),
            "replace" => new StringValue(first.Replace(Text(args[1]), Text(args[2]), StringComparison.Ordinal)),
            "split" => new RangeValue(Split(first, Text(args[1])).Select(part => (Value)new StringValue(part)), TypeRef.String),
            "empty" => Bool(first.Length == 0),
            "equal" => Bool(first == Text(args[1])),
            _ => ExtendedStringOperation(name, args)
        };
    }

    private static IEnumerable<string> Split(string text, string separator)
    {
        if (separator.Length != 0) return text.Split(separator, StringSplitOptions.None);
        return Enumerable.Repeat("", 1).Concat(text.EnumerateRunes().Select(rune => rune.ToString())).Append("");
    }

    private static Value BooleanOperation(string name, IReadOnlyList<Value> args) => name switch
    {
        "identity" => Bool(Boolean(args[0])),
        "negate" => Bool(!Boolean(args[0])),
        "both" => Bool(Boolean(args[0]) && Boolean(args[1])),
        "either" => Bool(Boolean(args[0]) || Boolean(args[1])),
        "xor" => Bool(Boolean(args[0]) != Boolean(args[1])),
        "implies" => Bool(!Boolean(args[0]) || Boolean(args[1])),
        _ => throw new MsyncException($"Unknown bool native '{name}'")
    };

    private static Value PathOperation(string name, IReadOnlyList<Value> args, RuntimeContext context) => name switch
    {
        "identity" => context.AsPath(args[0]),
        "join" => PathSemantics.JoinValue(context.AsPath(args[0]), args[1]),
        "qualify" => args[0] is ConnectionValue connection ? PathSemantics.Qualify(context.AsPath(args[1]), connection) : throw new MsyncException("path.qualify expects Connection"),
        _ => throw new MsyncException($"Unknown path native '{name}'")
    };

    private static Value ProcessOperation(string name, IReadOnlyList<Value> args)
    {
        ProcessValue process = args[0] as ProcessValue ?? throw new MsyncException("Expected Process");
        return name switch
        {
            "stdout" => new StringValue(process.StandardOutput),
            "stderr" => new StringValue(process.StandardError),
            "success" => Bool(process.ExitCode == 0),
            "exit_code" => new NumberValue(process.ExitCode),
            _ => throw new MsyncException($"Unknown process native '{name}'")
        };
    }

    private static bool LocalWindows(PathValue path) => OperatingSystem.IsWindows() && path.Connection?.Kind is not (ConnectionKind.Ssh or ConnectionKind.Wsl);
}
