using System.Collections.Concurrent;
using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static readonly ConcurrentDictionary<string, NativeBuiltinOperation> operations = new(StringComparer.Ordinal);

    public static NativeBuiltinOperation Resolve(string name) => operations.GetOrAdd(name, static symbol =>
    {
        if (!NativeSignatures.TryGet(symbol, out Msync.Core.NativeSignature? signature))
            throw new MsyncException($"Unknown native function '{symbol}'");
        int separator = symbol.IndexOf('.');
        string group = separator < 0 ? "" : symbol[..separator];
        string member = symbol[(separator + 1)..];
        NativeBuiltinHandler implementation = group switch
        {
            "" when symbol == "cast" => (args, context, _, _) => new(CastValue(args[0], args[1].AsType(), context)),
            "fs" => (args, context, _, token) => FileSystemAsync(member, args, context, token, signature.Effect),
            "io" => (args, context, _, token) => IOOperationAsync(member, args, context, token),
            "process" => (args, context, _, token) => ProcessOperationAsync(member, args, context, token),
            "math" => (args, _, _, _) => new(MathOperation(member, args)),
            "bits" => (args, _, _, _) => new(NativeValue.FromNumber(Word32.Evaluate(signature.Numeric,
                Number(args[0]), args.Count == 2 ? Number(args[1]) : 0))),
            "bool" => (args, _, _, _) => new(BooleanOperation(member, args)),
            "string" => (args, _, _, token) => StringOperationAsync(member, args, token),
            "path" => (args, context, _, _) => new(PathOperation(member, args, context)),
            "ranges" => (args, _, invoke, token) => RangeOperationAsync(member, args, invoke, token),
            "regex" => (args, _, _, _) => new(RegexOperation(member, args)),
            "text" => (args, _, _, token) => TextOperationAsync(member, args, token),
            "environment" => (args, context, _, _) => new(EnvironmentOperation(member, args, context)),
            "meta" => (args, context, _, _) => new(MetaOperation(member, args, context)),
            "shell" => (args, context, _, token) => new(ShellOperation(member, args, context, token)),
            _ => throw new MsyncException($"Native namespace '{group}' is not registered")
        };
        return new(signature, implementation);
    });

    public static ValueTask<NativeValue> InvokeAsync(string name, IReadOnlyList<NativeValue> arguments,
        NativeRuntimeContext context,
        Func<NativeValue, IReadOnlyList<NativeValue>, CancellationToken, ValueTask<NativeValue>> invoke,
        CancellationToken token = default) => InvokeAsync(Resolve(name), arguments, context, invoke, token);

    public static ValueTask<NativeValue> InvokeAsync(NativeBuiltinOperation operation, IReadOnlyList<NativeValue> arguments,
        NativeRuntimeContext context,
        Func<NativeValue, IReadOnlyList<NativeValue>, CancellationToken, ValueTask<NativeValue>> invoke,
        CancellationToken token = default)
    {
        Msync.Core.NativeSignature signature = operation.Signature;
        if (signature.Variadic ? arguments.Count < signature.Parameters.Count : arguments.Count != signature.Parameters.Count)
            throw new MsyncException($"Native '{signature.Name}' expects {(signature.Variadic ? "at least " : "")}{signature.Parameters.Count} arguments, received {arguments.Count}");
        return InvokeProducedAsync(operation, arguments, context, invoke, token);
    }

    public static ValueTask<NativeValue> InvokeProducedAsync(NativeBuiltinOperation operation, IReadOnlyList<NativeValue> arguments,
        NativeRuntimeContext context,
        Func<NativeValue, IReadOnlyList<NativeValue>, CancellationToken, ValueTask<NativeValue>> invoke,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        Msync.Core.NativeSignature signature = operation.Signature;
        if (context.DryRun && signature.Name != "process.spawn" && signature.Effect == NativeEffect.Write
            && (signature.Behavior.GetValueOrDefault() & NativeBehavior.LocalMemory) == 0)
            return new(signature.Result == TypeRef.Bool ? NativeValue.True : NativeValue.Null);
        return operation.Implementation(arguments, context, invoke, token);
    }

    internal static string Text(NativeValue value) => value.Kind switch
    {
        NativeKind.String => value.AsString(),
        NativeKind.Path => value.AsPath().NativeText,
        _ => throw new MsyncException($"Expected String or Path, received {value.Type}")
    };
    internal static double Number(NativeValue value) => value.Kind == NativeKind.Number ? value.AsNumber()
        : throw new MsyncException($"Expected Number, received {value.Type}");
    internal static bool Boolean(NativeValue value) => value.Kind == NativeKind.Bool ? value.AsBool()
        : throw new MsyncException($"Expected Bool, received {value.Type}");
    internal static NativeValue Bool(bool value) => NativeValue.FromBool(value);
    internal static NativeRange Range(NativeValue value, string operation) => value.Kind == NativeKind.Range ? value.AsRange()
        : throw new MsyncException($"ranges.{operation} expects Range, received {value.Type}");

    internal static NativeValue Record(NativeLayout layout, params NativeValue[] values)
    {
        NativeAllocation allocation = new(layout);
        for (int index = 0; index < values.Length; index++) allocation.Field(index).Store(values[index]);
        return NativeValue.FromAddress(allocation.Address);
    }

    // Reflection is an explicit library operation, not the executor's field ABI.
    internal static bool TryReflectField(NativeValue value, string name, out NativeValue result)
    {
        NativeAddress address = value.AsAddress();
        for (int index = 0; index < address.Allocation.Layout.Count; index++)
            if (address.Allocation.Layout[index].Name == name) { result = address.Field(index).Load(); return true; }
        result = NativeValue.Null;
        return false;
    }
}

public delegate ValueTask<NativeValue> NativeBuiltinHandler(IReadOnlyList<NativeValue> arguments,
    NativeRuntimeContext context,
    Func<NativeValue, IReadOnlyList<NativeValue>, CancellationToken, ValueTask<NativeValue>> invoke,
    CancellationToken token);

public sealed class NativeBuiltinOperation
{
    internal NativeBuiltinOperation(Msync.Core.NativeSignature signature, NativeBuiltinHandler implementation) =>
        (Signature, Implementation) = (signature, implementation);
    public Msync.Core.NativeSignature Signature { get; }
    internal NativeBuiltinHandler Implementation { get; }
}
