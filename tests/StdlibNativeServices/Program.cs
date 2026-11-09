using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

if (args.FirstOrDefault() == "--echo")
{
    Console.Write("echo:" + await Console.In.ReadToEndAsync());
    return;
}

if (args.FirstOrDefault() == "--fail")
{
    Console.Write("out:failure");
    Console.Error.Write("err:failure");
    Environment.ExitCode = 7;
    return;
}
if (args.FirstOrDefault() == "--command-policies")
{
    await CommandPolicyChecks.RunAsync();
    return;
}

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "native-services-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
try
{
    foreach (int level in new[] { 0, 2, 3 })
    {
        const string files = """
            let file = $std::files::File;
            let directory = $std::files::Directory;
            let folder = directory::new(./work);
            directory::create(folder);
            let first = directory::file(folder, "first.txt");
            let alias = first;
            let separate = file::new(./work/separate.txt);
            file::write(first, "hello 星");
            file::append(first, " second");
            file::write(separate, "independent");
            let initial = file::read_text(first);
            file::move_to(first, directory::child(folder, "moved.txt"));
            let copied = file::copy_to(first, ./work/copy.txt);
            let snapshot = directory::snapshot(folder);
            scope {
                record: first,
                initial: initial,
                moved: file::name(alias) == "moved.txt",
                separate_text: file::read_text(separate),
                count: $std::ranges::size(snapshot),
                copied: copied,
                separate_record: separate,
            }
            """;
        foreach (Module image in Images(files, "native-files.msync", level))
        {
            if (Directory.Exists(Path.Combine(root, "work"))) Directory.Delete(Path.Combine(root, "work"), true);
            var value = (ScopeValue)await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root));
            Require(value["initial"] is StringValue { Data: "hello 星 second" }, "File policy read/write/append bytes");
            Require(value["moved"] is BoolValue { Data: true }, "File alias did not observe record path mutation");
            Require(value["separate_text"] is StringValue { Data: "independent" }, "Fresh File records share path state");
            Require(value["count"] is NumberValue { Data: 3 }, "Directory snapshot membership");
            Require(value["copied"] is BoolValue { Data: true }, "Actual file copy");
            var file = (ScopeValue)value["record"];
            Require(ReferenceEquals(file["policy"], ((ScopeValue)value["separate_record"])["policy"]), "Default policy was recreated for each file");
            Require(file.Count == 2 && file["path"] is PathValue && file["policy"] is ScopeValue, "Canonical File is not a data-only record");
            Require(await File.ReadAllTextAsync(Path.Combine(root, "work", "copy.txt")) == "hello 星 second", "Published copy content");
        }

        const string policy = """
            let state = scope { text: "", reads: 0, writes: 0 };
            scope memory {
                read: fn(path: Path) -> String {
                    state::reads = state::reads + 1;
                    state::text
                },
                lines: fn(path: Path) -> Range<String> {
                    state::reads = state::reads + 1;
                    $std::string::lines(state::text)
                },
                write: fn(path: Path, value: String) -> Null {
                    state::writes = state::writes + 1;
                    state::text = value;
                },
                append: fn(path: Path, value: String) -> Null {
                    state::writes = state::writes + 1;
                    state::text = $std::string::concat(state::text, value);
                },
            };
            let file = $std::files::File;
            let record = file::with_policy(./never-opened.txt, memory);
            let untouched = state::reads == 0 and state::writes == 0;
            file::write(record, "custom");
            file::append(record, " policy");
            let text = file::read_text(record);
            let lines = $std::ranges::size(file::read_lines(record));
            scope {
                untouched: untouched,
                text: text,
                reads: state::reads,
                writes: state::writes,
                lines: lines,
                record: record,
                policy: memory,
            }
            """;
        foreach (Module image in Images(policy, "native-policy.msync", level))
        {
            var value = (ScopeValue)await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root));
            Require(value["untouched"] is BoolValue { Data: true }, "Concept checking/construction executed policy callbacks");
            Require(value["text"] is StringValue { Data: "custom policy" }, "Custom text policy not selected");
            Require(value["reads"] is NumberValue { Data: 2 } && value["writes"] is NumberValue { Data: 2 }, "Policy invocation count");
            Require(value["lines"] is NumberValue { Data: 1 }, "Selected policy line producer");
            Require(ReferenceEquals(((ScopeValue)value["record"])["policy"], value["policy"]), "Policy record lost identity");
            Require(!File.Exists(Path.Combine(root, "never-opened.txt")), "In-memory policy fell through to physical IO");
        }

        string executable = Environment.ProcessPath!.Replace("\"", "\"\"", StringComparison.Ordinal);
        string command = $$"""
            let command = $std::commands::Command;
            let completed = $std::commands::Completed;
            let original = command::new("{{executable}}", ["--echo"]);
            let extended = command::append_args(original, ["extra"]);
            let local = command::on(extended, $local);
            let result = command::capture_input(local, "hello 星");
            completed::write_to(result, ./command-output);
            scope {
                result: result,
                good: result::success and result::code == 0
                    and result::output == "echo:hello 星",
                original_count: $std::ranges::size(original::arguments),
                extended_count: $std::ranges::size(extended::arguments),
                original_record: original,
                extended_record: extended,
            }
            """;
        foreach (Module image in Images(command, "native-command.msync", level))
        {
            var value = (ScopeValue)await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root));
            Require(value["good"] is BoolValue { Data: true }, "Actual Command stdin/capture/UTF8");
            Require(value["original_count"] is NumberValue { Data: 1 } && value["extended_count"] is NumberValue { Data: 2 }, "Command argument snapshot mutation");
            Require(!ReferenceEquals(value["original_record"], value["extended_record"]), "Command configuration constructors reused identity");
            Require(((ScopeValue)value["result"]).Members.Values.All(member => member is not FunctionValue), "Completed data owns object methods");
            Require(await File.ReadAllTextAsync(Path.Combine(root, "command-output")) == "echo:hello 星", "Completed output publication");
        }
        Reject("$std::files::File::with_policy(./x, scope { read: fn(path:Path) { 1 }, lines: fn(path:Path) { [path as String] }, write: fn(path:Path,text:String) {}, append: fn(path:Path,text:String) {} })", "read", level);
        Reject("let value = $std::files::File::new(./x); value::path = 1;", "Path", level);
        Reject("$std::commands::Command::run(scope { executable: ./x, arguments: true, runner: $std::commands::NativeProcess })", "arguments", level);
    }
    Console.WriteLine($"PASS {checks} canonical native service contracts");
}
finally { Directory.Delete(root, true); }

IEnumerable<Module> Images(string source, string name, int level)
{
    Module module = Compiler.Compile(source, name, level);
    yield return module;
    using MemoryStream bytes = new();
    Artifact.Write(module, bytes);
    bytes.Position = 0;
    yield return Artifact.Read(bytes);
    Module executable = Binding.Bind(module, new(root, root));
    using MemoryStream executableBytes = new();
    Artifact.Write(executable, executableBytes);
    executableBytes.Position = 0;
    yield return Artifact.Read(executableBytes);
}
void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
void Reject(string source, string fragment, int level)
{
    try { Compiler.Compile(source, "native-service-rejection.msync", level); }
    catch (CompileException error)
    {
        Require(error.Diagnostic.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase), error.Diagnostic.Message);
        return;
    }
    throw new InvalidOperationException("Expected rejection: " + source);
}
