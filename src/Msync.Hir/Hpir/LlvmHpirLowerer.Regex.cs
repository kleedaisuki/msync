using Msync.Core;
using Msync.Core.Hpir;
using Msync.Core.Llvm;
using Msync.Core.Native;

namespace Msync.Hir;

public static partial class LlvmHpirLowerer
{
    private sealed partial class Geometry
    {
        private readonly Dictionary<(HpirRegexOp, int), int> regexImports = [];

        internal bool RegexOperation(LlvmCallInstruction call, out HpirRegexOp operation)
        {
            operation = default;
            if (call.CalledFunction is not { } target) return false;
            if (graph.NativeCalls.TryGetValue(target, out string? name)) return HpirRegexImports.TryNative(name, out operation);
            if (graph.RuntimeCalls.GetValueOrDefault(target) != NativeBinary.RegexMatch) return false;
            operation = IsPathRecord(call.GetArgument(0).Type) ? HpirRegexOp.MatchPath : HpirRegexOp.MatchString;
            return true;
        }

        internal int RegexLiteral(LlvmValue value) => TryContextualLiteral(value, out var literal)
            && literal.Kind == NativeKind.String ? TextLiteral(literal.Text ?? "") : -1;

        internal int RegexImport(HpirRegexOp operation, int literal)
        {
            var key = (operation, literal);
            if (regexImports.TryGetValue(key, out int import)) return import;
            if (HpirRegexImports.ProducesText(operation)) _ = TextLayout();
            import = Imports.Count;
            Imports.Add(HpirRegexImports.Create(operation, literal));
            regexImports.Add(key, import);
            return import;
        }
    }

    private sealed partial class Body
    {
        private int regexCalls, regexLiterals, regexPaths;

        private bool TryReadyRegex(LlvmCallInstruction call)
        {
            if (!geometry.RegexOperation(call, out var operation)) return TryRegexFind(call);
            bool text = HpirRegexImports.ProducesText(operation);
            if (call.ArgumentCount != HpirRegexImports.Arity(operation)
                || (text ? !geometry.IsGuestPointer(call.Type) : !call.Type.IsIntegerTy(1)))
                throw Error("a Regex call outside its selected signature", call);

            LlvmValue subject = call.GetArgument(0);
            bool path = operation == HpirRegexOp.MatchPath;
            if (operation == HpirRegexOp.MatchString)
            {
                TypeKind? kind = geometry.ReflectedType(call, 0)?.Kind;
                if (kind is null && subject is LlvmConstantPointerNull) kind = TypeKind.Null;
                if (kind is { } known && (known & (TypeKind.String | TypeKind.Path)) == 0 && !FutureRoot(subject))
                {
                    Add(new(HpirOp.ConstU8, Location(call)), call.Source);
                    return true;
                }
                if (kind is not (TypeKind.String or (TypeKind.String | TypeKind.Null)))
                    throw Error("a Regex subject without a known String or Path representation", call);
            }
            foreach (var argument in call.Arguments)
                if (FutureRoot(argument) || (!path || argument != subject) && !geometry.IsGuestPointer(argument.Type))
                    throw Error("a Regex call without fixed ready operands", call);

            int literal = operation == HpirRegexOp.Escape ? -1 : geometry.RegexLiteral(call.GetArgument(1));
            var arguments = new List<int>(call.ArgumentCount) { path ? PathRecordAddress(subject, call) : Location(subject) };
            if (operation != HpirRegexOp.Escape && literal < 0) arguments.Add(Location(call.GetArgument(1)));
            if (operation == HpirRegexOp.Replace) arguments.Add(Location(call.GetArgument(2)));
            EmitImportCall(call, geometry.RegexImport(operation, literal), arguments.ToArray(), safepoint: text);
            ++regexCalls;
            if (literal >= 0) ++regexLiterals;
            if (path) ++regexPaths;
            return true;
        }

        private void ReportRegex(CompilationTrace? trace)
        {
            trace?.Metric("regex.calls", regexCalls);
            trace?.Metric("regex.literal_patterns", regexLiterals);
            trace?.Metric("regex.path_subjects", regexPaths);
            trace?.Metric("regex.finds", regexFinds);
            trace?.Metric("regex.group_layout_checks_elided", regexGroupLayoutChecksElided);
        }
    }
}
