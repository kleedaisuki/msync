using Msync.Compiler.Syntax;
using Msync.Core;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private static void ValidateGlobalImports(Frame frame, FunctionExpr function)
    {
        void Check(Node node)
        {
            if (node is FunctionExpr) return;
            if (node is GlobalStatement global)
                foreach (var name in global.Names)
                {
                    var parameter = function.Parameters.FirstOrDefault(parameter => parameter.Name == name.Name);
                    if (parameter is not null)
                    {
                        var error = Error(frame, $"global '{name.Name}' conflicts with a parameter.", name.Span,
                            help: "Use a different parameter name. A global import refers to a binding in this function's lexical ancestry.", code: "E0214");
                        error.Diagnostic.Related.Add(new(frame.SourceName, frame.Source, parameter.Span, "conflicting parameter declared here"));
                        throw error;
                    }
                    if (!frame.Environment.ContainsKey(name.Name))
                        throw Error(frame, $"global '{name.Name}' was not declared before this function definition.", name.Span,
                            help: "Declare the binding in a lexical ancestor before defining this fn, or pass the value as a parameter. Caller locals and later declarations are not global imports.", code: "E0214");
                }
            foreach (var child in Children(node)) Check(child);
        }
        Check(function.Body);
    }

    private Info ImportGlobals(Frame frame, GlobalStatement statement)
    {
        if (frame.GlobalBindings is null)
            throw Error(frame, "global imports are only meaningful inside an fn.", statement.Span,
                help: "Top-level bindings already share their lexical environment. Use 'global name;' inside the function that imports an ancestor binding.", code: "E0214");
        if (editorContexts is not null && IsCurrentSource(frame)) editorContexts.Import(frame, statement);
        foreach (var name in statement.Names)
        {
            bool available = frame.GlobalBindings.TryGetValue(name.Name, out var ancestor);
            if (frame.Environment.TryGetValue(name.Name, out var existing) && (!available || !ReferenceEquals(existing.Identity, ancestor!.Identity)))
            {
                var error = Error(frame, $"global '{name.Name}' conflicts with a parameter or local binding.", name.Span,
                    help: "Use a different local or parameter name. An explicit global import cannot overwrite another lexical binding.", code: "E0214");
                if (existing.Origin is { } local) error.Diagnostic.Related.Add(new(local.SourceName, local.Source, local.Span, "conflicting local binding declared here"));
                throw error;
            }
            if (!available)
                throw Error(frame, $"global '{name.Name}' was not declared before this function definition.", name.Span,
                    help: "Declare the binding in a lexical ancestor before defining this fn, or pass the value as a parameter. Caller locals and later declarations are not global imports.", code: "E0214");
            var info = Current(frame, ancestor!);
            frame.Bind(name.Name, ancestor! with { Info = info });
            Observe(frame, name.Span, info, name.Name);
            if (ancestor!.Origin is { } origin) CaptureDefinition(frame, name.Span, [origin]);
        }
        frame.Emit(OpCode.Null, span: statement.Span);
        return new(TypeRef.Null);
    }
}
