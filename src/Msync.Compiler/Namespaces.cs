using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private Info Native(Frame frame, NativeDeclaration native, string? module)
    {
        string identity = module is null ? native.Name : $"{module}.{native.Name}";
        if (!NativeSignatures.TryGet(identity, out var signature))
        {
            var matches = NativeSignatures.All.Where(item => item.Name.EndsWith("." + identity, StringComparison.Ordinal)).ToArray();
            if (matches.Length != 1) throw Error(frame, $"Native function '{identity}' has no unique registered implementation.", native.Span);
            identity = matches[0].Name; signature = matches[0];
        }
        if ((!signature.Variadic && native.Parameters.Count != signature.Parameters.Count) || (signature.Variadic && (native.Parameters.Count < signature.Parameters.Count || native.Parameters.Count > signature.Parameters.Count + 1)))
            throw Error(frame, $"Native declaration '{identity}' has the wrong parameter count.", native.Span);
        for (int index = 0; index < native.Parameters.Count; index++)
        {
            var parameter = native.Parameters[index];
            if (signature.Variadic && index >= signature.Parameters.Count) continue;
            if (parameter.Type is null)
            {
                if (signature.ResultRule != NativeResultRule.Fixed || signature.Parameters[index].Kind == (TypeKind.Any & ~TypeKind.Null)) continue;
                throw Error(frame, "This native parameter requires an explicit type.", parameter.Span);
            }
            var actual = ResolveType(frame, parameter.Type);
            if (parameter.Nullable) actual = actual.WithNull;
            if (!signature.Parameters[index].Accepts(actual)) throw Error(frame, $"Native '{identity}' parameter '{parameter.Name}' must accept {signature.Parameters[index]}, not {actual}.", parameter.Span);
        }
        var written = ResolveType(frame, native.ReturnType);
        if (signature.ResultRule == NativeResultRule.Fixed && !written.Accepts(signature.Result))
            throw Error(frame, $"Native '{identity}' returns {signature.Result}, not {written}.", native.ReturnType.Span);
        Constant(frame, new FunctionValue(-1, NativeName: identity), native.Span);
        Info info = new(TypeRef.Function, NativeName: identity);
        return info;
    }

    private Info Standard(Frame frame, SourceSpan span)
    {
        if (frame.Environment.TryGetValue("$std", out var existing))
        {
            frame.Emit(OpCode.LoadLocal, existing.Slot); return existing.Info;
        }
        Dictionary<string, Info> members = [];
        foreach (var module in libraries.Value.Keys.Order(StringComparer.Ordinal)) members[module] = StandardModule(frame, module, span);
        frame.Emit(OpCode.MakeScope, members.Count, text: string.Join('\0', members.Keys));
        var info = new Info(TypeRef.Scope, Scope: new(members));
        frame.Emit(OpCode.Dup); Store(frame, "$std", info);
        return info;
    }
    private Info StandardModule(Frame frame, string name, SourceSpan span)
    {
        if (!libraries.Value.TryGetValue(name, out var library)) return StandardNativeModule(frame, name, span);
        Frame module = new(library.Source, $"msync-stdlib:///0.8.0/{name}.msync") { Locals = frame.Locals };
        List<string> keys = []; Dictionary<string, Info> members = [];
        foreach (var statement in library.Document.Statements)
        {
            string? memberName = null; Info? info = null;
            switch (statement)
            {
                case NativeDeclaration native: memberName = native.Name; info = Native(module, native, name); break;
                case FunctionDeclaration function: memberName = function.Name; info = Function(module, function.Function); break;
                case LetStatement { Pattern: NamePattern bound } let: memberName = bound.Name; info = Expression(module, let.Value); break;
            }
            if (memberName is null || info is null) throw Error(module, "A standard module contains only named declarations.", statement.Span);
            module.Emit(OpCode.Dup); Store(module, memberName, info);
            keys.Add(memberName); members[memberName] = info;
        }
        module.Emit(OpCode.MakeScope, keys.Count, text: string.Join('\0', keys), span: span);
        frame.Code.AddRange(module.Code); frame.Locals = module.Locals;
        var result = new Info(TypeRef.Scope, Scope: new(members));
        return result;
    }
    private Info StandardNativeModule(Frame frame, string name, SourceSpan span)
    {
        Dictionary<string, Info> members = [];
        foreach (var signature in NativeSignatures.All.Where(signature => signature.Name.StartsWith(name + ".", StringComparison.Ordinal)))
        {
            string key = signature.Name[(name.Length + 1)..];
            Constant(frame, new FunctionValue(-1, NativeName: signature.Name), span);
            var info = new Info(TypeRef.Function, NativeName: signature.Name); members[key] = info;
        }
        frame.Emit(OpCode.MakeScope, members.Count, text: string.Join('\0', members.Keys));
        return new(TypeRef.Scope, Scope: new(members));
    }


}
