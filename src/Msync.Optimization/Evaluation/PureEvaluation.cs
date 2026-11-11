namespace Msync.Optimization;

internal static class PureEvaluation
{
    internal static bool IsContextNative(string? name) => name is "path.identity" or "path.join" or "path.qualify";

    internal static PathValue? Qualify(ConnectionValue connection, PathValue path, BindingContext? context)
    {
        if (connection.Kind is not (ConnectionKind.Local or ConnectionKind.Ssh or ConnectionKind.Wsl)) return null;
        if (connection.Kind != ConnectionKind.Local || connection.Address.Length == 0) return PathSemantics.Qualify(path, connection);
        if (context is null) return null;
        if (connection.Address == "pwd" && path.Raw == "pwd")
            return new(context.WorkingDirectory, context.WorkingDirectory, HomeDirectory: context.HomeDirectory);
        if (connection.Address == "file")
        {
            if (context.SourceFile is not { } source) return null;
            path = path.WithRaw(path.Raw switch
            {
                "this" => source,
                "parent" => System.IO.Path.GetDirectoryName(source)!,
                _ => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(source)!, path.NativeText)
            });
        }
        return PathSemantics.Qualify(path, connection) with
        { WorkingDirectory = path.WorkingDirectory ?? context.WorkingDirectory, HomeDirectory = path.HomeDirectory ?? context.HomeDirectory };
    }

    internal static Value? Native(string? name, IReadOnlyList<Value> arguments, Module module)
    {
        Value Bool(bool result) => result ? Value.True : Value.False;
        if (arguments.Count > 0 && arguments[0] is BoolValue first)
        {
            var second = arguments.Count > 1 && arguments[1] is BoolValue boolean ? boolean : null;
            return name switch
            {
                "bool.identity" => first,
                "bool.negate" => Bool(!first.Data),
                "bool.both" when second is not null => Bool(first.Data && second.Data),
                "bool.either" when second is not null => Bool(first.Data || second.Data),
                "bool.xor" when second is not null => Bool(first.Data != second.Data),
                "bool.implies" when second is not null => Bool(!first.Data || second.Data),
                _ => null
            };
        }
        if (arguments.Count > 0 && arguments[0] is StringValue text)
        {
            var second = arguments.Count > 1 && arguments[1] is StringValue next ? next : null;
            return name switch
            {
                "string.contains" when second is not null => Bool(text.Data.Contains(second.Data, StringComparison.Ordinal)),
                "string.starts_with" when second is not null => Bool(text.Data.StartsWith(second.Data, StringComparison.Ordinal)),
                "string.ends_with" when second is not null => Bool(text.Data.EndsWith(second.Data, StringComparison.Ordinal)),
                "string.equal" when second is not null => Bool(text.Data == second.Data),
                "string.concat" when second is not null => new StringValue(text.Data + second.Data),
                "string.trim" => new StringValue(text.Data.Trim()),
                "string.trim_start" => new StringValue(text.Data.TrimStart()),
                "string.trim_end" => new StringValue(text.Data.TrimEnd()),
                "string.empty" => Bool(text.Data.Length == 0),
                "string.replace" when second is { Data.Length: > 0 } && arguments.Count == 3 && arguments[2] is StringValue replacement
                    => new StringValue(text.Data.Replace(second.Data, replacement.Data, StringComparison.Ordinal)),
                _ => null
            };
        }
        return ContextualNative(name, arguments, module);
    }

    internal static Value? ContextualNative(string? name, IReadOnlyList<Value> arguments, Module module)
    {
        if (module.Stage != ArtifactStage.Executable || module.Context is not { } context || !IsContextNative(name)) return null;
        PathValue? Path(Value value) => value switch
        {
            PathValue path => path,
            StringValue spelling => new(spelling.Data, context.WorkingDirectory, HomeDirectory: context.HomeDirectory),
            _ => null
        };
        return name switch
        {
            "path.identity" when arguments.Count == 1 => Path(arguments[0]),
            "path.join" when arguments.Count == 2 && Path(arguments[0]) is { } path && arguments[1] is PathValue or StringValue
                => PathSemantics.JoinValue(path, arguments[1]),
            "path.qualify" when arguments.Count == 2 && arguments[0] is ConnectionValue connection && Path(arguments[1]) is { } path
                => PathSemantics.Qualify(path, connection),
            _ => null
        };
    }
}
