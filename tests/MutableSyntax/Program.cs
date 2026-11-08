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

Require(ParseExpression("release") is BarrierExpr { Release: true, Acquire: false, Value: null }, "standalone release");
Require(ParseExpression("acquire;") is BarrierExpr { Release: false, Acquire: true, Value: null }, "standalone acquire");
Require(ParseExpression("release acquire ./tool()") is BarrierExpr { Release: true, Acquire: true, Value: CallExpr }, "combined scheduling modifiers");
Require(ParseExpression("acqrel source -> target") is BarrierExpr { Release: true, Acquire: true, Value: BinaryExpr { Operator: "->" } }, "whole transfer scheduling");
Require(ParseExpression("acquire process") is BarrierExpr { Acquire: true, Value: NameExpr { Name: "process" } }, "selected process acquire");
Require(ParseExpression("release process = ./tool()") is BarrierExpr { Value: AssignmentExpr { Value: CallExpr } }, "released process assignment");
Require(ParseExpression("[release, acquire]") is RangeExpr { Items.Count: 2 }, "standalone barrier delimiter boundaries");
Require(ParseExpression("async") is NameExpr { Name: "async" }, "removed async keyword remains ordinary name");
foreach (string duplicate in new[] { "release release;", "acqrel acquire;", "acquire acqrel;" })
{
    try { Parser.Parse(duplicate); throw new InvalidOperationException("Accepted duplicate prefix: " + duplicate); }
    catch (SyntaxException syntax) { Require(syntax.Diagnostic.Message.Contains("Duplicate scheduling prefix", StringComparison.Ordinal), "duplicate prefix diagnostic"); }
}
string barriers = Formatter.Format("release; let p = release ./tool(); acquire p; acqrel source -> target;");
Require(Formatter.Format(barriers) == barriers, "barrier formatting idempotence");
Console.WriteLine("Barrier syntax: 12 contracts passed.");

static IReadOnlyList<Msync.Core.Instruction> Instructions(string source) =>
    Msync.Compiler.Compiler.Compile(source, optimization: 0).Functions[0].Instructions;
Require(Instructions("release;").Any(instruction => instruction.Op == Msync.Core.OpCode.Barrier && instruction.A == 1), "standalone Before lowering");
Require(Instructions("acquire;").Any(instruction => instruction.Op == Msync.Core.OpCode.Acquire && instruction.A == 0), "standalone join-all lowering");
Require(Instructions("release ./tool();").Single(instruction => instruction.Op == Msync.Core.OpCode.ProcessCall).B == 1, "released process lowering");
Require(Instructions("acqrel ./tool();").Single(instruction => instruction.Op == Msync.Core.OpCode.ProcessCall).B == 3, "acqrel process lowering");
Require(Instructions("./tool();").Single(instruction => instruction.Op == Msync.Core.OpCode.ProcessCall).B == 0, "ordinary process waits");
Require(Instructions("let process = release ./tool(); acquire process;").Any(instruction => instruction.Op == Msync.Core.OpCode.Acquire && instruction.A == 1), "selected process join lowering");
var generic = Instructions("acquire ./source -> ./destination;");
Require(generic.Any(instruction => instruction.Op == Msync.Core.OpCode.Barrier && instruction.A == 2)
    && generic.All(instruction => instruction.Op != Msync.Core.OpCode.Acquire), "after fence is not join-all");
var later = Instructions("release; ./tool();");
Require(later.Single(instruction => instruction.Op == Msync.Core.OpCode.ProcessCall).B == 0, "release is not a sticky process mode");
var acquireRegion = Instructions("acquire ./source -> ./destination;").ToArray();
int regionStart = Array.FindIndex(acquireRegion, instruction => instruction is { Op: Msync.Core.OpCode.Barrier, A: 2, B: 1 });
int transfer = Array.FindIndex(acquireRegion, instruction => instruction.Op == Msync.Core.OpCode.Transfer);
int regionEnd = Array.FindIndex(acquireRegion, instruction => instruction is { Op: Msync.Core.OpCode.Barrier, A: 2, B: 0 });
Require(regionStart >= 0 && regionStart < transfer && transfer < regionEnd, "acquire region brackets exactly its transfer");
var nestedRegions = Instructions("acquire { acquire ./first -> ./second; ./third -> ./fourth }")
    .Where(instruction => instruction is { Op: Msync.Core.OpCode.Barrier, A: 2 }).Select(instruction => instruction.B);
Require(nestedRegions.SequenceEqual([1, 1, 0, 0]), "nested acquire regions retain lexical ownership");
Require(Instructions("acquire;").All(instruction => instruction.Op != Msync.Core.OpCode.Barrier), "standalone acquire does not open a region");
Require(Instructions("release ./source -> ./destination;").All(instruction => instruction is not { Op: Msync.Core.OpCode.Barrier, A: 2 }), "release does not open an acquire region");
Console.WriteLine("Barrier lowering: 12 contracts passed.");
