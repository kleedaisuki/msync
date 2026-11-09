using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;
using System.Globalization;
using System.Numerics;
static Expr E(string source) => ((ExpressionStatement)Parser.Parse(source).Statements[0]).Expression;
static void Check(bool value, string name) { if (!value) throw new Exception(name); }
foreach (var literal in new[] { "1.5", ".5", "1.", "1e-3", "1E+3", "0xFF", "0b1010", "0o77", "1_234.5_6e2", "9223372036854775808" }) Check(E(literal) is NumberExpr, literal);
Check(E("1+2*3") is BinaryExpr { Operator:"+", Right: BinaryExpr { Operator:"*" } }, "compact arithmetic");
Check(E("a*b/c") is BinaryExpr { Operator:"/", Left: BinaryExpr {Operator:"*"} }, "division precedence");
Check(E("1-2") is BinaryExpr {Operator:"-"}, "compact subtract");
Check(E("-2**2") is UnaryExpr {Operator:"-",Operand:BinaryExpr {Operator:"**"}}, "power binds before unary minus");
Check(E("2**-2") is BinaryExpr {Operator:"**",Right:UnaryExpr {Operator:"-"}}, "negative power");
Check(E("2**3**4") is BinaryExpr {Operator:"**",Right:BinaryExpr {Operator:"**"}}, "right associative power");
Check(E("2**operation()") is BinaryExpr {Operator:"**",Right:CallExpr}, "call in exponent");
Check(E("-0") is UnaryExpr {Operator:"-",Operand:NumberExpr}, "signed zero");
Check(E("-value") is UnaryExpr {Operator:"-",Operand:NameExpr {Name:"value"}}, "negative variable");
Check(E("7%4") is BinaryExpr {Operator:"%"}, "remainder");
Check(E("copy-config") is NameExpr {Name:"copy-config"}, "hyphen identifier");
Check(E("123-config") is NameExpr {Name:"123-config"}, "numeric prefix identifier");
Check(E(".codex") is NameExpr {Name:".codex"}, "dot identifier");
Check(Parser.NumberSpans("~/123/4 1/2").Count == 2, "numeric path segment lexer roles");
Check(E("~/.codex?") is UnaryExpr {Operator:"?",Operand:BinaryExpr {Operator:"/"}}, "path nullable precedence");
Check(E("exists ~/.codex") is UnaryExpr {Operator:"exists",Operand:BinaryExpr {Operator:"/"}}, "exists path");
Check(E("file @ remote -> ./") is BinaryExpr {Operator:"->",Left:BinaryExpr {Operator:"@"}}, "endpoint qualification");
Check(E("file @ connection()") is BinaryExpr {Operator:"@",Right:CallExpr}, "endpoint expression call");
Check(E("ssh://user@host") is NameExpr {Name:"ssh://user@host"}, "SSH authority user");
Check(E("1 as String") is CastExpr {Type.Name:"String",Value:NumberExpr}, "cast");
var namedScope = (ScopeExpr)((LetStatement)Parser.Parse("let object = scope Named { value: 1 };").Statements.Single()).Value;
Check(namedScope.Name == "Named" && namedScope.NameSpan == new SourceSpan(19, 5), "named scope expression retains identity and source span");
Check(E("scope { value: 1 }") is ScopeExpr {Name:null}, "anonymous scope expression has no identity name");
Check(NameSyntax.SpanAt("-value", 6) == new SourceSpan(1, 5), "unary minus is outside completion replacement");
Check(NameSyntax.SpanAt("copy-config", 11) == new SourceSpan(0, 11), "hyphenated lexical binding stays whole");
Check(NameSyntax.SpanAt("./-filename", 11) == new SourceSpan(2, 9), "path filename preserves leading minus");
var highlights = Parser.HighlightSpans("-value ./-filename copy-config 2**3");
Check(highlights.Numbers.Count == 2 && highlights.ArithmeticOperators.Count == 2, "lexical highlighter separates unary and path/name hyphens");
for (int bits = 64; bits <= 512; bits += 7)
{
    BigInteger integer = (BigInteger.One << bits) + (BigInteger.One << (bits - 53)) + 1;
    string literal = "0x" + integer.ToString("x", CultureInfo.InvariantCulture);
    Check(NumberSyntax.TryParse(literal, out double parsed) && parsed == (double)integer, "large hexadecimal integer has one binary64 conversion");
}
var concept = (ConceptDeclaration)Parser.Parse("concept C { Base, type Self; identity: Self, combine: fn(left: Self, right: Self) -> Self, };").Statements.Single();
Check(concept.Types.Count==1 && concept.Members.Count==2 && concept.Bases.Count==1,"concept requirements");
Console.WriteLine("Grammar upgrade: focused literal, operator, path, cast and concept contracts passed.");
