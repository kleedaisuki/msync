using Msync.Core;
using Msync.Core.Native;

namespace Msync.Runtime.Native;

public static partial class NativeBuiltins
{
    private static readonly NativeLayout SignatureLayout = NativeBuiltinLayouts.Signature.Layout;
    private static readonly NativeLayout MemberLayout = NativeBuiltinLayouts.Member.Layout;

    private static NativeValue MetaOperation(string operation, IReadOnlyList<NativeValue> args, NativeRuntimeContext context)
    {
        if (operation is "has_member" or "member_type" or "member_names" or "member_count" or "members_of")
            return MetaMembers(operation, args, context);
        if (operation is "parameter_count" or "parameter_types" or "is_variadic" or "signature_of"
            or "is_invocable" or "is_invocable_r" or "invoke_result" or "try_invoke_result")
            return MetaCallable(operation, args, context);
        if (operation == "common_type_of")
            return NativeValue.FromType(args.Aggregate(new TypeRef(TypeKind.Never),
                static (type, value) => TypeRef.Union(type, value.AsType())));
        return MetaTypes.Evaluate(operation, args[0].AsType(), args.Count > 1 ? args[1].AsType() : null);
    }

    private static NativeCallableDescription? CallableDescription(NativeValue value, NativeRuntimeContext context)
    {
        if (value.Kind is NativeKind.Path or NativeKind.String) return new([], TypeRef.Process, true);
        if (value.Kind != NativeKind.Function) return null;
        NativeFunction function = value.AsFunction();
        if (function.NativeName is { } name && NativeSignatures.TryGet(name, out Msync.Core.NativeSignature? native))
            return new(native.Parameters, native.Result, native.Variadic);
        return context.DescribeFunction?.Invoke(function);
    }

    private static NativeValue MetaCallable(string operation, IReadOnlyList<NativeValue> args, NativeRuntimeContext context)
    {
        int offset = operation == "is_invocable_r" ? 1 : 0;
        NativeCallableDescription? signature = CallableDescription(args[offset], context);
        if (operation is "is_invocable" or "is_invocable_r" or "invoke_result" or "try_invoke_result")
        {
            int count = args.Count - offset - 1;
            bool valid = signature is not null && (signature.Variadic ? count >= signature.Parameters.Count : count == signature.Parameters.Count);
            for (int index = 0; valid && index < Math.Min(count, signature!.Parameters.Count); index++)
            {
                NativeValue argument = args[index + offset + 1];
                TypeRef actual = argument.Kind == NativeKind.Type ? argument.AsType() : context.DescribeValue?.Invoke(argument) ?? argument.Type;
                valid = MetaTypes.IsConvertible(actual, signature.Parameters[index]);
            }
            if (operation == "is_invocable_r") valid &= signature is not null && MetaTypes.IsConvertible(signature.Result, args[0].AsType());
            if (operation is "is_invocable" or "is_invocable_r") return Bool(valid);
            if (valid) return NativeValue.FromType(signature!.Result);
            return operation == "try_invoke_result" ? NativeValue.Null : throw new MsyncException("Cannot infer the result of this invocation.");
        }
        return operation switch
        {
            "parameter_count" => signature is null ? NativeValue.Null : NativeValue.FromNumber(signature.Parameters.Count),
            "parameter_types" => signature is null ? NativeValue.Null : NativeValue.FromRange(context.Snapshot(signature.Parameters.Select(NativeValue.FromType), TypeRef.Type)),
            "is_variadic" => Bool(signature?.Variadic == true),
            "signature_of" => signature is null ? NativeValue.Null : Record(context, SignatureLayout,
                NativeValue.FromRange(context.Snapshot(signature.Parameters.Select(NativeValue.FromType), TypeRef.Type)),
                NativeValue.FromNumber(signature.Parameters.Count), NativeValue.FromType(signature.Result), NativeValue.True, Bool(signature.Variadic)),
            _ => throw new MsyncException($"Unknown callable meta native '{operation}'")
        };
    }

    private static NativeValue MetaMembers(string operation, IReadOnlyList<NativeValue> args, NativeRuntimeContext context)
    {
        NativeValue receiver = args[0];
        NativeAddress address = receiver.Kind == NativeKind.Address ? receiver.AsAddress() : default;
        IReadOnlyList<NativeFieldLayout> fields = address.IsNull ? [] : address.Allocation.Layout.Fields;
        int[] ordered = Enumerable.Range(0, fields.Count).OrderBy(index => fields[index].Name, StringComparer.Ordinal).ToArray();
        if (operation == "member_count") return NativeValue.FromNumber(fields.Count);
        if (operation == "member_names") return NativeValue.FromRange(context.Snapshot(
            ordered.Select(index => NativeValue.FromString(fields[index].Name)), TypeRef.String));
        if (operation == "members_of") return NativeValue.FromRange(context.Snapshot(ordered.Select(index =>
        {
            NativeValue value = address.Field(index).Load();
            TypeRef storage = context.DescribeField?.Invoke(address, index).StorageType ?? SourceType(fields[index].Type);
            return Record(context, MemberLayout, NativeValue.FromString(fields[index].Name),
                NativeValue.FromType(context.DescribeValue?.Invoke(value) ?? value.Type), NativeValue.FromType(storage), Bool(storage.Nullable));
        }), TypeRef.Scope));
        string name = Text(args[1]);
        int found = ordered.FirstOrDefault(index => fields[index].Name == name, -1);
        return operation == "has_member" ? Bool(found >= 0)
            : found < 0 ? NativeValue.Null : NativeValue.FromType(context.DescribeValue?.Invoke(address.Field(found).Load()) ?? address.Field(found).Load().Type);
    }

    private static TypeRef SourceType(NativeType type) => new(type.Alternatives.Aggregate(TypeKind.Never, static (kind, alternative) => kind | NativeValue.TypeOfKind(alternative).Kind));
}
