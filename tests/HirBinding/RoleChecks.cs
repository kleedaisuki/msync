using Msync.Compiler.Semantics;
using Msync.Hir;

internal static class RoleChecks
{
    internal static void Run()
    {
        SemanticInputRoles roles = new();
        var first = roles.Root(0);
        var second = roles.Root(1);
        Check(first == roles.Root(0), "root roles are interned");
        Check(first != second, "distinct input roots retain separate roles");
        Check(roles.Field(first, "item") == roles.Field(first, "item"), "field roles are interned");
        Check(roles.Field(first, "item") != roles.Field(second, "item"), "field roots remain distinct");
        Check(roles.Element(first) != roles.Field(first, "[]"), "edge kinds do not collide");
        Check(roles.Capture(new(7)) == roles.Capture(new(7)), "cell capture roles are canonical");
        Check(roles.Capture(new(7)) != roles.ValueCapture(first, new(7)), "value captures retain their selection role");
        Check(roles.ValueCapture(first, new(7)) != roles.ValueCapture(second, new(7)), "value capture roots remain distinct");
        Check(roles.Alternative(first, new(4)) != roles.Alternative(first, new(5)), "type alternatives retain identity");
        Check(roles.WriteValue(first) != roles.WriteElement(first), "write phases remain distinct");
        Check(roles.ExternalResource(new(4)) == roles.ExternalResource(new(4)), "external resource roles are interned");
        Console.WriteLine("PASS 11 input role contracts");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
