using Msync.Compiler.Syntax;

static Expr ParseExpression(string source) => ((ExpressionStatement)Parser.Parse(source).Statements[0]).Expression;
static void Require(bool condition, string context)
{
    if (!condition) throw new InvalidOperationException(context);
}

var assignment = (AssignmentExpr)ParseExpression("object::filename = ~/.codex;");
Require(assignment.Target is BinaryExpr { Operator: "::", Right: NameExpr { Name: "filename" } }, "field target");
Require(assignment.OperatorSpan is { Start: 17, Length: 1 }, "assignment operator source span");
Require(ParseExpression("first = second = true") is AssignmentExpr { Value: AssignmentExpr }, "right-associative assignment");
Require(ParseExpression("value = false or true") is AssignmentExpr { Value: BinaryExpr { Operator: "or" } }, "assignment precedence");
Require(ParseExpression("destination = source -> output") is AssignmentExpr { Value: BinaryExpr { Operator: "->" } }, "transfer precedence");
Require(ParseExpression(".codex = ~/.codex") is AssignmentExpr { Target: NameExpr { Name: ".codex" } }, "dot binding assignment");
Require(ParseExpression("value=true") is AssignmentExpr { Target: NameExpr { Name: "value" }, Value: BoolExpr { Value: true } }, "unspaced assignment");
Require(ParseExpression("./\"source=1\" -> ./\"output=2\"") is BinaryExpr { Operator: "->", Left: BinaryExpr { Operator: "/", Right: StringExpr { Value: "source=1" } } }, "quoted equals filenames");

const string declaration = "scope SomeType { remote: ssh://forge, new: fn { scope { filename?: null } }, }; let object = SomeType::new(); object::filename = ~/.codex;";
var document = Parser.Parse(declaration);
var scope = ((ScopeDeclaration)document.Statements[0]).Scope;
Require(!scope.Members[0].Nullable, "ordinary field remains nonnullable");
var constructor = (FunctionExpr)scope.Members[1].Value;
var result = (ScopeExpr)((ExpressionStatement)constructor.Body.Statements[0]).Expression;
Require(result.Members[0] is { Name: "filename", Nullable: true, Value: NullExpr }, "nullable anonymous returned scope");
Require(((ScopeExpr)ParseExpression("scope { \"two words\"?: null, .codex?: null }")).Members.All(member => member.Nullable), "nullable quoted and dot fields");
string formatted = Formatter.Format(declaration);
Require(Formatter.Format(formatted) == formatted, "nullable field and assignment formatting idempotence");
Require(formatted.Contains("filename?: null", StringComparison.Ordinal), "formatter preserves field admission");
var incomplete = Parser.ParseForEditor("scope SomeType { field?: null, new: fn { scope { filename?:");
Require(incomplete.Error is not null && incomplete.Document.Statements.Count == 1, "incomplete nullable scope recovery");
Console.WriteLine("Mutable assignment and nullable field syntax: 15 contracts passed.");
