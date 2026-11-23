using System.Text.RegularExpressions;

namespace Msync.Cli;

internal static partial class TerminalPresentation
{
    internal static void ArtifactWritten(string file, string kind, string policy)
    {
        bool color = Colors(policy);
        Console.WriteLine($"Wrote {Paint(file, CommandColor, color)} ({Paint(kind, HeadingColor, color)}).");
    }

    internal static string LlvmSyntax(string listing) => LlvmToken().Replace(listing, token =>
        Paint(token.Value, token.Groups["comment"].Success ? "90"
            : token.Groups["global"].Success ? "1;35"
            : token.Groups["local"].Success ? "36"
            : token.Groups["type"].Success ? "33"
            : token.Groups["string"].Success ? "32"
            : CommandColor, color: true));

    [GeneratedRegex("""
        (?<comment>;[^\r\n]*)|
        (?<global>@(?:"(?:\\.|[^"\\])*"|[-$.\w]+))|
        (?<local>%(?:"(?:\\.|[^"\\])*"|[-$.\w]+))|
        (?<string>"(?:\\.|[^"\\])*")|
        (?<type>\b(?:i[0-9]+|b[0-9]+|ptr|half|bfloat|float|double|x86_fp80|fp128|ppc_fp128|void|label|token|metadata|x86_amx)\b)|
        (?<operation>\b(?:define|declare|ret|br|switch|unreachable|fneg|add|fadd|sub|fsub|mul|fmul|udiv|sdiv|fdiv|urem|srem|frem|shl|lshr|ashr|and|or|xor|alloca|load|store|getelementptr|trunc|zext|sext|fptoui|fptosi|uitofp|sitofp|fptrunc|fpext|ptrtoint|ptrtoaddr|inttoptr|bitcast|addrspacecast|icmp|fcmp|phi|call|select|extractelement|insertelement|shufflevector|extractvalue|insertvalue|freeze|heapalloc|file\.in|file\.out)\b)
        """, RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant)]
    private static partial Regex LlvmToken();
}
