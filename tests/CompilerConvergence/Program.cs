using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

string directory = Path.Combine(Environment.CurrentDirectory, ".temp", "compiler-convergence");
Directory.CreateDirectory(directory);
int assertions = 0;
foreach (int level in new[] { 0, 2, 3 })
{
    foreach (var (name, source, expected) in new[]
    {
        ("row-map", """
            fn Map(initial: Range) {
                let empty = $std::ranges::transform(
                    $std::ranges::take(initial, 0),
                    fn(entry) { scope { key: entry::key, value: entry::value } }
                );
                let state = scope { items: $std::ranges::materialize(empty) };
                let contains = fn(key: String) {
                    $std::ranges::any(state::items,
                        fn(entry) { entry::key == key })
                };
                let put = fn(key: String, value: Number) {
                    let present = contains(key);
                    if present {
                        state::items = $std::ranges::materialize(
                            $std::ranges::transform(state::items, fn(entry) {
                                if entry::key == key {
                                    scope { key: entry::key, value: value }
                                } else { entry }
                            })
                        );
                    } else {
                        state::items = $std::ranges::materialize(
                            $std::ranges::concat(state::items,
                                [scope { key: key, value: value }])
                        );
                    };
                    not present
                };
                for entry in initial { put(entry::key, entry::value); };
                scope { put: put, entries: fn { state::items } }
            };
            let map = Map([
                scope { key: "first", value: 1 },
                scope { key: "second", value: 2 },
                scope { key: "first", value: 3 }
            ]);
            map::put("second", 4);
            let entries = $std::ranges::materialize(map::entries());
            $std::ranges::first(entries)::key == "first"
                and $std::ranges::first(entries)::value == 3
                and $std::ranges::last(entries)::key == "second"
                and $std::ranges::last(entries)::value == 4
                and $std::ranges::size(entries) == 2
            """, "true"),
        ("fresh-range-item", """
            let items = [scope { value: 1 }];
            for increment in [1, 2] {
                items = $std::ranges::materialize(
                    $std::ranges::transform(items, fn(item) {
                        scope { value: item::value + increment }
                    })
                );
            };
            $std::ranges::first(items)::value
            """, "4"),
        ("nested-range-item", """
            let buckets = [[scope { value: 1 }]];
            for increment in [1, 2] {
                buckets = $std::ranges::materialize(
                    $std::ranges::transform(buckets, fn(bucket) {
                        $std::ranges::materialize(
                            $std::ranges::transform(bucket, fn(item) {
                                scope { value: item::value + increment }
                            })
                        )
                    })
                );
            };
            $std::ranges::first($std::ranges::first(buckets))::value
            """, "4"),
        ("nullable-range-member", """
            let items = [scope { value?: 1 }];
            for clear in [false, true] {
                items = $std::ranges::materialize(
                    $std::ranges::transform(items, fn(item) {
                        scope { value?: if clear { null } else { item::value } }
                    })
                );
            };
            $std::ranges::first(items)::value is null
            """, "true"),
        ("callable-identity", """
            let operation = fn(value: Number) { value + 1 };
            for selected in [false, true] {
                if selected { operation = fn(value: Number) { value + 2 }; };
            };
            operation(40)
            """, "42"),
        ("independent-instances", """
            fn make(value: Number) { scope { value: value } };
            let left = make(1);
            let right = make(2);
            left::value = 3;
            right::value
            """, "2")
    })
    {
        string path = Path.Combine(directory, name + ".msync");
        File.WriteAllText(path, source);
        Module sourceModule = Compiler.Compile(source, path, level);
        string relocatable = Path.ChangeExtension(path, $"O{level}.mso");
        using (var file = File.Create(relocatable)) Artifact.Write(sourceModule, file);
        Module restored;
        using (var file = File.OpenRead(relocatable)) restored = Artifact.Read(file);
        Module executable = Binding.Bind(restored, new(directory, directory));
        string executablePath = Path.ChangeExtension(path, $"O{level}.mexe");
        using (var file = File.Create(executablePath)) Artifact.Write(executable, file);
        using (var file = File.OpenRead(executablePath)) executable = Artifact.Read(file);
        foreach (Module module in new[] { sourceModule, restored, executable })
        {
            string actual = (await Interpreter.ExecuteAsync(module)).ToString()!;
            if (actual != expected) throw new InvalidOperationException($"{name} O{level} {module.Stage}: {actual}, expected {expected}");
            assertions++;
        }
    }
}
Console.WriteLine($"PASS: {assertions} loop shape/freshness/callable source/mso/mexe contracts");
