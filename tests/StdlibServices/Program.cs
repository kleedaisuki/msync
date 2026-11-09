using Msync.Optimization;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

if (args.FirstOrDefault() == "--echo")
{
    Console.Write("echo:" + await Console.In.ReadToEndAsync());
    return;
}

string root = Path.Combine(Environment.CurrentDirectory, ".temp", "services-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
int checks = 0;
try
{
    foreach (int level in new[] { 0, 2, 3 })
    {
        string source = """
            let Directory = $std::files::Directory;
            let File = $std::files::File;
            let folder = Directory::new(./work);
            Directory::create(folder);
            let file = Directory::file(folder, "first.txt");
            let alias = file;
            let separate = Directory::file(folder, "separate.txt");
            File::write(file, "hello 星");
            File::append(file, " second");
            File::write(separate, "independent");
            let initial = File::read_text(file);
            File::move_to(file, Directory::child(folder, "moved.txt"));
            let copied = File::copy_to(file,
                Directory::child(folder, "copy.txt"));
            let snapshot = Directory::snapshot(folder);
            let filtered = Directory::matching(folder, fn(path: Path) {
                $std::path::extension(path) == ".txt"
            });
            scope {
                text: initial,
                moved: File::name(alias) == "moved.txt",
                separate: File::read_text(separate),
                count: $std::ranges::size(snapshot),
                filtered: $std::ranges::size(filtered),
                copied: copied,
            }
            """;
        Module module = Compiler.Compile(source, "services.msync", level);
        using MemoryStream serialized = new();
        Artifact.Write(module, serialized);
        serialized.Position = 0;
        foreach (Module image in new[] { module, Artifact.Read(serialized), Binding.Bind(module, new(root, root)) })
        {
            if (Directory.Exists(Path.Combine(root, "work"))) Directory.Delete(Path.Combine(root, "work"), true);
            Value result = await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root));
            if (result is not ScopeValue scope) throw new InvalidOperationException("Service result is not Scope");
            Require(scope.Members["text"] is StringValue { Data: "hello 星 second" }, "File read/append content: " + scope.Members["text"]);
            Require(scope.Members["moved"] is BoolValue { Data: true }, "Move did not update the aliased record location");
            Require(scope.Members["separate"] is StringValue { Data: "independent" }, "Independent file records share location");
            Require(scope.Members["count"] is NumberValue { Data: 3 }, "Directory snapshot lost entries");
            Require(scope.Members["filtered"] is NumberValue { Data: 3 }, "Directory predicate traversal");
            Require(await File.ReadAllTextAsync(Path.Combine(root, "work", "copy.txt")) == "hello 星 second", "File copy bytes");
        }

        string executable = Environment.ProcessPath!.Replace("\"", "\"\"", StringComparison.Ordinal);
        string command = $$"""
            let Command = $std::commands::Command;
            let Completed = $std::commands::Completed;
            let command = Command::new("{{executable}}", ["--echo"]);
            let extended = Command::append_args(command, ["extra"]);
            let local = Command::on(extended, $local);
            let result = Command::capture_input(local, "hello 星");
            Completed::write_to(result, ./command-output);
            result::success and result::code == 0 and result::output == "echo:hello 星"
                and $std::ranges::size(command::arguments) == 1
                and $std::ranges::size(extended::arguments) == 2
            """;
        Module commands = Compiler.Compile(command, "commands.msync", level);
        using MemoryStream commandImage = new();
        Artifact.Write(commands, commandImage);
        commandImage.Position = 0;
        foreach (Module image in new[] { commands, Artifact.Read(commandImage), Binding.Bind(commands, new(root, root)) })
        {
            Require(await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root)) is BoolValue { Data: true }, "Command capture/control/Unicode");
            Require(await File.ReadAllTextAsync(Path.Combine(root, "command-output")) == "echo:hello 星", "Command output publication");
        }
    }
    Console.WriteLine($"PASS {checks} services contracts");
}
finally
{
    Directory.Delete(root, true);
}

void Require(bool condition, string message)
{
    checks++;
    if (!condition) throw new InvalidOperationException(message);
}
