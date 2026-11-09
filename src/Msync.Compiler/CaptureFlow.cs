using Msync.Compiler.Syntax;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private readonly Dictionary<(FunctionExpr Function, string Name), bool> parameterWrites = [];

    private bool ParameterMayBeWritten(FunctionExpr function, string name)
    {
        var key = (function, name);
        if (parameterWrites.TryGetValue(key, out bool saved)) return saved;
        bool written = FindParameterWrite(function.Body, name);
        parameterWrites.Add(key, written);
        return written;
    }

    private static bool FindParameterWrite(BlockExpr body, string name)
    {
        return Writes(body, true, false);

        bool Writes(Node node, bool visible, bool global)
        {
            switch (node)
            {
                case AssignmentExpr { Target: NameExpr target } assignment:
                    return visible && target.Name == name || Writes(assignment.Value, visible, global);
                case BlockExpr block:
                    return Block(block, visible, global);
                case FunctionExpr nested:
                    return Writes(nested.Body, visible && nested.Name != name
                        && nested.Parameters.All(parameter => parameter.Name != name), visible);
                case FunctionDeclaration declaration:
                    return Writes(declaration.Function, visible && declaration.Name != name, global);
                case ScopeDeclaration declaration:
                    return Writes(declaration.Scope, visible && declaration.Name != name, global);
                case ScopeExpr scope:
                    return Scope(scope, visible && scope.Name != name, global);
                case ForExpr loop:
                    return Writes(loop.Source, visible, global)
                        || Writes(loop.Body, visible && !Binds(loop.Pattern, name), global);
                case MatchExpr match:
                    return Writes(match.Value, visible, global)
                        || match.Arms.Any(arm => Writes(arm.Value, visible && !Binds(arm.Pattern, name), global));
                default:
                    return Children(node).Any(child => Writes(child, visible, global));
            }
        }

        bool Block(BlockExpr block, bool visible, bool global)
        {
            foreach (var statement in block.Statements)
            {
                if (Writes(statement, visible, global)) return true;
                visible = statement switch
                {
                    LetStatement let when Binds(let.Pattern, name) => false,
                    FunctionDeclaration function when function.Name == name => false,
                    ScopeDeclaration scope when scope.Name == name => false,
                    NativeDeclaration native when native.Name == name => false,
                    GlobalStatement import when import.Names.Any(item => item.Name == name) => global,
                    _ => visible
                };
            }
            return false;
        }

        bool Scope(ScopeExpr scope, bool visible, bool global)
        {
            foreach (var member in scope.Members)
            {
                if (Writes(member.Value, visible, global)) return true;
                if (member.Name == name) visible = false;
            }
            return scope.Value is not null && Writes(scope.Value, visible, global);
        }
    }

    private static bool Binds(Pattern pattern, string name) => pattern switch
    {
        NamePattern { Name: not "_" } bound => bound.Name == name,
        TuplePattern tuple => tuple.Items.Any(item => Binds(item, name)),
        _ => false
    };
}
