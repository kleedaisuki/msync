using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private void CheckReturnConversion(Frame frame, TypeRef actual, TypeRef expected, SourceSpan span)
    {
        try { CheckConversion(frame, actual, expected, span); }
        catch (CompileException)
        {
            var error = Error(frame, $"Expected return type {expected}, found {actual}.", span,
                label: $"this returns {actual}",
                help: $"Return a {expected} value, or change this function's return annotation.", code: "E0203");
            if (frame.ExpectedReturnSpan is { } annotation)
                error.Diagnostic.Related.Add(new(frame.SourceName, frame.Source, annotation,
                    $"this function declares return type {expected}"));
            throw error;
        }
    }
}
