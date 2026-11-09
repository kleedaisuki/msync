using Msync.Compiler.Syntax;

namespace Msync.Compiler;

internal sealed class StandardSource
{
    private readonly Lazy<string> source;
    private readonly Lazy<Document> document;
    public string Source => source.Value;
    public Document Document => document.Value;
    public StandardSource(string resource, string module)
    {
        source = new(() =>
        {
            using var stream = typeof(Compiler).Assembly.GetManifestResourceStream(resource)!;
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        });
        document = new(() => Parser.Parse(source.Value, $"msync-stdlib:///0.8.0/{module}.msync"));
    }
}
