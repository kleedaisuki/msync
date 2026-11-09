using System.Runtime.CompilerServices;
using Msync.Compiler.Syntax;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private static readonly ConditionalWeakTable<FunctionExpr, CaptureWrites> functionWrites = new();

    private static IReadOnlySet<int> UnwrittenLocations(FunctionExpr function) =>
        functionWrites.GetValue(function, static syntax => CaptureWrites.Function(syntax)).Unwritten;

    private sealed class CaptureWrites(IReadOnlySet<int> unwritten)
    {
        public IReadOnlySet<int> Unwritten { get; } = unwritten;

        public static CaptureWrites Function(FunctionExpr function)
        {
            Writer writer = new();
            writer.Function(function, new());
            return new(writer.Unwritten());
        }

        public static IReadOnlySet<int> Document(Document document, bool closedRoot)
        {
            Writer writer = new();
            writer.Statements(document.Statements, new(), new(), closedRoot);
            return writer.Unwritten();
        }

        private sealed class Location(int? position)
        {
            public int? Position { get; } = position;
            public bool Written { get; set; }
        }

        private sealed class LexicalScope(LexicalScope? parent = null)
        {
            private readonly LexicalScope? enclosing = parent;
            private readonly Dictionary<string, Location> bindings = new(StringComparer.Ordinal);
            public void Bind(string name, Location location) => bindings[name] = location;
            public Location? Find(string name)
            {
                for (LexicalScope? scope = this; scope is not null; scope = scope.enclosing)
                    if (scope.bindings.TryGetValue(name, out var location)) return location;
                return null;
            }
        }

        private sealed class Writer
        {
            private readonly List<Location> locations = [];
            public IReadOnlySet<int> Unwritten() => locations.Where(location => !location.Written && location.Position is not null)
                .Select(location => location.Position!.Value).ToHashSet();

            private Location Declare(int? position)
            {
                Location location = new(position);
                if (position is not null) locations.Add(location);
                return location;
            }

            public void Function(FunctionExpr function, LexicalScope captures)
            {
                LexicalScope environment = new(captures);
                if (function.Name is { } self) environment.Bind(self, Declare(null));
                foreach (var parameter in function.Parameters)
                    environment.Bind(parameter.Name, Declare(parameter.Span.Start));
                Statements(function.Body.Statements, environment, captures, true);
            }

            public void Statements(IReadOnlyList<Statement> statements, LexicalScope environment,
                LexicalScope globals, bool privateLocals)
            {
                foreach (var statement in statements)
                {
                    switch (statement)
                    {
                        case LetStatement let:
                            Visit(let.Value, environment, globals);
                            Pattern(let.Pattern, environment, privateLocals);
                            break;
                        case FunctionDeclaration function:
                            environment.Bind(function.Name, Declare(null));
                            Function(function.Function, environment);
                            break;
                        case ScopeDeclaration scope:
                            environment.Bind(scope.Name, Declare(null));
                            Visit(scope.Scope, environment, globals);
                            break;
                        case NativeDeclaration native:
                            environment.Bind(native.Name, Declare(null));
                            break;
                        case GlobalStatement import:
                            foreach (var name in import.Names)
                                environment.Bind(name.Name, globals.Find(name.Name) ?? Declare(null));
                            break;
                        default:
                            Visit(statement, environment, globals);
                            break;
                    }
                }
            }

            private void Pattern(Pattern pattern, LexicalScope environment, bool privateLocal)
            {
                if (pattern is NamePattern { Name: not "_" } name)
                    environment.Bind(name.Name, Declare(privateLocal ? name.Span.Start : null));
                if (pattern is TuplePattern tuple)
                    foreach (var item in tuple.Items) Pattern(item, environment, privateLocal);
            }

            private void Visit(Node node, LexicalScope environment, LexicalScope globals)
            {
                switch (node)
                {
                    case AssignmentExpr { Target: NameExpr target } assignment:
                        if (environment.Find(target.Name) is { } location) location.Written = true;
                        Visit(assignment.Value, environment, globals);
                        return;
                    case BlockExpr block:
                        Statements(block.Statements, new(environment), globals, true);
                        return;
                    case FunctionExpr function:
                        Function(function, environment);
                        return;
                    case ScopeExpr scope:
                        Scope(scope, environment, globals);
                        return;
                    case ForExpr loop:
                        Visit(loop.Source, environment, globals);
                        LexicalScope iteration = new(environment);
                        Pattern(loop.Pattern, iteration, true);
                        Visit(loop.Body, iteration, globals);
                        return;
                    case MatchExpr match:
                        Visit(match.Value, environment, globals);
                        foreach (var arm in match.Arms)
                        {
                            LexicalScope alternative = new(environment);
                            Pattern(arm.Pattern, alternative, true);
                            Visit(arm.Value, alternative, globals);
                        }
                        return;
                }
                foreach (var child in Children(node)) Visit(child, environment, globals);
            }

            private void Scope(ScopeExpr scope, LexicalScope parent, LexicalScope globals)
            {
                LexicalScope environment = new(parent);
                if (scope.Name is { } self) environment.Bind(self, Declare(null));
                foreach (var member in scope.Members)
                {
                    Visit(member.Value, environment, globals);
                    // Fields stay writable through aliases outside this syntax tree.
                    environment.Bind(member.Name, Declare(null));
                }
                if (scope.Value is not null) Visit(scope.Value, environment, globals);
            }
        }
    }
}
