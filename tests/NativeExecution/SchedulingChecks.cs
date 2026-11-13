using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;
using SourceCompiler = Msync.Compiler.Compiler;

internal static class SchedulingChecks
{
    internal static async Task<int> RunAsync()
    {
        int checks = 0;
        foreach (bool observed in new[] { false, true })
        {
            string directory = Path.GetFullPath(Path.Combine(".temp", "native-scheduling", Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(directory);
            List<RuntimeObservation> observations = [];
            NativeProgram program = SourceCompiler.CompileNative("\"native stats\" -> ./result.txt", "native-statistics.msync");
            await using NativeInterpreter interpreter = new(program, new(directory,
                Observe: observed ? observations.Add : null));
            NativeValue result = await interpreter.ExecuteAsync();
            if (!result.AsBool() || await File.ReadAllTextAsync(Path.Combine(directory, "result.txt")) != "native stats")
                throw new Exception("Native scheduling statistics changed the actual transfer result/bytes");
            SchedulingStatistics statistics = interpreter.SchedulingStatistics;
            if (statistics.AdmittedOperations != 1 || statistics.AdmittedTransfers != 1 || statistics.ResourceNodesVisited <= 0)
                throw new Exception($"Native statistics did not count actual residual admission: {statistics}");
            if (statistics.SubmittedPlans != 0 || statistics.SubmittedBatches != 0 || statistics.EliminatedTransfers != 0
                || statistics.DependencyEdges != 0 || statistics.NativeHazardWaits != 0)
                throw new Exception($"Residual execution manufactured prepared work or dependencies: {statistics}");
            if (!observed && (statistics.AdmissionNanoseconds != 0 || statistics.CohortAdmissionNanoseconds != 0
                || statistics.WaitNanoseconds != 0 || observations.Count != 0))
                throw new Exception("Disabled native scheduling observation ran clocks or emitted observations");
            if (observed && (statistics.AdmissionNanoseconds <= 0 || observations.Count(item => item is
                { Phase: "transfer", Outcome: "success", DurationNanoseconds: > 0 }) != 1))
                throw new Exception("Observed native execution omitted its actual admission/transfer interval");
            checks += 5;
        }
        Console.WriteLine("PASS native-scheduling: actual residual admission and file bytes, disabled clocks, observed transfer interval");
        return checks;
    }
}
