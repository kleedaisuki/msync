using Msync.Compiler.Syntax;
using Msync.Core;

internal static class QuotedWindowsPaths
{
    public static void Run()
    {
        string[] paths = [
            "D:/\"Program Files (x86)\"",
            "D:/base/\"semi;colon\"/next.txt",
            "D:\\\"Program Files (x86)\"",
            "\\\\server\\share\\\"space name\"",
            "\\\"space name\""
        ];
        foreach (string path in paths)
        {
            string source = $"let selected = {path}; decltype(selected)";
            var parsed = Parser.Parse(source);
            var binding = (LetStatement)parsed.Statements[0];
            Check(binding.Value is BinaryExpr { Operator: "/" }, "quoted native path is not a Path join: " + source);
            var module = Msync.Compiler.Compiler.Compile(source);
            Check(module.Functions[0].ReturnType == TypeRef.Type, "quoted path type query failed");
            string formatted = Formatter.Format(source);
            Check(!formatted.Contains("/ \"", StringComparison.Ordinal) && !formatted.Contains("\\ \"", StringComparison.Ordinal),
                "formatter separated a native prefix from its quoted child: " + formatted);
            Check(Formatter.Format(formatted) == formatted, "quoted native formatting is not idempotent");
            _ = Msync.Compiler.Compiler.Compile(formatted);
        }
        var plain = Msync.Compiler.Compiler.Compile("let selected = D:/base/plain.txt;");
        Check(plain.Constants.Any(value => value is PathValue { Raw: "D:/base/plain.txt" }), "ordinary native path spelling changed");
        _ = Msync.Compiler.Compiler.Compile("$local::D:/\"Program Files (x86)\"/tool.exe(\"argument\");");
        Console.WriteLine("quoted Windows path items preserve Path type, ordinary tokens and compact formatting");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
