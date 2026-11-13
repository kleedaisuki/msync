namespace Msync.Core.Native;

public static partial class NativeLegacyV6Importer
{
    private sealed partial class Importer
    {
        private sealed partial class FunctionImport
        {
            private StorageShape[] HeaderAlternatives(Item value)
            {
                NativeCodeType type = owner.Types[value.Type];
                if ((type.Kind & TypeKind.Scope) == 0) return [];
                return owner.storageShapes.Values.Where(shape => type.Layouts is not { Count: > 0 }
                    || type.Layouts.Contains(shape.Header)).ToArray();
            }
            private Item LayoutSwitch(Item receiver, StorageShape[] alternatives, int type,
                Func<StorageShape, Item> selected, Func<Item> fallback)
            {
                int result = Register(type); List<int> exits = [];
                foreach (StorageShape shape in alternatives)
                {
                    Item matches = Result(BoolType, new(NativeOp.Unary, A: receiver.Register, B: shape.Header,
                        Immediate: (long)NativeUnary.HasLayout));
                    int branch = Branch(matches), body = Label;
                    Item value = selected(shape); Add(new(NativeOp.Copy, result, value.Register));
                    exits.Add(Jump()); BranchTo(branch, body, Label);
                }
                Item remaining = fallback(); Add(new(NativeOp.Copy, result, remaining.Register));
                foreach (int exit in exits) JumpTo(exit, Label);
                return new(type, Register: result);
            }
            private Item HeaderDescription(Item actual, StorageShape shape)
            {
                Item storage = As(actual, shape.Type);
                Item element = MemoryRead(storage, shape.Header, 8);
                return Result(owner.Type(TypeKind.Type), new(NativeOp.MakeType, A: element.Register, Immediate: (ushort)TypeKind.Storage));
            }
            private bool StorageMetadata(string name, Item[] arguments, out Item result)
            {
                if (name == "cast")
                {
                    StorageShape[] headers = HeaderAlternatives(arguments[0]);
                    if (headers.Length == 0) { result = null!; return false; }
                    Item ConvertHeader(StorageShape shape)
                    {
                        Item described = HeaderDescription(arguments[0], shape);
                        Item matches = Result(BoolType, new(NativeOp.Unary, A: described.Register,
                            B: arguments[1].Register, Immediate: (long)NativeUnary.IsType));
                        Check(matches, $"Cannot explicitly convert Storage<{SourceType(shape.Element)}> to {arguments[1].Descriptor?.ToString() ?? "the requested Type"}");
                        return arguments[0];
                    }
                    NativeCodeType input = owner.Types[arguments[0].Type];
                    bool onlyHeaders = (input.Kind & ~(TypeKind.Scope | TypeKind.Null)) == 0
                        && input.Layouts is { Count: > 0 } && input.Layouts.All(layout => headers.Any(shape => shape.Header == layout));
                    int castType = onlyHeaders ? arguments[0].Type : owner.Join(arguments[0].Type, NativeResult(name, arguments));
                    result = LayoutSwitch(arguments[0], headers, castType, ConvertHeader,
                        () => Result(castType, new(NativeOp.CallNative, Symbol: name, Inputs: arguments.Select(item => item.Register).ToArray())));
                    return true;
                }
                if (name is "meta.is_invocable" or "meta.is_invocable_r" or "meta.invoke_result" or "meta.try_invoke_result")
                {
                    int start = name == "meta.is_invocable_r" ? 2 : 1; bool changed = false;
                    for (int argument = start; argument < arguments.Length; argument++)
                    {
                        Item value = arguments[argument]; StorageShape[] headers = HeaderAlternatives(value);
                        if (headers.Length == 0) continue;
                        arguments[argument] = LayoutSwitch(value, headers, owner.Join(value.Type, owner.Type(TypeKind.Type)),
                            shape => HeaderDescription(value, shape), () => value); changed = true;
                    }
                    result = changed ? Result(NativeResult(name, arguments), new(NativeOp.CallNative, Symbol: name,
                        Inputs: arguments.Select(item => item.Register).ToArray())) : null!;
                    return changed;
                }
                if (name is not ("meta.has_member" or "meta.member_type" or "meta.member_names" or "meta.member_count" or "meta.members_of"))
                { result = null!; return false; }
                StorageShape[] alternatives = HeaderAlternatives(arguments[0]);
                if (alternatives.Length == 0) { result = null!; return false; }
                int type = NativeResult(name, arguments);
                Item Empty(StorageShape _) => name switch
                {
                    "meta.has_member" => Boolean(false), "meta.member_type" => NullItem(), "meta.member_count" => Number(0),
                    _ => Result(type, new(NativeOp.MakeRange, Inputs: []))
                };
                result = LayoutSwitch(arguments[0], alternatives, type, Empty,
                    () => Result(type, new(NativeOp.CallNative, Symbol: name, Inputs: arguments.Select(item => item.Register).ToArray())));
                return true;
            }
            private Item ValueDescription(Item value)
            {
                int type = owner.Type(TypeKind.Type);
                Item Ordinary() => Result(type, new(NativeOp.Unary, A: value.Register, Immediate: (long)NativeUnary.ValueType));
                StorageShape[] headers = HeaderAlternatives(value);
                return headers.Length == 0 ? Ordinary() : LayoutSwitch(value, headers, type,
                    shape => HeaderDescription(value, shape), Ordinary);
            }
            private Item TypeTest(Item actual, Item expected)
            {
                Item Test(Item value) => Result(BoolType, new(NativeOp.Unary, A: value.Register,
                    B: expected.Register, Immediate: (long)NativeUnary.IsType));
                StorageShape[] alternatives = HeaderAlternatives(actual);
                if (alternatives.Length == 0) return Test(actual);
                Item Selected(StorageShape shape)
                {
                    return Test(HeaderDescription(actual, shape));
                }
                return LayoutSwitch(actual, alternatives, BoolType, Selected, () => Test(actual));
            }
        }
    }
}
