namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterRanges(IDictionary<string, NativeSignature> result)
    {
        TypeRef range = new(TypeKind.Range);
        void Add(string name, NativeResultRule rule, params TypeRef[] parameters) =>
            result.Add("ranges." + name, new("ranges." + name, parameters, range, ResultRule: rule,
                Behavior: NativeBehavior.Deferred | (name is "enumerate_from" or "chunk" or "slide" or "repeat" ? NativeBehavior.MayFail : NativeBehavior.None)));
        Add("concat", NativeResultRule.RangeConcat, range, range);
        Add("flatten", NativeResultRule.RangeFlatten, range);
        Add("zip", NativeResultRule.RangeZip, range, range);
        Add("enumerate_from", NativeResultRule.RangeEnumerate, range, TypeRef.Number);
        Add("chunk", NativeResultRule.RangeNested, range, TypeRef.Number);
        Add("slide", NativeResultRule.RangeNested, range, TypeRef.Number);
        Add("materialize", NativeResultRule.FirstArgument, range);
        Add("sort", NativeResultRule.FirstArgument, range);
        Add("sort_with", NativeResultRule.FirstArgument, range, TypeRef.Function);
        Add("scan", NativeResultRule.RangeScan, range, TypeRef.Any, TypeRef.Function);
        Add("repeat", NativeResultRule.RangeRepeat, TypeRef.Any, TypeRef.Number);
        result.Add("ranges.at", new("ranges.at", [range, TypeRef.Number], TypeRef.Any,
            ResultRule: NativeResultRule.RangeItem, Behavior: NativeBehavior.Enumerates | NativeBehavior.MayFail));
        result.Add("ranges.sequence", new("ranges.sequence", [TypeRef.Number, TypeRef.Number, TypeRef.Number], new(TypeKind.Range, TypeRef.Number),
            Behavior: NativeBehavior.Deferred | NativeBehavior.MayFail));
    }
}
