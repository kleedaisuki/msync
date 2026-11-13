namespace Msync.Core.Native;

public static partial class NativeLegacyV6Importer
{
    private sealed partial class Importer
    {
        private sealed partial class FunctionImport
        {
            private Item ProjectedField(Item receiver, int projection, Item? assigned)
            {
                string name = owner.Module.Projections[projection].Name;
                if (owner.Types[receiver.Type].Kind == TypeKind.Process)
                {
                    int member = owner.Type(name switch { "stdout" or "stderr" => TypeKind.String,
                        "exit_code" => TypeKind.Number, "success" => TypeKind.Bool, _ => TypeKind.Never });
                    return Result(member, new(NativeOp.CallNative, Symbol: "process." + name, Inputs: [receiver.Register]));
                }
                IReadOnlyList<int>? domain = owner.Types[receiver.Type].Layouts;
                NativeProjectionCase[] cases = owner.Cases(projection)
                    .Where(item => domain is not { Count: > 0 } || domain.Contains(item.Layout)).ToArray();
                int type = assigned is not null ? NullType : cases.Select(item => owner.PayloadType(item.Layout, item.Field))
                    .Aggregate(owner.Type(TypeKind.Never), owner.Join);
                if (cases.Length == 0)
                {
                    Add(new(NativeOp.Unreachable, Symbol: $"Namespace has no member '{name}'"));
                    return NullItem();
                }
                Item Read(NativeProjectionCase selected)
                {
                    Item scope = As(receiver, owner.Scope(selected.Layout));
                    Item address = FieldAddress(scope, selected.Layout, selected.Field);
                    int payload = owner.PayloadType(selected.Layout, selected.Field);
                    if (owner.IndirectFields.Contains((selected.Layout, selected.Field)))
                        address = Result(owner.Pointer(payload), new(NativeOp.Load, A: address.Register));
                    if (assigned is null) return Result(payload, new(NativeOp.Load, A: address.Register));
                    Item value = As(assigned, payload);
                    Add(new(NativeOp.Store, A: address.Register, B: value.Register, C: 0, Type: payload));
                    return NullItem();
                }
                Item result;
                if (cases.Length == 1 && domain is { Count: 1 } && owner.Types[receiver.Type].Kind == TypeKind.Scope)
                    result = Read(cases[0]);
                else
                {
                    int register = Register(type); List<int> exits = [];
                    foreach (NativeProjectionCase selected in cases)
                    {
                        Item matches = Result(BoolType, new(NativeOp.Unary, A: receiver.Register,
                            B: selected.Layout, Immediate: (long)NativeUnary.HasLayout));
                        int branch = Branch(matches), body = Label;
                        Item value = Read(selected); Add(new(NativeOp.Copy, register, value.Register));
                        exits.Add(Jump()); BranchTo(branch, body, Label);
                    }
                    Add(new(NativeOp.Unreachable, Symbol: $"Namespace has no member '{name}' in its admitted layouts"));
                    foreach (int exit in exits) JumpTo(exit, Label);
                    result = new(type, Register: register);
                }
                var facts = cases.Select(item => owner.FieldFacts.GetValueOrDefault((item.Layout, item.Field), (-1, -1))).Distinct().ToArray();
                return facts.Length == 1 ? result with { Function = facts[0].Item1, Environment = facts[0].Item2 } : result;
            }
        }
    }
}
