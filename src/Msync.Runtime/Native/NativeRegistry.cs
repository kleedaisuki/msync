using System.Collections.Concurrent;
using Msync.Core;

namespace Msync.Runtime;

public static partial class NativeRegistry
{
    internal sealed record Operation(NativeSignature Signature, string Group, string Member,
        InstructionEffect Effects, bool ImmediateExternal);

    private static readonly ConcurrentDictionary<string, Operation> operations = new(StringComparer.Ordinal);

    private static Operation Describe(NativeSignature signature)
    {
        string name = signature.Name;
        int separator = name.IndexOf('.');
        InstructionEffect effects = InstructionEffects.NativeEffects(name);
        // Enumeration/callback effects are not immediate native I/O. The
        // selected producer or callback owns its own scheduling dependencies.
        bool external = !NativeEvaluation.DefersFileRead(name) && signature.Effect != NativeEffect.Pure
            && (signature.Behavior.GetValueOrDefault() & NativeBehavior.LocalMemory) == 0;
        return new(signature, separator < 0 ? "" : name[..separator], name[(separator + 1)..], effects,
            external);
    }

    internal static Operation GetOperation(string name) => operations.GetOrAdd(name, static key =>
        NativeSignatures.TryGet(key, out var signature) ? Describe(signature)
            : throw new MsyncException($"Unknown native function '{key}'"));

    public static ValueTask<Value> InvokeAsync(string name, IReadOnlyList<Value> arguments, RuntimeContext context,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke, CancellationToken cancellationToken = default) =>
        InvokeAsync(GetOperation(name), arguments, context, invoke, cancellationToken);

    internal static async ValueTask<Value> InvokeAsync(Operation native, IReadOnlyList<Value> arguments, RuntimeContext context,
        Func<Value, IReadOnlyList<Value>, CancellationToken, ValueTask<Value>> invoke, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        NativeSignature signature = native.Signature;
        if (!signature.Variadic && arguments.Count != signature.Parameters.Count)
            throw new MsyncException($"Native '{signature.Name}' expects {signature.Parameters.Count} arguments, received {arguments.Count}");
        if (context.DryRun && signature.Effect == NativeEffect.Write
            && (signature.Behavior.GetValueOrDefault() & NativeBehavior.LocalMemory) == 0)
            return signature.Result == TypeRef.Bool ? Value.True : Value.Null;
        if (signature.Name == "cast") return CastOperation(arguments, context);
        string operation = native.Member;
        Value result = native.Group switch
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
            _ => throw new MsyncException($"Native namespace '{native.Group}' is not registered")
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
