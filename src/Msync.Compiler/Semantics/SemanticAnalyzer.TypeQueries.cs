using Msync.Core;
using Msync.Hir;

namespace Msync.Compiler.Semantics;

internal sealed partial class SemanticAnalyzer
{
    private readonly HashSet<int> activeTypeQueries = [];

    private HirTypeId ResolveTypeQuery(int parameter, bool nullable, SemanticContext context)
    {
        if (!activeTypeQueries.Add(parameter))
        {
            if (program.TypeQueries.TryGetValue(parameter, out var recursive))
                throw Error(recursive.Source, "Circular decltype requirement.", "E0214");
            var active = activeTypeQueries.Select(id => program.TypeQueries.GetValueOrDefault(id)).FirstOrDefault(item => item is not null);
            throw Error(active?.Source ?? new HirSourceRange(new(0), 0, 0),
                "Circular type requirement.", "E0214");
        }
        try
        {
            HirTypeId result;
            if (program.TypeQueryMembers.TryGetValue(parameter, out var member))
                result = ResolveHirType(member, context);
            else
            {
                var expression = program.TypeQueries[parameter];
                var query = context.QueryFork();
                foreach (var (location, input) in expression.Inputs)
                {
                    var type = ResolveHirType(input, query);
                    var target = query.Location(location);
                    query.Values[target] = ValueForType(type);
                    query.Storage[target] = type;
                }
                var value = Analyze(expression.Operand, query);
                result = value.Scope is not null || value.Element is not null ? FlowType(value, query) : value.Type;
                Trace?.Metric("inference.decltype_queries", 1);
            }
            return nullable ? Nullable(result) : result;
        }
        finally { activeTypeQueries.Remove(parameter); }
    }

    private SemanticValue QueryTypeCall(SemanticValue callee, IReadOnlyList<SemanticValue> arguments,
        HirSourceRange source, SemanticContext context)
    {
        int signature = hir.Types[callee.Type.Value].Signature;
        if (signature < 0) throw Error(source, "decltype needs a statically described operation signature.", "E0214");
        var contract = hir.Signatures[signature];
        if (arguments.Count != contract.Parameters.Length)
            throw Error(source, "Argument count does not match the decltype operation signature.", "E0214");
        for (int i = 0; i < arguments.Count; i++)
            Require(arguments[i], contract.Parameters[i], source, context: context);
        return ValueForType(ResolveHirType(contract.Result, context));
    }
}
