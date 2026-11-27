using Msync.Core.Native;

if (args is ["--artifact-revision"])
{
    await ArtifactRevisionChecks.Run();
    return;
}

if (args is ["--inspect", var path])
{
    NativeProgram program = NativeArtifact.Read(path);
    for (int fn = 0; fn < program.Functions.Count; fn++)
    {
        Console.WriteLine($"fn {fn}: {program.Functions[fn].Name}");
        foreach (var (code, pc) in program.Backend!.Functions[fn].Instructions.Select((code, pc) => (code, pc)))
            Console.WriteLine($"{pc}: {code.Op} r={code.Result} a={code.A} b={code.B} c={code.C} imm={code.Immediate} span={code.Span} handler={code.Handler} u64={code.Numbers}" +
                (code.Op == NativeOp.Constant ? $" constant={program.Constants[code.A]}" : ""));
    }
    return;
}

await BackendChecks.Run();

internal static class BackendChecks
{
    private static int checks;
    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new InvalidOperationException(message);
    }

    internal static async Task Run()
    {
        (string Name, string Source, double Expected)[] cases =
        [
            ("unsigned", "let i=0; let sum=0; loop { if i>=1000 {break;}; sum=sum+(i*3+7)%97; i=i+1; }; sum", Enumerable.Range(0, 1000).Sum(i => (i * 3 + 7) % 97)),
            ("fractional-boundary", "let i=0; let sum=0; loop { if i>=129 {break;}; sum=sum+(i*3+7)/2; i=i+1; }; sum", Enumerable.Range(0, 129).Sum(i => (i * 3 + 7) / 2d)),
            ("negative-remainder", "let i=0; let sum=0; loop { if i>=129 {break;}; sum=sum+(i - 1001)%97; i=i+1; }; sum", Enumerable.Range(0, 129).Sum(i => (i - 1001) % 97)),
            ("rounding-boundary", "let i=0; let value=9007199254740992; loop { if i>=129 {break;}; value=value+1; i=i+1; }; value", 9007199254740992d),
            ("signed-zero", "let i=0; let value=0.0; loop { if i>=129 {break;}; value=-value; i=i+1; }; value", -0d),
            ("zero-divisor", "let i=0; let value=0; loop { if i>=129 {break;}; value=i%0; i=i+1; }; value", double.NaN),
            ("captured-write", "let n=1; fn change() {n=0.25;}; let before=n; change(); before+n", 1.25),
            ("alias-write", "let row=object {n:1}; fn change(row) {row::n=0.25;}; let before=row::n; change(row); before+row::n", 1.25)
        ];
        foreach ((string name, string source, double expected) in cases)
            foreach (int level in new[] { 0, 2 })
            {
                NativeProgram input = Msync.Compiler.Compiler.Compile(source, name + ".msync", level);
                List<Msync.Core.CompilationObservation> observations = [];
                NativeProgram bound = Msync.Runtime.Native.NativePreparation.Prepare(input,
                    new Msync.Core.BindingContext(Environment.CurrentDirectory, Environment.CurrentDirectory, name + ".msync"),
                    trace: new(observations.Add, true));
                Check(bound.Backend is not null, "Binding did not compile an executable backend");
                Check(observations.Any(item => item.Name == "backend.function"), "Missing backend phase attribution");
                using MemoryStream bytes = new();
                NativeArtifact.WriteProduced(bound, bytes); bytes.Position = 0;
                bound = NativeArtifact.Read(bytes);
                foreach (Msync.Runtime.Native.NativeJitPolicy policy in new[] { Msync.Runtime.Native.NativeJitPolicy.Off, Msync.Runtime.Native.NativeJitPolicy.Force })
                {
                    Msync.Runtime.RuntimeProfile profile = new();
                    await using Msync.Runtime.Native.NativeInterpreter interpreter = new(bound, new()
                    { Profile = profile, Tiers = new(policy, CallThreshold: 1, BackedgeThreshold: 1) });
                    double actual = (await interpreter.ExecuteAsync()).AsNumber();
                    Check(double.IsNaN(expected) ? double.IsNaN(actual)
                        : BitConverter.DoubleToInt64Bits(actual) == BitConverter.DoubleToInt64Bits(expected),
                        $"{name} O{level} {policy}: expected {expected:R}, got {actual:R}");
                    if (name == "unsigned" && level == 2)
                    {
                        Check(bound.Backend!.Functions.SelectMany(function => function.Instructions)
                            .Any(code => code.Handler == NativeBackendHandler.UnsignedBinary), "Unsigned operations were not specialized");
                        if (policy == Msync.Runtime.Native.NativeJitPolicy.Force)
                            Check(profile.Drain().Where(item => item.Name == "vm.function").Sum(item => (long)item.Metrics["jit_u64_operations"]) > 0,
                                "Machine code did not contain u64 operations");
                    }
                }
            }
        await Connections();
        Console.WriteLine($"PASS {checks} native backend representation and resource contracts");
    }

    private static async Task Connections()
    {
        const string source = "let remote=ssh://example; let other=ssh://example; let path=remote @ ./item; path";
        NativeProgram program = Msync.Runtime.Native.NativePreparation.Prepare(Msync.Compiler.Compiler.Compile(source, "connections.msync", 2));
        Check(program.Backend!.Connections.Count(value => value.Kind == Msync.Core.ConnectionKind.Ssh && value.Address == "example") == 1,
            "Equal connection descriptions were not interned");
        await using Msync.Runtime.Native.NativeInterpreter interpreter = new(program);
        NativePath path = (await interpreter.ExecuteAsync()).AsPath();
        Check(path.Connection == new NativeConnection(Msync.Core.ConnectionKind.Ssh, "example") && path.Raw == "./item", "Connection qualification changed descriptor identity");
        foreach (NativeConnection connection in new[] { new NativeConnection(Msync.Core.ConnectionKind.Ssh, "user@example:2222"), new NativeConnection(Msync.Core.ConnectionKind.Wsl, "Ubuntu") })
        {
            System.Diagnostics.ProcessStartInfo start = Msync.Runtime.Native.NativeHostIO.CreateRemoteShellProcessStartInfo(connection);
            Check(start.FileName == (connection.Kind == Msync.Core.ConnectionKind.Ssh ? "ssh" : "wsl.exe"), "Wrong native endpoint implementation");
        }
    }
}
