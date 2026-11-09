using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;

internal static class CommandPolicyChecks
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Environment.CurrentDirectory, ".temp", "command-policies-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        int checks = 0;
        string executable = Environment.ProcessPath!.Replace("\"", "\"\"", StringComparison.Ordinal);
        try
        {
            foreach (int level in new[] { 0, 2, 3 })
            {
                string source = $$"""
                    let counts = scope { runs: 0, inputs: 0, spawns: 0 };
                    scope TrackedRunner {
                        spawn: fn(program: Path, arguments: Range) -> Process {
                            counts::spawns = counts::spawns + 1;
                            $std::commands::NativeProcess::spawn(program, arguments)
                        },
                        run: fn(program: Path, arguments: Range) -> Process {
                            counts::runs = counts::runs + 1;
                            $std::commands::NativeProcess::run(program, arguments)
                        },
                        run_input: fn(program: Path, arguments: Range,
                            input: String) -> Process {
                            counts::inputs = counts::inputs + 1;
                            $std::commands::NativeProcess::run_input(
                                program, arguments, input)
                        },
                    };
                    scope AcceptSeven {
                        accept: fn(result: $std::concepts::CompletedProcess)
                            -> Bool {
                            result::code == 7
                        },
                    };
                    let command = $std::commands::Command;
                    let checked = $std::commands::Checked;
                    let result = $std::data::Result;
                    let spec = command::with_runner("{{executable}}", ["--echo"],
                        TrackedRunner);
                    let changed = command::with_args(spec, ["--echo"]);
                    let extended = command::append_args(changed, ["extra"]);
                    let local = command::on(extended, $local);
                    let failure = command::with_runner("{{executable}}", ["--fail"],
                        TrackedRunner);
                    let selected = checked::with_policy(failure, AcceptSeven);
                    let ready = counts::runs == 0 and counts::inputs == 0
                        and counts::spawns == 0;
                    let echo = command::run_input(local, "policy 星");
                    let child = command::spawn(spec);
                    $std::process::close_stdin(child);
                    let spawned = $std::process::wait(child);
                    let failed = checked::run(checked::new(failure));
                    let accepted = checked::run(selected);
                    let succeeded = checked::run_input(checked::new(spec), "checked");
                    let fail_record = result::fold(failed,
                        fn(value) { value }, fn(error) { error });
                    let accepted_record = result::get_or(accepted, fail_record);
                    let success_record = result::get_or(succeeded, fail_record);
                    scope {
                        ready: ready,
                        output: $std::process::stdout(echo),
                        spawned: $std::process::stdout(spawned),
                        failed: result::is_err(failed),
                        accepted: result::is_ok(accepted),
                        accepted_code: accepted_record::code,
                        accepted_success: accepted_record::success,
                        failure_record: fail_record,
                        success_output: success_record::output,
                        run_count: counts::runs,
                        input_count: counts::inputs,
                        spawn_count: counts::spawns,
                        record: spec,
                        clone: local,
                    }
                    """;
                Module module = Compiler.Compile(source, "command-policies.msync", level);
                foreach (Module image in Images(module, root))
                {
                    var value = (ScopeValue)await Interpreter.ExecuteAsync(image, new(WorkingDirectory: root));
                    Require(value["ready"] is BoolValue { Data: true }, "Construction or concept checking ran a process");
                    Require(value["output"] is StringValue { Data: "echo:policy 星" }, "Selected runner lost stdin/output");
                    Require(value["spawned"] is StringValue { Data: "echo:" }, "Selected spawn runner did not own child");
                    Require(value["failed"] is BoolValue { Data: true }, "Default checked policy accepted nonzero exit");
                    Require(value["accepted"] is BoolValue { Data: true }, "Custom exit policy did not select Result::ok");
                    Require(value["accepted_code"] is NumberValue { Data: 7 } && value["accepted_success"] is BoolValue { Data: false }, "Exit policy rewrote actual Process status");
                    var failure = (ScopeValue)value["failure_record"];
                    Require(failure["code"] is NumberValue { Data: 7 } && failure["output"] is StringValue { Data: "out:failure" } && failure["error"] is StringValue { Data: "err:failure" }, "Result::err lost completed process details");
                    Require(value["success_output"] is StringValue { Data: "echo:checked" }, "Checked success lost data");
                    Require(value["run_count"] is NumberValue { Data: 2 } && value["input_count"] is NumberValue { Data: 2 } && value["spawn_count"] is NumberValue { Data: 1 }, "Configured runner was not preserved through clones/checks");
                    var record = (ScopeValue)value["record"];
                    var clone = (ScopeValue)value["clone"];
                    Require(ReferenceEquals(record["runner"], clone["runner"]) && !ReferenceEquals(record, clone), "Clones lost shared runner or fresh data identity");
                    Require(record.Count == 3 && record.Members.Values.All(member => member is not FunctionValue), "Command stores per-instance operations");
                }
                Reject("$std::commands::Command::with_runner(./program, [], true)", "ProcessRunner", level);
                Reject("$std::commands::Command::with_runner(./program, [], scope { run: fn(p:Path,a:Range) -> Process { $std::process::run(p,a) } })", "spawn", level);
                Reject("$std::commands::Checked::with_policy($std::commands::Command::new(./p, []), scope { accept: fn(value:$std::concepts::CompletedProcess) { 1 } })", "accept", level);
                Reject("$std::commands::Checked::with_policy($std::commands::Command::new(./p, []), scope { wrong: true })", "accept", level);
                Reject("$std::commands::Command::run(scope { executable: ./p, arguments: [] })", "runner", level);
            }
            Console.WriteLine($"PASS {checks} process/exit policy contracts");
        }
        finally { Directory.Delete(root, true); }

        void Require(bool condition, string message)
        {
            checks++;
            if (!condition) throw new InvalidOperationException(message);
        }
        void Reject(string source, string fragment, int level)
        {
            try { Compiler.Compile(source, "command-policy-rejection.msync", level); }
            catch (CompileException error)
            {
                Require(error.Diagnostic.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase), error.Diagnostic.Message);
                return;
            }
            throw new InvalidOperationException("Expected rejection: " + source);
        }
    }

    private static IEnumerable<Module> Images(Module module, string root)
    {
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
}
