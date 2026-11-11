namespace Msync.Core;

public static partial class NativeSignatures
{
    private static void RegisterIOProcess(IDictionary<string, NativeSignature> result)
    {
        TypeRef path = TypeRef.Union(TypeRef.Path, TypeRef.String);
        void Add(string name, TypeRef output, NativeEffect effect, params TypeRef[] inputs) =>
            result.Add(name, new(name, inputs, output, effect));
        Add("io.read_text", TypeRef.String, NativeEffect.Read, path);
        Add("io.read_lines", new(TypeKind.Range, TypeRef.String), NativeEffect.Pure, path);
        Add("io.read_bytes", new(TypeKind.Range, TypeRef.Number), NativeEffect.Pure, path);
        Add("io.write_text", TypeRef.Null, NativeEffect.Write, path, TypeRef.String);
        Add("io.append_text", TypeRef.Null, NativeEffect.Write, path, TypeRef.String);
        Add("io.write_bytes", TypeRef.Null, NativeEffect.Write, path, new(TypeKind.Range, TypeRef.Number));
        Add("io.append_bytes", TypeRef.Null, NativeEffect.Write, path, new(TypeKind.Range, TypeRef.Number));
        Add("io.file_size", TypeRef.Number, NativeEffect.Read, path);
        Add("io.read_line", TypeRef.String.WithNull, NativeEffect.Read);
        Add("io.read_stdin", TypeRef.String, NativeEffect.Read);
        // The range borrows a live input reader and must not become a planner constant.
        result.Add("io.stdin_lines", new("io.stdin_lines", [], new(TypeKind.Range, TypeRef.String),
            NativeEffect.Read, Behavior: NativeBehavior.Deferred));
        foreach (string name in new[] { "io.print", "io.println", "io.eprint", "io.eprintln" })
            Add(name, TypeRef.Null, NativeEffect.Write, TypeRef.String);
        Add("io.flush", TypeRef.Null, NativeEffect.Write);
        Add("io.flush_error", TypeRef.Null, NativeEffect.Write);
        Add("process.spawn", TypeRef.Process, NativeEffect.Write, path, new(TypeKind.Range));
        Add("process.pid", TypeRef.Number, NativeEffect.Read, TypeRef.Process);
        Add("process.running", TypeRef.Bool, NativeEffect.Read, TypeRef.Process);
        Add("process.wait", TypeRef.Process, NativeEffect.Read, TypeRef.Process);
        Add("process.kill", TypeRef.Bool, NativeEffect.Write, TypeRef.Process);
        Add("process.terminate", TypeRef.Bool, NativeEffect.Write, TypeRef.Process);
        Add("process.write_stdin", TypeRef.Null, NativeEffect.Write, TypeRef.Process, TypeRef.String);
        Add("process.close_stdin", TypeRef.Null, NativeEffect.Write, TypeRef.Process);
        foreach (string name in new[] { "io.read_lines", "io.read_bytes" })
            result[name] = result[name] with { Behavior = NativeBehavior.Deferred };
        foreach (string name in new[] { "io.write_bytes", "io.append_bytes", "process.spawn" })
            result[name] = result[name] with { Behavior = NativeBehavior.Enumerates | NativeBehavior.MayFail };
        foreach (string name in new[] { "io.read_text", "io.write_text", "io.append_text", "io.file_size",
            "process.pid", "process.running", "process.wait", "process.kill", "process.terminate", "process.write_stdin", "process.close_stdin" })
            result[name] = result[name] with { Behavior = NativeBehavior.MayFail };
    }
}
