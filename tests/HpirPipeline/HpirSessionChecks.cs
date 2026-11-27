using Msync.Compiler;
using Msync.Compiler.Syntax;
using Msync.Core;
using Msync.Core.Hpir;
using Msync.Runtime;
using Msync.Runtime.Hpir;

internal static class HpirSessionChecks
{
    internal static void Run()
    {
        string prelude = ShellPrelude();
        for (int optimization = 0; optimization <= 3; optimization++)
        {
            Check(optimization);
            CheckShell(optimization, prelude);
        }
        CheckPathOrigins(0);
        CheckPathOrigins(3);
        if (OperatingSystem.IsLinux() && File.Exists("/bin/sh"))
        {
            CheckProcessWork(0);
            CheckProcessWork(2);
        }
        Console.WriteLine("PASS HPIR live source session: stable cells, native/guest calls, shell prelude, failed-prefix publication, collection, no replay, and reset O0–O3");
    }

    internal static string ShellPrelude()
    {
        using var stream = typeof(HpirSessionChecks).Assembly.GetManifestResourceStream("shell.msync")!;
        using var reader = new StreamReader(stream);
        var shell = Parser.Parse(reader.ReadToEnd(), "msync-stdlib:///0.8.0/shell.msync");
        return string.Join('\n', shell.Statements.Select(statement => statement switch
        {
            FunctionDeclaration function => function.Name,
            NativeDeclaration native => native.Name,
            ScopeDeclaration scope => scope.Name,
            LetStatement { Pattern: NamePattern binding } => binding.Name,
            _ => null
        }).OfType<string>().Select(name => $"let {name} = $std::shell::{name};"));
    }

