using System.Reflection;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

internal static class SpatialVmChecks
{
    internal static async Task Run()
    {
        int checks = 0;
        string root = Path.GetFullPath(".temp/spatial-vm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach ((string name, string fields) in new[]
            {
                ("computed", "x: value + 1, y: value * 2, z: value - 3, w: value / 2"),
                ("literals", "x: 1, y: 2, z: 3, w: 4"),
                ("mixed-depth", "x: value + 1, y: value * 2, a: \"a\", b: \"b\", c: \"c\", d: \"d\", e: \"e\", f: \"f\", g: \"g\", h: \"h\", i: \"i\", j: \"j\", k: \"k\", l: \"l\", path: ./fixed")
            })
            {
                string source = "fn build(values: Range<Number>) { let rows = $std::storage::Buffer::new([]); for value in values { $std::storage::Buffer::push(rows, scope { "
                    + fields + " }); }; $std::storage::Buffer::snapshot(rows) }; build([1, 2, 3, 4])";
                var module = Compiler.Compile(source, name, 2);
                var result = (RangeValue)await Interpreter.ExecuteAsync(module, new(WorkingDirectory: root));
                List<ScopeValue> rows = [];
                await foreach (Value value in result.Items) rows.Add((ScopeValue)value);
                Check(rows.Count == 4, name + ": row count changed");
                if (name == "computed") Check(rows.All(IsPacked), name + ": actual VM construction did not select numeric storage");
                Console.WriteLine($"{name}: packed {rows.Count(IsPacked)}/{rows.Count}");
                for (int index = 0; index < rows.Count; index++)
                {
                    double expected = name == "literals" ? 1 : index + 2;
                    Check(rows[index]["x"] is NumberValue number && number.Data == expected, name + ": row value changed");
                    Check(rows[index]["y"] is NumberValue y && y.Data == (name == "literals" ? 2 : (index + 1) * 2), name + ": y changed");
                }
                rows[0].SetMember("x", new NumberValue(101));
                Check(rows[1]["x"] is NumberValue neighbor && neighbor.Data == (name == "literals" ? 1 : 3), name + ": row writes alias");
                if (name == "mixed-depth") Check(rows[0]["l"] is StringValue { Data: "l" }, "Reference suffix was truncated by numeric sidecar length.");
            }
        }
        finally { Directory.Delete(root, recursive: true); }
        Console.WriteLine($"PASS {checks} actual VM numeric/reference construction contracts");

        void Check(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
    }

    private static bool IsPacked(ScopeValue value) =>
        typeof(ScopeValue).GetProperty("IsPacked", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(value) is true;
}
