using System.Runtime.InteropServices;
using Msync.Compiler;
using Msync.Core.Native;
using Msync.Runtime;
using Msync.Runtime.Native;

internal static class PrivateInitializationChecks
{
    internal static async Task<int> RunAsync()
    {
        const string source = "let n = $std::io::read_stdin() as Number; $std::math::floor(n + 1.75) * 2";
        NativeProgram program = NativePreparation.Prepare(Compiler.Compile(source, "private-initialization.msync", 0));
        NativeProgram tracked = program with
        {
            Functions = program.Functions.Select(function => function with
            {
                Storage = function.Storage! with
                {
                    Slots = function.Storage!.Slots.Select(slot => slot & ~NativeSlotStorage.UnobservedInitialization).ToArray()
                }
            }).ToArray()
        };
        NativeProgram conservative = program with
        {
            Functions = program.Functions.Select(function => function with
            {
                Storage = function.Storage! with
                {
                    Slots = function.Storage!.Slots.Select(slot => slot & ~(NativeSlotStorage.PrivateArgument | NativeSlotStorage.UnobservedInitialization)).ToArray()
                }
            }).ToArray(),
            Backend = program.Backend! with
            {
                Functions = program.Backend!.Functions.Select(function => function with
                {
                    Instructions = function.Instructions.Select(instruction => instruction.Handler == NativeBackendHandler.CompiledCopy
                        ? instruction with { Handler = NativeBackendHandler.Standard } : instruction).ToArray()
                }).ToArray()
            }
        };
        foreach ((Architecture architecture, bool windows) in new[] { (Architecture.X64, false), (Architecture.X64, true), (Architecture.Arm64, false) })
        {
            (int bytes, int removed, int writes) = Emit(program, architecture, windows);
            (int guardedBytes, int guardedRemoved, _) = Emit(conservative, architecture, windows);
            (int trackedBytes, int trackedChecks, int trackedWrites) = Emit(tracked, architecture, windows);
            if (removed == 0 || guardedRemoved != 0 || bytes >= guardedBytes)
                throw new InvalidOperationException($"{architecture}/{windows}: private proof did not remove initialization code ({bytes}/{guardedBytes}, {removed}/{guardedRemoved})");
            if (writes == 0 || trackedWrites != 0 || trackedChecks != removed || bytes >= trackedBytes)
                throw new InvalidOperationException($"{architecture}/{windows}: unobserved flags still require native stores");
        }
        foreach (NativeProgram variant in new[] { program, tracked, conservative })
        {
            using StringReader input = new("41");
            RuntimeProfile profile = new();
            await using NativeInterpreter interpreter = new(variant,
                new() { Input = input, Profile = profile, Tiers = new(NativeJitPolicy.Force, CallThreshold: 1) });
            if ((await interpreter.ExecuteAsync()).AsNumber() != 84)
                throw new InvalidOperationException("Initialization proof changed scalar execution");
            var samples = profile.Drain().Where(sample => sample.Name == "vm.function").ToArray();
            if (ReferenceEquals(variant, program)
                && (samples.Sum(sample => (long)sample.Metrics["jit_initialization_checks_elided"]) == 0
                    || samples.Sum(sample => (long)sample.Metrics["jit_initialization_writes_elided"]) == 0))
                throw new InvalidOperationException("Missing actual initialization-elision telemetry");
        }
        await BorrowedArgument();
        return 9;
    }

    private static async Task BorrowedArgument()
    {
        NativeProgram program = NativePreparation.Prepare(Compiler.Compile(
            "fn twice(n: Number) { n*2 }; let n=$std::io::read_stdin() as Number; twice(n+1)", "borrowed-init.msync", 0));
        int borrows = 0;
        foreach (NativeFunctionCode function in program.Functions)
            foreach (NativeCallStorage call in function.Storage!.Calls)
                for (int argument = 0; argument < call.Borrowed.Count; argument++)
                {
                    if (!call.Borrowed[argument]) continue;
                    borrows++;
                    int slot = function.Instructions[call.Instruction].Inputs![argument];
                    if (function.Storage.Slots[slot].HasFlag(NativeSlotStorage.UnobservedInitialization))
                        throw new InvalidOperationException("A borrowed source lost its observable initialization flag");
                }
        if (borrows == 0) throw new InvalidOperationException("Fixture lost its borrowed call");
        using MemoryStream bytes = new();
        NativeArtifact.WriteProduced(program, bytes);
        bytes.Position = 0;
        program = NativeArtifact.Read(bytes);
        foreach (NativeJitPolicy policy in new[] { NativeJitPolicy.Off, NativeJitPolicy.Force })
        {
            using StringReader input = new("41");
            await using NativeInterpreter interpreter = new(program, new() { Input = input, Tiers = new(policy, CallThreshold: 1) });
            if ((await interpreter.ExecuteAsync()).AsNumber() != 84)
                throw new InvalidOperationException("Native flag elision broke borrowed argument execution");
        }
    }

    private static (int Bytes, int Elisions, int Writes) Emit(NativeProgram program, Architecture architecture, bool windows)
    {
        NativeImage image = TierFixture.Link(program);
        using NativeExecutionFunction function = new(image, program.EntryPoint, new(NativeJitPolicy.Force));
        NativeBackendFunction backend = program.Backend!.Functions[program.EntryPoint];
        if (!ReferenceEquals(function.RawWritable, backend.RawWritable) || !ReferenceEquals(function.ExportBackend(), backend))
            throw new InvalidOperationException("Execution duplicated authoritative backend metadata");
        using NativeCompiledFunction version = NativeCompiledFunction.Compile(image, function, NativeJitPolicy.Force)!;
        if (function.ObservedProfiles is not null)
            throw new InvalidOperationException("Compilation allocated profiles without any observations");
        byte[] code = NativeMachineCode.Emit(function, version.Operations, architecture, windows,
            1, 2, 3, program.Constants, out NativeInitializationElisions removed, version.Layout, 4);
        if (program.Functions.Any(item => item.Storage!.Slots.Any(slot => slot.HasFlag(NativeSlotStorage.UnobservedInitialization)))
            && removed.Writes == 0)
            throw new InvalidOperationException("Native code still writes every private initialization flag");
        return (code.Length, removed.Checks, removed.Writes);
    }
}
