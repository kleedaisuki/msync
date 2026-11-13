using Msync.Compiler.Syntax;
using Msync.Core;

namespace Msync.Compiler;

internal sealed class StandardSource
{
    private readonly Lazy<string> source;
    private readonly Lazy<Document> document;
    public string Source => source.Value;
    public Document GetDocument(CompilationTrace? trace) => trace is null ? document.Value
        : trace.MeasureDetailed("compiler.standard.document", this, static self => self.document.Value,
            attributes: new Dictionary<string, string> { ["module.name"] = moduleName,
                ["cache.state"] = document.IsValueCreated ? "warm" : "cold" },
            metrics: static result => new Dictionary<string, ulong> { ["statements"] = (ulong)result.Statements.Count });
    private readonly string moduleName;
    public Document Document => document.Value;
    public StandardSource(string resource, string module)
    {
        moduleName = module;
        source = new(() =>
        {
            using var stream = typeof(Compiler).Assembly.GetManifestResourceStream(resource)!;
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        });
        document = new(() => Parser.Parse(source.Value, $"msync-stdlib:///0.8.0/{module}.msync"));
    }
}
