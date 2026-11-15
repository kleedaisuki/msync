namespace Msync.Core.Native;

public static partial class NativeLegacyV6Importer
{
    private sealed partial class Importer
    {
        private sealed partial class FunctionImport
        {
            private Item TypeLiteral(TypeRef description)
            {
                int type = owner.Type(TypeKind.Type);
                return new(type, Register: Constant(new(NativeKind.Type, Type: description), type), Descriptor: description);
            }
            private Item ElementDescription(Item[] items)
            {
                Item result = TypeLiteral(new(TypeKind.Never));
                foreach (Item item in items)
                {
                    Item description;
                    if (HeaderAlternatives(item).Length != 0) description = ValueDescription(item);
                    else description = Result(owner.Type(TypeKind.Type), new(NativeOp.Unary, A: item.Register,
                        Immediate: (long)NativeUnary.ValueType, Symbol: "non-demanding"));
                    result = Result(owner.Type(TypeKind.Type), new(NativeOp.CallNative, Symbol: "meta.common_type",
                        Inputs: [result.Register, description.Register]));
                }
                return result;
            }
            private Item CommonDescription(Item left, Item right) => Result(owner.Type(TypeKind.Type),
                new(NativeOp.CallNative, Symbol: "meta.common_type", Inputs: [left.Register, right.Register]));
            private Item DescriptionOrAny(Item optional)
            {
                int type = owner.Type(TypeKind.Type), result = Register(type);
                Item missing = Result(BoolType, new(NativeOp.Unary, A: optional.Register, Immediate: (long)NativeUnary.IsNull));
                int branch = Branch(missing), absent = Label;
                Add(new(NativeOp.Copy, result, TypeLiteral(TypeRef.Any).Register)); int done = Jump(), present = Label;
                Add(new(NativeOp.Copy, result, As(optional, type).Register)); BranchTo(branch, absent, present); JumpTo(done, Label);
                return new(type, Register: result);
            }
            private Item CallbackDescription(Item function, params Item[] parameters)
            {
                if (function.Function >= 0) return TypeLiteral(owner.ReflectionType(owner.Module.Functions[function.Function].Signature.Result));
                Item optional = Result(owner.Join(owner.Type(TypeKind.Type), NullType), new(NativeOp.CallNative,
                    Symbol: "meta.try_invoke_result", Inputs: new[] { function.Register }.Concat(parameters.Select(item => item.Register)).ToArray()));
                return DescriptionOrAny(optional);
            }
            private Item NestedDescription(Item description)
            {
                Item optional = Result(owner.Join(owner.Type(TypeKind.Type), NullType), new(NativeOp.CallNative,
                    Symbol: "meta.element_type", Inputs: [description.Register]));
                return DescriptionOrAny(optional);
            }
            private int NativeRangeDescription(string name, Item[] arguments, int resultType)
            {
                NativeCodeType result = owner.Types[resultType];
                if (result.Kind != TypeKind.Range || result.Element < 0) return -1;
                NativeResultRule rule = NativeSignatures.Get(name).ResultRule;
                Item description = rule switch
                {
                    NativeResultRule.FirstArgument or NativeResultRule.RangeMergeLeft => RangeElementDescription(arguments[0]),
                    NativeResultRule.RangeOfArguments => ElementDescription(arguments),
                    NativeResultRule.Transform => CallbackDescription(arguments[1], RangeElementDescription(arguments[0])),
                    NativeResultRule.RangeConcat or NativeResultRule.RangeMerge => CommonDescription(RangeElementDescription(arguments[0]), RangeElementDescription(arguments[1])),
                    NativeResultRule.RangeFlatten => NestedDescription(RangeElementDescription(arguments[0])),
                    NativeResultRule.RangeZip or NativeResultRule.RangeEnumerate => TypeLiteral(TypeRef.Scope),
                    NativeResultRule.RangeNested => ValueDescription(arguments[0]),
                    NativeResultRule.RangeRepeat => ValueDescription(arguments[0]),
                    NativeResultRule.RangeOfType => arguments[0],
                    NativeResultRule.RangeScan => CommonDescription(ValueDescription(arguments[1]), CallbackDescription(arguments[2],
                        ValueDescription(arguments[1]), RangeElementDescription(arguments[0]))),
                    _ => TypeLiteral(SourceType(result.Element))
                };
                return description.Register;
            }
        }
    }
}
