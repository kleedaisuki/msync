using System.Text;
using System.Reflection;
using Msync.Compiler;
using Msync.Core;
using Msync.Runtime;
using Module = Msync.Core.Module;

internal static class FrontierRetirementChecks
{
    internal static async Task RunAsync(string root, bool observe)
    {
        var one = await CompletedCopies(root, 1);
        var hundred = await CompletedCopies(root, 100);
        var thousand = await CompletedCopies(root, 1000);
        if (!observe)
            Require((thousand.ResourceNodesVisited - hundred.ResourceNodesVisited) * 99
                == (hundred.ResourceNodesVisited - one.ResourceNodesVisited) * 900,
                $"Completed descendants were scanned again: visits {one.ResourceNodesVisited}/{hundred.ResourceNodesVisited}/{thousand.ResourceNodesVisited}.");
        StaleLease(root);
        await FailureAndRetry(root);
        Console.WriteLine($"PASS: completed-copy frontiers 1/100/1000 visits {one.ResourceNodesVisited}/{hundred.ResourceNodesVisited}/{thousand.ResourceNodesVisited}; stale lease and fault/cancel dependencies; nine failure/Bool/retry artifact executions.");
    }

    private static void StaleLease(string root)
    {
        var indexType = typeof(Interpreter).GetNestedType("ResourceIndex", BindingFlags.NonPublic)!;
        var keyType = typeof(Interpreter).GetNestedType("ResourceKey", BindingFlags.NonPublic)!;
        var index = Activator.CreateInstance(indexType, nonPublic: true)!;
        var key = Activator.CreateInstance(keyType, ["local", Path.Combine(root, "lease-target")])!;
        var add = indexType.GetMethod("Add")!;
        var remove = indexType.GetMethod("Remove")!;
        var query = indexType.GetMethod("Dependencies")!;
        var keys = Array.CreateInstance(keyType, 1); keys.SetValue(key, 0);
        var empty = Array.CreateInstance(keyType, 0);
        HashSet<Task> Reads() => (HashSet<Task>)query.Invoke(index, [keys, empty])!;
        object Add(Task task) => add.Invoke(index, [task, null, key])!;

        var first = Task.FromResult(true); var second = Task.FromResult(true);
        var firstLease = Add(first); var secondLease = Add(second);
        Require(Reads().Count == 0, "Successful completed writes remained active dependencies.");
        remove.Invoke(index, [first, firstLease]);
        TaskCompletionSource<bool> fresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = Add(fresh.Task);
        remove.Invoke(index, [second, secondLease]);
        Require(Reads().Contains(fresh.Task), "A retired old lease removed a new same-path resource node.");
        fresh.SetResult(true);
        var failed = Task.FromException<bool>(new IOException("selected transfer failure"));
        _ = failed.Exception;
        var canceled = Task.FromCanceled<bool>(new CancellationToken(canceled: true));
        _ = Add(failed); _ = Add(canceled);
        var retained = Reads();
        Require(retained.Contains(failed) && retained.Contains(canceled), "Faulted or canceled work disappeared from resource dependencies before observation.");
    }

    private static async Task<SchedulingStatistics> CompletedCopies(string root, int count)
    {
        var directory = Path.Combine(root, "retirement-" + count);
        var output = Path.Combine(directory, "output");
        Directory.CreateDirectory(output);
        List<Value> constants = [new StringValue("completed 可莉\n"), new PathValue(output)];
        List<Instruction> code = [];
        for (var index = 0; index < count; index++)
        {
            constants.Add(new PathValue(Path.Combine(output, "file-" + index)));
            code.Add(new(OpCode.Constant, 0)); code.Add(new(OpCode.Constant, index + 2));
            code.Add(new(OpCode.Transfer)); code.Add(new(OpCode.Pop));
            code.Add(new(OpCode.Acquire));
            code.Add(new(OpCode.Constant, 1)); code.Add(new(OpCode.NativeCall, 1, Text: "fs.exists")); code.Add(new(OpCode.Pop));
        }
        code.Add(new(OpCode.Null)); code.Add(new(OpCode.Return));
        Module module = new(constants, [new("completed-frontier", 0, 0, code, TypeRef.Null)], OptimizationLevel: 0);
        Artifact.Validate(module);
        Interpreter interpreter = new(module, new(WorkingDirectory: directory, Jobs: 4, Output: TextWriter.Null, Error: TextWriter.Null));
        await interpreter.ExecuteAsync();
        for (var index = 0; index < count; index++)
            Require(await File.ReadAllTextAsync(Path.Combine(output, "file-" + index)) == "completed 可莉\n", "A completed publication disappeared.");
        Require(interpreter.FailedTransfers == 0, "Completed-frontier copies failed.");
        Require(interpreter.SchedulingStatistics.AdmittedTransfers == count, "A completed-frontier copy was skipped.");
        Require(interpreter.SchedulingStatistics.DependencyEdges == 0, "Completed work created an active dependency edge.");
        return interpreter.SchedulingStatistics;
    }

    private static async Task FailureAndRetry(string root)
    {
        for (var stage = 0; stage < 3; stage++)
        {
            var directory = Path.Combine(root, "retirement-errors-" + stage);
            Directory.CreateDirectory(directory);
            var options = new ExecutionOptions(WorkingDirectory: directory, Jobs: 2, Output: TextWriter.Null, Error: TextWriter.Null);
            Module Source(string text) => Compiler.Compile(text, "frontier-errors.msync", 2);
            Module Prepare(Module module)
            {
                if (stage == 0) return module;
                using MemoryStream bytes = new();
                Artifact.Write(module, bytes); bytes.Position = 0;
                module = Artifact.Read(bytes);
                if (stage == 1) return module;
                module = Binding.Bind(module, new(directory, directory));
                using MemoryStream executable = new();
                Artifact.Write(module, executable); executable.Position = 0;
                return Artifact.Read(executable);
            }

            const string failedSource = "\"kept\" -> ./kept; acquire; fn fail() { ./missing -> ./failed; acquire; }; fail(); \"later\" -> ./after;";
            Interpreter failed = new(Prepare(Source(failedSource)), options);
            try
            {
                await failed.ExecuteAsync();
                throw new InvalidOperationException("A discarded failed transfer was retired before acquire/owner observed it.");
            }
            catch (MsyncException exception)
            {
                Require(exception.Message.Contains("failed", StringComparison.OrdinalIgnoreCase), "The failure lost its transfer diagnostic.");
            }
            Require(failed.FailedTransfers == 1, "A failed transfer was lost from accounting.");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "kept")) == "kept", "Failure rolled back an earlier completed file.");
            Require(!File.Exists(Path.Combine(directory, "after")), "Execution crossed a failed explicit acquire.");

            Interpreter consumed = new(Prepare(Source("let ok = ./missing -> ./reported; acquire; if not ok { \"observed\" -> ./false-observed; };")), options);
            await consumed.ExecuteAsync();
            Require(consumed.FailedTransfers == 1, "A consumed failure was lost from accounting.");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "false-observed")) == "observed", "A consumed false became an owner exception instead of a Bool.");

            await File.WriteAllBytesAsync(Path.Combine(directory, "missing"), Encoding.UTF8.GetBytes("retry 可莉\n"));
            Interpreter retry = new(Prepare(Source(failedSource)), options);
            await retry.ExecuteAsync();
            Require(retry.FailedTransfers == 0, "Retry inherited a completed failure.");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "failed")) == "retry 可莉\n", "Retry did not publish actual corrected bytes.");
            Require(await File.ReadAllTextAsync(Path.Combine(directory, "after")) == "later", "Successful retry did not continue after acquire.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
