#if MSYNC_OPTIMIZATION
using Msync.Optimization;
#endif
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Msync.Core;
using Module = Msync.Core.Module;
using SourceCompiler = Msync.Compiler.Compiler;

string workload = args.Length == 0 ? "bool" : args[0];
int samples = args.Length < 2 ? 15 : int.Parse(args[1], System.Globalization.CultureInfo.InvariantCulture);
string source = workload switch
{
    "none" => "let value = true; value",
    "bool" => "$std::bool::both(true, false)",
    "ranges_common" => "$std::ranges::first($std::ranges::take([1, 2, 3], 2))",
    "ranges" => "$std::ranges::size($std::ranges::take([1, 2, 3], 2))",
    "cast" => "\"42\" as Number",
    "functional" => "let op = $std::functional::compose($std::string::to_uppercase, $std::string::trim); op(\" hello \")",
    "legacy_list" => "let list = $std::collections::List([1, 2, 3]); list::size()",
    "container_list" => "let list = $std::containers::List::new([1, 2, 3]); $std::containers::List::size(list)",
    "container_module" => "let module = $std::containers; let list = module::List::new([1, 2, 3]); module::List::size(list)",
    "meta" => "$std::meta::is_convertible(Path, String)",
    "shell" => "$std::shell::pwd()",
    "shell_prelude" => ShellPrelude(),
    _ => throw new ArgumentException($"Unknown workload '{workload}'")
};

long allocated = GC.GetTotalAllocatedBytes(precise: true);
long started = Stopwatch.GetTimestamp();
Module cold = SourceCompiler.Compile(source, "stdlib-load.msync", optimization: 0);
double coldMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
long coldBytes = GC.GetTotalAllocatedBytes(precise: true) - allocated;
for (int index = 0; index < 3; index++) SourceCompiler.Compile(source, "stdlib-load.msync", optimization: 0);
double[] milliseconds = new double[samples];
long[] bytes = new long[samples];
Module? retained = null;
for (int index = 0; index < samples; index++)
{
    allocated = GC.GetTotalAllocatedBytes(precise: true);
    started = Stopwatch.GetTimestamp();
    retained = SourceCompiler.Compile(source, "stdlib-load.msync", optimization: 0);
    milliseconds[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    bytes[index] = GC.GetTotalAllocatedBytes(precise: true) - allocated;
}

double[] sorted = milliseconds.Order().ToArray();
double Percentile(double fraction)
{
    double position = (sorted.Length - 1) * fraction;
    int first = (int)position;
    int last = Math.Min(first + 1, sorted.Length - 1);
    return sorted[first] + (sorted[last] - sorted[first]) * (position - first);
}
var executable = Environment.ProcessPath!;
var report = new JsonObject
{
    ["workload"] = workload,
    ["source"] = source,
    ["executable"] = executable,
    ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(executable))),
    ["compiler_build"] = typeof(SourceCompiler).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    ["core_build"] = typeof(Optimizer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
    ["compiler_configuration"] = typeof(SourceCompiler).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration,
    ["compiler_sha256"] = File.Exists(Path.Combine(AppContext.BaseDirectory, "Msync.Compiler.dll"))
        ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Msync.Compiler.dll")))) : null,
    ["runtime"] = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    ["cold_o0_milliseconds"] = coldMilliseconds,
    ["cold_o0_allocated_bytes"] = coldBytes,
    ["warm_o0_p50_milliseconds"] = Percentile(0.5),
    ["warm_o0_p95_milliseconds"] = Percentile(0.95),
    ["warm_o0_allocated_bytes_mean"] = bytes.Average(),
    ["warm_o0_milliseconds"] = new JsonArray(milliseconds.Select(value => JsonValue.Create(value)).ToArray()),
    ["warm_o0_allocated_bytes"] = new JsonArray(bytes.Select(value => JsonValue.Create(value)).ToArray()),
    ["raw_functions"] = cold.Functions.Count,
    ["raw_constants"] = cold.Constants.Count,
    ["raw_instructions"] = cold.Functions.Sum(function => function.Instructions.Count),
    ["raw_locals"] = cold.Functions.Sum(function => function.LocalCount),
    ["raw_function_names"] = new JsonArray(cold.Functions.Select(function => JsonValue.Create(function.Name)).ToArray())
};
Console.WriteLine(report.ToJsonString());
GC.KeepAlive(retained);

static string ShellPrelude()
{
    var assembly = typeof(SourceCompiler).Assembly;
    string resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(".shell.msync", StringComparison.Ordinal));
    using Stream stream = assembly.GetManifestResourceStream(resource)!;
    using StreamReader reader = new(stream);
    var document = Msync.Compiler.Syntax.Parser.Parse(reader.ReadToEnd(), "msync-stdlib:///0.8.0/shell.msync");
    return string.Join('\n', document.Statements.Select(statement => statement switch
    {
        Msync.Compiler.Syntax.FunctionDeclaration function => function.Name,
        Msync.Compiler.Syntax.NativeDeclaration native => native.Name,
        Msync.Compiler.Syntax.ScopeDeclaration scope => scope.Name,
        Msync.Compiler.Syntax.LetStatement { Pattern: Msync.Compiler.Syntax.NamePattern binding } => binding.Name,
        _ => null
    }).OfType<string>().Select(name => $"let {name} = $std::shell::{name};"));
}
