namespace Msync.Cli;

internal readonly record struct CompileOutput(string Path, CompileEmission Emission, bool Announce)
{
    internal static CompileOutput Resolve(string input, CommandOptions options)
    {
        string extension = options.Emission switch
        {
            CompileEmission.Hir => ".mhir",
            CompileEmission.Prepared => ".mexe",
            _ => ".mso"
        };
        string output = options.Output ?? (input == "-"
            ? throw new ArgumentException("Compiling source from stdin requires '-o FILE' or '-o -'.")
            : System.IO.Path.ChangeExtension(input, extension));
        if (output == "-") return new(output, options.Emission, false);

        string suffix = System.IO.Path.GetExtension(output).ToLowerInvariant();
        if (suffix is ".mso" or ".mexe" or ".mhir" or ".msync" && suffix != extension)
        {
            string hint = suffix switch
            {
                ".mexe" => " Use --prepare to save a prepared program.",
                ".mhir" => " Use --emit hir to export typed HIR.",
                _ => ""
            };
            throw new ArgumentException($"Selected output is {extension}; cannot write it as '{output}'.{hint}");
        }
        if (input != "-" && string.Equals(System.IO.Path.GetFullPath(input), System.IO.Path.GetFullPath(output),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException($"Compile output would overwrite its input '{input}'; choose a different -o path.");
        return new(output, options.Emission, options.Output is null);
    }

    internal string Kind => Emission switch
    {
        CompileEmission.Hir => "hir",
        CompileEmission.Prepared => "executable",
        _ => "object"
    };

    internal void Report()
    {
        if (!Announce) return;
        string description = Emission switch
        {
            CompileEmission.Hir => "typed HIR",
            CompileEmission.Prepared => "prepared program",
            _ => "portable program"
        };
        Console.WriteLine($"Wrote {Path} ({description}).");
    }
}