    private static void CheckPathOrigins(int optimization)
    {
        string work = Path.GetFullPath(Path.Combine(".temp", "hpir", "session-paths-" + Guid.NewGuid().ToString("N")));
        string initial = Path.Combine(work, "old"), changed = Path.Combine(work, "new");
        Directory.CreateDirectory(initial);
        Directory.CreateDirectory(changed);
        try
        {
            string ambient = Environment.CurrentDirectory;
            List<string> changes = [];
            List<RuntimeTimelineRecord> recorded = [];
            using var execution = new HpirExecutionSession(new(WorkingDirectory: initial)
            {
                WorkingDirectoryChanged = changes.Add, Timeline = new(recorded.Add, "hpir-session-paths")
            });
            var compiler = new CompilerSession("<hpir-session-paths>", optimization);
            var saved = Submit("""
                fn saved() { ./same };
                fn write_saved(value: String) { $std::io::write_text(./written.txt, value); null };
                fn copy_saved(value: String) { value -> ./copied.txt; null };
                saved()
                """);
            Require(saved.Value.Path?.WorkingDirectory == initial, "The original Path occurrence captures the initial directory.");
            _ = Submit("write_saved(\"first\"); copy_saved(\"first copy\"); null");
            execution.ChangeWorkingDirectory(changed);
            Require(execution.WorkingDirectory == changed && changes.SequenceEqual([changed])
                && Environment.CurrentDirectory == ambient, "Host /cd must change only this session's directory.");
            Require(Submit("saved()").Value.Path?.WorkingDirectory == initial
                && Submit("./same").Value.Path?.WorkingDirectory == changed,
                "An older occurrence retains its captured origin; the same raw spelling at a new occurrence does not.");
            var resumed = Submit("write_saved(\"after\"); copy_saved(\"after copy\"); null");
            Require(File.ReadAllText(Path.Combine(initial, "written.txt")) == "after"
                && File.ReadAllText(Path.Combine(initial, "copied.txt")) == "after copy"
                && !File.Exists(Path.Combine(changed, "written.txt")) && !File.Exists(Path.Combine(changed, "copied.txt"))
                && resumed.Image.Program.TransferInputs.Count == 0,
                "Both resumed effects must consume the original Paths, not this submission's current directory.");
            var prepared = Submit("\"fresh\" -> ./new.txt;");
            Require(prepared.Image.Program.TransferInputs.Count == 1
                && File.ReadAllText(Path.Combine(changed, "new.txt")) == "fresh",
                "A fresh current-submission literal keeps its bind-time preparation.");
            Require(recorded.Any(item => item.Name == "hpir.session.link.globals"
                && item.Metrics.GetValueOrDefault("paths.reused") > 0), "Missing occurrence relocation reuse telemetry.");
            Require(recorded.Any(item => item.Name == "hpir.session.link.globals"
                && item.Metrics.GetValueOrDefault("paths.prepared_captures") > 0), "Missing first anchor capture for a sliced prepared Path.");
            Console.WriteLine($"PASS HPIR O{optimization} session Path origins across /cd, resumed writes/transfers and fresh preparation");

            (HpirSessionSubmission Image, HpirSessionValue Value) Submit(string source)
            {
                var image = compiler.CompilePhysical(source);
                var value = execution.Execute(image);
                compiler.AcceptPhysicalSubmission(execution.ObserveBinding);
                return (image, value);
            }
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    private static void CheckProcessWork(int optimization)
    {
        string work = Path.GetFullPath(Path.Combine(".temp", "hpir", "session-process-" + Guid.NewGuid().ToString("N")));
        string release = Path.Combine(work, "release");
        Directory.CreateDirectory(work);
        try
        {
            List<RuntimeTimelineRecord> recorded = [];
            using StringWriter output = new() { NewLine = "\n" };
            using var execution = new HpirExecutionSession(new(WorkingDirectory: work, Output: output)
                { Timeline = new(recorded.Add, "hpir-session-process") });
            var compiler = new CompilerSession("<hpir-session-process>", optimization);
            var launched = compiler.CompilePhysical("""
                let child = release /bin/sh("-c", "while [ ! -f ./release ]; do sleep 0.01; done; printf live-stdout");
                let pending = child::stdout;
                let replace_ready = "before";
                null
                """);
            _ = execution.Execute(launched);
            compiler.AcceptPhysicalSubmission(execution.ObserveBinding);
            Require(PendingBindings(launched) == 1,
                "Accept a genuinely pending typed Text without demand or reboxing ready Text.");
            var broken = compiler.CompilePhysical("""
                let keep_child = child;
                let pending_alias = pending;
                replace_ready = pending;
                let broken_number = "not-a-number" as Number;
                let unreachable = 77;
                null
                """);
            try
            {
                _ = execution.Execute(broken);
                throw new InvalidOperationException("Expected a failing submission while the child was still pending.");
            }
            catch (MsyncException error) when (error.Span?.Source == "<hpir-session-process>")
            {
                compiler.ReconcilePhysicalSubmission(execution.IsInitialized, execution.ObserveBinding);
            }
            Require(compiler.BindingNames.Contains("keep_child") && compiler.BindingNames.Contains("pending_alias")
                && !compiler.BindingNames.Contains("unreachable"), "A failed prefix must publish its retained aliases.");
            Require(PendingBindings(broken) == 3,
                "Both new and already ready canonical String cells can hold the same pending completion.");
            Require(recorded.Count(item => item.Name == "hpir.work.settle"
                && item.Metrics.GetValueOrDefault("work.session_pending") == 1) == 2,
                "Both submissions must finish without joining the gate-held child.");
            File.WriteAllText(release, "");
            var written = compiler.CompilePhysical("$std::io::write_text(./future.txt, pending_alias); null");
            Require(written.Program.Imports.Any(item => item.Service == HpirService.DemandText)
                && written.Program.Imports.Any(item => item.Service == HpirService.HasLayout),
                "Stored Text demand must use the two real pointer layouts, not demand at publication.");
            _ = execution.Execute(written);
            compiler.AcceptPhysicalSubmission(execution.ObserveBinding);
            Require(File.ReadAllBytes(Path.Combine(work, "future.txt")).AsSpan().SequenceEqual("live-stdout"u8),
                "Write exact stdout bytes from the retained Text alias published before the failure.");
            _ = execution.Execute(compiler.CompilePhysical("$std::io::println(keep_child::stdout); null"));
            compiler.AcceptPhysicalSubmission(execution.ObserveBinding);
            Require(output.ToString() == "live-stdout\n", "Demand actual stdout from the same retained process after the gate opens.");
            string next = Path.Combine(work, "next");
            Directory.CreateDirectory(next);
            var ready = execution.Execute(compiler.CompilePhysical("""
                $std::shell::cd("next");
                replace_ready = $std::shell::pwd() as String;
                replace_ready
                """));
            compiler.AcceptPhysicalSubmission(execution.ObserveBinding);
            Require(ready.Text == next, "A revised pwd must be valid ready Text in the same previously mixed cell.");
            execution.ChangeWorkingDirectory(work);
            _ = execution.Execute(compiler.CompilePhysical("""
                let reset_child = release /bin/sh("-c", "printf started > ./reset-started; while [ ! -f ./release-reset ]; do sleep 0.01; done");
                null
                """));
            compiler.AcceptPhysicalSubmission(execution.ObserveBinding);
            Require(SpinWait.SpinUntil(() => File.Exists(Path.Combine(work, "reset-started")), TimeSpan.FromSeconds(3)),
                "The real child must reach the OS-side reset gate.");
            execution.Dispose();
            Require(recorded.Last(item => item.Name == "hpir.work.settle").Metrics.GetValueOrDefault("work.joined") > 0,
                "Reset must cancel and join active session work before releasing its owner.");
            Console.WriteLine($"PASS HPIR O{optimization} session-held Process/stdout across failure, exact write_text, and reset with an active child");

            int PendingBindings(HpirSessionSubmission image) => image.CellRelocations.Values
                .Count(cell => cell.Kind == TypeKind.String && execution.ObserveBinding(cell.Slot) is { RequiresDemand: true });
        }
        finally { Directory.Delete(work, recursive: true); }
    }

    private static void CheckShell(int optimization, string prelude)
    {
        var session = new CompilerSession("<hpir-shell>", optimization);
        using var execution = new HpirExecutionSession();
        var bootstrap = Submit(prelude);
        Require(bootstrap.Value.Kind == TypeKind.Null, "The full shell prelude must be accepted silently.");
        var working = Submit("pwd()");
        Require(working.Value.Path is { } pwd && pwd.Raw == Path.GetFullPath(Environment.CurrentDirectory)
            && pwd.WorkingDirectory == pwd.Raw, "The actual shell pwd() must return the bound current directory.");
        Require(Submit("home()").Value.Path is { } home && home.Raw == HostContext.HomeDirectory,
            "The actual shell home() must return the bound home directory.");
        var initialAliases = bootstrap.Image.CellRelocations.Values.Where(cell => cell.Aliased)
            .Select(cell => cell.Slot).ToHashSet();
        Require(working.Image.CellRelocations.Values.Any(cell => cell.Aliased && initialAliases.Contains(cell.Slot)),
            "A lazily initialized module must still write the canonical field captured by the shell.");

        (HpirSessionSubmission Image, HpirSessionValue Value) Submit(string source)
        {
            var image = session.CompilePhysical(source);
            var result = execution.Execute(image);
            session.AcceptPhysicalSubmission(execution.ObserveBinding);
            return (image, result);
        }
    }

    private static void Check(int optimization)
    {
        List<RuntimeTimelineRecord> runtime = [];
        List<CompilationObservation> compiler = [];
        using StringWriter output = new() { NewLine = "\n" };
        var session = new CompilerSession("<hpir-session>", optimization,
            new CompilationTrace(compiler.Add, includeOptimizerPasses: true));
        using var execution = new HpirExecutionSession(new(Output: output) { Timeline = new(runtime.Add, "hpir-session") });

        HpirSessionSubmission first = session.CompilePhysical("""
            let value = 7;
            fn read() { value };
            fn set(next: Number) { global value; value = next; };
            let record = object { value: 1 };
            let alias = record;
            let text = "retained: λ";
            $std::io::println("once");
            null
            """);
        _ = execution.Execute(first);
        session.AcceptPhysicalSubmission(execution.ObserveBinding);
        Number("let admitted = true; let rejected = false; if admitted and not rejected { 1 } else { 0 }", 1);
        Number("""
            if ({ $std::io::println("condition"); true }) { 7 } else { 9 }
            """, 7);
        var retainedPath = Submit("let retained_path = ./relative/presentation; retained_path");
        Require(retainedPath.Path is { Raw: "./relative/presentation" } path
            && path.WorkingDirectory == Path.GetFullPath(Environment.CurrentDirectory),
            "A Path result must retain its source spelling and contextual origin.");
        _ = Submit("""
            let display_record = object { n: 7, caption: "row", location?: ./relative/nested, absent?: null };
            let display_root = object { first: display_record, second: display_record };
            null
            """);
        var view = Submit("display_root");
        Require(view.Scope is { Members.Count: 2 } nested
            && nested.Members.All(member => member.Value.Scope is { Cycle: false, Members.Count: 4 }),
            "Field projection must survive later compilation and render an alias twice, not as a cycle.");
        Require(Member(Member(view, "first"), "location").Path is { Raw: "./relative/nested" }
            && Member(Member(view, "second"), "caption").Text == "row",
            "Read the display member from its declared Path/nullable and String lanes.");
        var updated = Member(Submit("display_record::n = 9; display_record::location = null; display_root"), "second");
        Require(Member(updated, "n").Number == 9 && Member(updated, "location").Kind == TypeKind.Null
            && Member(updated, "absent").Kind == TypeKind.Null, "The display must observe selected live payloads, not the initial semantic constants.");
        var firstType = session.CompilePhysical("""
            let expected = decltype(7);
            fn checks_type(value: Number, witness: Type) -> Bool { value is witness };
            expected
            """);
        Require(execution.Execute(firstType).Type == TypeRef.Number, "A Type result is a typed presentation observation.");
        session.AcceptPhysicalSubmission(execution.ObserveBinding);
        var laterType = session.CompilePhysical("""
            let other = decltype(false);
            let nested = Range<Number>;
            let admitted = checks_type(18, expected);
            let rejected = checks_type(18, other);
            admitted and not rejected
            """);
        Require(laterType.Program.TypeLiterals.Take(firstType.Program.TypeLiterals.Count)
                .SequenceEqual(firstType.Program.TypeLiterals)
            && execution.Execute(laterType).Boolean, "Persisted Type handles must keep meaning as later image tables grow.");
        session.AcceptPhysicalSubmission(execution.ObserveBinding);
        Require(Submit("nested").Type == new TypeRef(TypeKind.Range, TypeRef.Number), "Retain the exact Range element witness.");

        Fail("""
            let before = 11;
            alias::value = 4;
            let failed = "not-a-number" as Number;
            let after = 5;
            null
            """);
        Require(session.BindingNames.Contains("before") && !session.BindingNames.Contains("failed")
            && !session.BindingNames.Contains("after"), "Accept only the actually published prefix.");
        Number("before + record::value + read()", 22);
        Fail("let value = \"not-a-number\" as Number; null");
        Number("value + read()", 14);
        Number("value = 9; alias::value = 5; read() + record::value", 14);
        Number("let value = 100; read() + value", 109);
        Number("set(6); read() + value + record::value", 111);
        _ = Submit("""
            let action = fn { 4 };
            let stable = action;
            fn invoke() { action() };
            null
            """);
        Number("invoke() * 10 + stable()", 44);
        Number("action = fn { 6 }; invoke() * 10 + stable()", 64);
        Fail("""
            action = fn { 5 };
            let failed_action = "not-a-number" as Number;
            action = fn { 8 };
            null
            """);
        Number("invoke() * 10 + stable()", 54);
        Number("action() * 10 + stable()", 54);
        var function = Submit("invoke");
        Require(function.Kind == TypeKind.Function && function.Bits != 0 && function.Text is null,
            "A Function result must present its actual callable identity.");
        var builtin = Submit("$std::math::floor");
        Require(builtin.Kind == TypeKind.Function && builtin.Text == "math.floor", "Present the registered native label.");
        _ = Submit("""
            let floor_target = $std::math::floor;
            fn through_target(value: Number) { floor_target(value) };
            null
            """);
        Number("through_target(3.75)", 3);
        Number("floor_target = fn(value: Number) { value + 10 }; through_target(3.75)", 13.75);
        Number("floor_target = $std::math::ceil; through_target(3.75)", 4);
        Fail("""
            floor_target = $std::math::floor;
            let failed_native = "not-a-number" as Number;
            floor_target = fn(value: Number) { value + 20 };
            null
            """);
        Number("through_target(3.75)", 3);
        Require(Submit("floor_target").Text == "math.floor", "The failed prefix retained the actual native descriptor.");
        _ = Submit("""
            let path_target = fn(value: Path) -> String { value as String };
            fn through_path(value: Path) -> String { path_target(value) };
            null
            """);
        Require(Submit("path_target = $std::string::trim; through_path(./relative/presentation)").Text
            == "./relative/presentation", "Select the actual native signature and coerce Path to String on its branch.");
        Number("""
            let total = 0;
            for number in $std::ranges::iota(2, 5) { total = total + number; };
            total
            """, 9);
        Number("""
            fn churn(limit: Number) -> Number {
                let i = 0;
                loop {
                    if i >= limit { break i; };
                    let cell = object { n: i };
                    i = cell::n + 1;
                }
            };
            churn(22000)
            """, 22000);
        Require(Submit("text").Text == "retained: λ", "The captured text outlives its original bank and survives collection.");
        Require(Submit("expected").Type == TypeRef.Number, "A retained Type handle does not depend on the former literal bank.");
        Require(Submit("retained_path").Path == retainedPath.Path, "Retain the entire Path record across collection.");
        Require(Submit("invoke").Bits == function.Bits, "A new image must not renumber a retained callable.");
        Require(output.ToString() == "once\ncondition\n", "Prior effects replayed, or the known-arm condition was skipped.");
        Require(runtime.Any(item => item.Name == "hpir.heap.collect" && item.Metrics.GetValueOrDefault("roots.globals") > 0),
            "Missing physical collection over retained session roots.");
        Require(runtime.Any(item => item.Name == "hpir.session.link.globals" && item.Metrics.GetValueOrDefault("globals.reused") > 0),
            "Missing retained-global linkage.");
        Require(runtime.Any(item => item.Name == "hpir.session.presentation"
            && item.Attributes.GetValueOrDefault("result.kind") == "Path"
            && item.Metrics.GetValueOrDefault("result.record_bytes") == HpirPathImports.PathBytes),
            "Missing typed caller result observation.");
        Require(runtime.Any(item => item.Name == "hpir.session.presentation"
            && item.Metrics.GetValueOrDefault("scope.records") == 3 && item.Metrics.GetValueOrDefault("scope.fields") == 10),
            "Missing concrete Object display work attribution.");
        if (HpirMachineImage.IsSupported && Environment.GetEnvironmentVariable("MSYNC_JIT") == "force")
            Require(runtime.Any(item => item.Name == "hpir.machine.publish"
                && item.Metrics.GetValueOrDefault("code.bytes") > 0), "The forced session must emit native guest code.");
        Require(compiler.Any(item => item.Name == "compiler.hpir.select" && item.Metrics?.GetValueOrDefault("session.cells") > 0)
            && compiler.Any(item => item.Name == "compiler.hpir.select.function"
                && item.Metrics?.GetValueOrDefault("memory.session_alias_bindings") > 0),
            "Missing compiler attribution of session relocations and structural field alias links.");

        try
        {
            _ = execution.Execute(first);
            throw new InvalidOperationException("Expected the submission generation to reject replay.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("only once", StringComparison.Ordinal)) { }
        var newCompiler = new CompilerSession("<hpir-reset>", optimization);
        using var fresh = new HpirExecutionSession();
        var reset = newCompiler.CompilePhysical("let value = 2; value");
        try
        {
            _ = execution.Execute(reset);
            throw new InvalidOperationException("Expected a different compiler owner to be rejected.");
        }
        catch (InvalidOperationException error) when (error.Message.Contains("different compilation session", StringComparison.Ordinal)) { }
        Require(fresh.Execute(reset).Number == 2,
            "Independent session must have independent guest and compiler state.");
        return;

        HpirSessionValue Submit(string source)
        {
            var result = execution.Execute(session.CompilePhysical(source));
            session.AcceptPhysicalSubmission(execution.ObserveBinding);
            return result;
        }

        void Fail(string source)
        {
            var submission = session.CompilePhysical(source);
            try { _ = execution.Execute(submission); }
            catch (MsyncException error) when (error.Span?.Source == "<hpir-session>")
            {
                session.ReconcilePhysicalSubmission(execution.IsInitialized, execution.ObserveBinding);
                return;
            }
            throw new InvalidOperationException("Expected source-attributed runtime failure after the prefix.");
        }

        void Number(string source, double expected)
        {
            HpirSessionValue observed = Submit(source);
            Require(observed.Kind == TypeKind.Number && observed.Number == expected,
                $"O{optimization} {source}: expected {expected}, got {observed.Number}");
        }
    }

    private static HpirSessionValue Member(HpirSessionValue instance, string name) =>
        instance.Scope!.Members.Single(member => member.Name == name).Value;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
