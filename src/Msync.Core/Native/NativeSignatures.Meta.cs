namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterMeta(IDictionary<string, NativeSignature> signatures)
    {
        void Add(string names, TypeRef result, params TypeRef[] parameters)
        {
            foreach (string name in names.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                signatures.Add("meta." + name, new("meta." + name, parameters, result, Behavior: NativeBehavior.None));
        }
        Add("is_range is_union is_scalar", TypeRef.Bool, TypeRef.Type);
        Add("is_same accepts is_convertible is_castable", TypeRef.Bool, TypeRef.Type, TypeRef.Type);
        Add("add_null remove_null range_of", TypeRef.Type, TypeRef.Type);
        Add("common_type", TypeRef.Type, TypeRef.Type, TypeRef.Type);
        signatures.Add("meta.common_type_of", new("meta.common_type_of", [TypeRef.Type], TypeRef.Type, Variadic: true, Behavior: NativeBehavior.None));
        Add("element_type", TypeRef.Type.WithNull, TypeRef.Type);
        Add("range_rank", TypeRef.Number, TypeRef.Type);
        Add("type_name", TypeRef.String, TypeRef.Type);
        Add("alternatives", new(TypeKind.Range, TypeRef.Type), TypeRef.Type);
        signatures.Add("meta.is_invocable", new("meta.is_invocable", [TypeRef.Any], TypeRef.Bool, Variadic: true, Behavior: NativeBehavior.None));
        signatures.Add("meta.is_invocable_r", new("meta.is_invocable_r", [TypeRef.Type, TypeRef.Any], TypeRef.Bool, Variadic: true, Behavior: NativeBehavior.None));
        signatures.Add("meta.invoke_result", new("meta.invoke_result", [TypeRef.Any], TypeRef.Type, Variadic: true, Behavior: NativeBehavior.None));
        signatures.Add("meta.try_invoke_result", new("meta.try_invoke_result", [TypeRef.Any], TypeRef.Type.WithNull, Variadic: true, Behavior: NativeBehavior.None));
        Add("parameter_count", TypeRef.Number.WithNull, TypeRef.Any);
        Add("parameter_types", new TypeRef(TypeKind.Range, TypeRef.Type).WithNull, TypeRef.Any);
        Add("is_variadic", TypeRef.Bool, TypeRef.Any);
        Add("signature_of", TypeRef.Scope.WithNull, TypeRef.Any);
        Add("has_member", TypeRef.Bool, TypeRef.Any, TypeRef.String);
        Add("member_type", TypeRef.Type.WithNull, TypeRef.Any, TypeRef.String);
        Add("member_names", new(TypeKind.Range, TypeRef.String), TypeRef.Any);
        Add("member_count", TypeRef.Number, TypeRef.Any);
        Add("members_of", new(TypeKind.Range, TypeRef.Scope), TypeRef.Any);
    }
}
