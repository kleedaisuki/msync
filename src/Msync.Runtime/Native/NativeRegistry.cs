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
        if (context.DryRun && signature.Effect == NativeEffect.Write
            && (signature.Behavior.GetValueOrDefault() & NativeBehavior.LocalMemory) == 0)
            return signature.Result == TypeRef.Bool ? Value.True : Value.Null;
        if (name == "cast") return CastOperation(arguments, context);
        int separator = name.IndexOf('.');
        string group = separator < 0 ? "" : name[..separator];
        string operation = separator < 0 ? name : name[(separator + 1)..];
        Value result = group switch
        {
            "fs" => await FileSystemAsync(operation, arguments, context, cancellationToken, signature.Effect),
            "io" => await IOOperationAsync(operation, arguments, context, cancellationToken),
            "math" => MathOperation(operation, arguments),
            "meta" => MetaOperation(operation, arguments),
            "string" => await StringOperationAsync(operation, arguments, cancellationToken),
            "bool" => BooleanOperation(operation, arguments),
            "path" => PathOperation(operation, arguments, context),
            "ranges" => await RangeOperationAsync(operation, arguments, invoke, cancellationToken),
            "process" => await ProcessOperationAsync(operation, arguments, cancellationToken),
            "environment" => EnvironmentOperation(operation, arguments, context),
            "regex" => RegexOperation(operation, arguments),
            "text" => await TextBuilderOperations.InvokeAsync(operation, arguments, cancellationToken),
            "storage" => await IndexedStorageOperations.InvokeAsync(operation, arguments, context, cancellationToken),
            "shell" => ShellOperation(operation, arguments, context, cancellationToken),
            _ => throw new MsyncException($"Native namespace '{group}' is not registered")
        };
        RetainRangeInputs(result, arguments, signature);
        return result;
    }

    private static void RetainRangeInputs(Value result, IReadOnlyList<Value> arguments, NativeSignature signature)
    {
        if (result is not RangeValue { RetainedValues: null } range
            || (signature.Behavior.GetValueOrDefault() & NativeBehavior.EagerRange) != 0
            || signature.ResultRule == NativeResultRule.RangeItem) return;
        bool retained = false;
        foreach (var value in arguments)
        {
            if (ReferenceEquals(value, result)) return;
            retained |= value is RangeValue or ScopeValue or FunctionValue or ProcessValue or CellValue;
        }
        if (retained) range.RetainedValues = arguments;
    }

    private static string Text(Value value) => value switch
    {
        StringValue text => text.Data,
        PathValue path => path.NativeText,
        _ => throw new MsyncException($"Expected String or Path, received {value.Type}")
    };
    private static bool Boolean(Value value) => value is BoolValue boolean ? boolean.Data : throw new MsyncException($"Expected Bool, received {value.Type}");
    private static Value Bool(bool value) => value ? Value.True : Value.False;
}
