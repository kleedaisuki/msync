using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using Msync.Compiler;
using Msync.Core;
using Msync.Hir;
using Msync.Runtime;
using Module = Msync.Core.Module;

int checks = 0;
string directory = Path.Combine(Environment.CurrentDirectory, ".temp", "hir-boundary-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    Check(!typeof(HirModule).Assembly.GetReferencedAssemblies().Any(assembly => assembly.Name == "Msync.Compiler"),
        "HIR and lowering must not depend on source binding");
    Check(typeof(HirModule).GetProperties().All(property => property.PropertyType != typeof(Instruction)
        && property.PropertyType != typeof(FunctionCode) && property.PropertyType != typeof(Module)),
        "The semantic module must not wrap an executable module or instruction");

    var cases = new (string Name, string Source, Value Expected)[]
    {
        ("arithmetic", "3 + 2", new NumberValue(5)),
        ("location", "let number = 3; number = number + 2; number", new NumberValue(5)),
        ("nullable-flow", "let number?: Number = 3; if number is not null { number + 2 } else { 0 }", new NumberValue(5)),
        ("nullable-write", "let number?: Number = 3; number = null; number is null", Value.True),
        ("runtime-if", "if exists ./missing { 1 } else { 2 }", new NumberValue(2)),
    };
    foreach (var test in cases)
    {
        if (args.Length != 0 && !args.Contains(test.Name, StringComparer.Ordinal)) continue;
        HirModule semantic = Compiler.Analyze(test.Source, test.Name + ".msync");
        byte[] before = HirBytes(semantic);
        Module first = HirLowerer.Lower(semantic, new(0)).Module;
        Module second = HirLowerer.Lower(semantic, new(0)).Module;
        Check(Bytes(first).AsSpan().SequenceEqual(Bytes(second)), test.Name + ": lowering twice is deterministic");
        Check(before.AsSpan().SequenceEqual(HirBytes(semantic)), test.Name + ": lowering does not mutate semantics");
        Check(first.Functions[first.EntryPoint].ReturnType == test.Expected.Type, test.Name + ": finalized result type");

        using MemoryStream input = new(before, writable: false);
        HirModule loaded = HirArtifact.Read(input);
        Module imported = HirLowerer.Lower(loaded, new(0)).Module;
        Check(Bytes(first).AsSpan().SequenceEqual(Bytes(imported)), test.Name + ": semantic file is a sufficient lowering input");
        Value actual = await Interpreter.ExecuteAsync(imported, new(WorkingDirectory: directory));
        Check(actual == test.Expected, test.Name + ": runtime value");
        Check(!Directory.EnumerateFiles(directory).Any(), test.Name + ": analysis/lowering does not execute external effects");

        if (test.Name == "location")
        {
            Check(semantic.Locations.Any(location => location.Name == "number"), "Source declaration identity remains inspectable");
            Check(semantic.Nodes.Any(node => node.Kind == HirKind.Assign), "Assignment remains a semantic operation before lowering");
            Check(semantic.Nodes.Any(node => node.Kind == HirKind.Binary), "Arithmetic remains a semantic expression before lowering");
        }
        if (test.Name == "runtime-if")
            Check(semantic.Nodes.Any(node => node.Kind == HirKind.If), "Runtime-dependent control flow retains its high-level structure");
        if (test.Name == "nullable-flow")
        {
            HirLocationId location = new(semantic.Locations.FindIndex(item => item.Name == "number"));
            Check(semantic.Locations[location.Value].AdmitsNull
                || semantic.RuntimeType(semantic.Locations[location.Value].StorageType).Nullable,
                "Null admission belongs to the fixed storage type");
            Check(semantic.Nodes.Any(node => node.Kind == HirKind.ReadLocation && node.Data == location.Value
                && semantic.RuntimeType(node.Type) == TypeRef.Number),
                "A guarded value read has a narrower type than its location");
        }
    }

    Console.WriteLine($"PASS {checks} semantic HIR boundary contracts");
    Console.WriteLine("Compiler " + Hash(typeof(Compiler).Assembly));
    Console.WriteLine("HIR " + Hash(typeof(HirModule).Assembly));
}
finally { Directory.Delete(directory, recursive: true); }

void Check(bool condition, string reason)
{
    checks++;
    if (!condition) throw new InvalidOperationException(reason);
}
static byte[] HirBytes(HirModule module)
{
    using MemoryStream stream = new();
    HirArtifact.Write(module, stream);
    return stream.ToArray();
}
static byte[] Bytes(Module module)
{
    using MemoryStream stream = new();
    Artifact.Write(module, stream);
    return stream.ToArray();
}
static string Hash(Assembly assembly) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location)));

internal static class ImmutableArraySearch
{
    internal static int FindIndex<T>(this ImmutableArray<T> values, Func<T, bool> predicate)
    {
        for (int index = 0; index < values.Length; index++)
            if (predicate(values[index])) return index;
        throw new InvalidOperationException("Expected semantic declaration is absent.");
    }
}
