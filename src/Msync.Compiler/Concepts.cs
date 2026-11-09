using System.Collections.Immutable;
using Msync.Compiler.Syntax;
using Msync.Core;
using SourceSpan = Msync.Compiler.Syntax.SourceSpan;

namespace Msync.Compiler;

internal sealed record ConceptDefinition(ConceptDeclaration Syntax, string Source, string SourceName,
    ImmutableDictionary<string, ConceptDefinition> Environment);
internal sealed record ConceptContract(ConceptDefinition Definition, TypeSyntax Annotation, ImmutableDictionary<string, ConceptDefinition> Environment);
internal sealed record ConceptMemberContract(ConceptDefinition Definition, ConceptMember Requirement, IReadOnlyDictionary<string, Info> Associated);

internal sealed partial class Compilation
{
    private readonly HashSet<(ConceptDefinition Concept, object Shape)> checkingConcepts = [];
    private bool attachConceptContracts = true;
    private readonly Dictionary<StorageConstraint, (ImmutableDictionary<string, ConceptContract> Concepts,
        ImmutableDictionary<string, ConceptMemberContract> Members)> changedConceptStorage = [];

    private void ClearConceptStorage() => changedConceptStorage.Clear();
    private void RestoreConceptStorage()
    {
        foreach (var (storage, saved) in changedConceptStorage)
        {
            storage.ConceptContracts = saved.Concepts;
            storage.ConceptMemberContracts = saved.Members;
        }
        changedConceptStorage.Clear();
    }
    private void RememberConceptStorage(StorageConstraint storage) => changedConceptStorage.TryAdd(storage,
        (storage.ConceptContracts, storage.ConceptMemberContracts));

    private Info DeclareConcept(Frame frame, ConceptDeclaration declaration)
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (var associated in declaration.Types)
            if (!names.Add(associated.Name)) throw ConceptError(frame, $"Associated type '{associated.Name}' is declared twice.", associated.Span);
        names.Clear();
        foreach (var member in declaration.Members)
        {
            if (!names.Add(member.Name)) throw ConceptError(frame, $"Required member '{member.Name}' is declared twice.", member.Span);
            if (member.Parameters is { } parameters)
                foreach (var parameter in parameters)
                    if (parameter.Type is null) throw ConceptError(frame,
                        $"Required operation parameter '{parameter.Name}' needs an explicit type or associated type.", parameter.Span);
        }
        foreach (var parent in declaration.Bases)
            if (!frame.Concepts.ContainsKey(parent.Name))
                throw ConceptError(frame, $"Unknown base concept '{parent.Name}'.", parent.Span);
        ConceptDefinition definition = new(declaration, frame.Source, frame.SourceName, frame.Concepts);
        frame.Concepts = frame.Concepts.SetItem(declaration.Name, definition);
        frame.Emit(OpCode.Null);
        return new(TypeRef.Null);
    }

    private TypeRef ConceptType(Frame frame, TypeSyntax syntax)
    {
        TypeRef type = TypeRef.Scope;
        foreach (var alternative in syntax.Alternatives) type = TypeRef.Union(type, ResolveType(frame, alternative));
        return syntax.Nullable ? type.WithNull : type;
    }

    private void CheckConcept(Frame frame, Info value, TypeSyntax annotation, SourceSpan span)
    {
        if (value.EditorUnknown || value.Type.Kind == TypeKind.Never) return;
        if (annotation.Alternatives.Count != 0)
        {
            var alternatives = new[] { annotation with { Alternatives = [] } }.Concat(annotation.Alternatives).ToArray();
            TypeRef ordinary = new(TypeKind.Never);
            foreach (var alternative in alternatives)
                if (!frame.Concepts.ContainsKey(alternative.Name)) ordinary = TypeRef.Union(ordinary, ResolveType(frame, alternative));
            TypeKind required = value.Type.Kind & ~ordinary.Kind;
            if (required == TypeKind.Never) return;
            CompileException? mismatch = null;
            foreach (var alternative in alternatives)
            {
                if (!frame.Concepts.ContainsKey(alternative.Name)) continue;
                bool attach = attachConceptContracts;
                try
                {
                    attachConceptContracts = false;
                    CheckConcept(frame, value with { Type = new(required, value.Type.Element) }, alternative, span);
                }
                catch (CompileException error) { mismatch = error; continue; }
                finally { attachConceptContracts = attach; }
                CheckConcept(frame, value with { Type = new(required, value.Type.Element) }, alternative, span);
                return;
            }
            if (mismatch is not null) throw mismatch;
            return;
        }
        if (frame.Concepts.TryGetValue(annotation.Name, out var definition))
            CheckConceptDefinition(frame, Current(frame, value), definition, annotation.Nullable, span);
        else if (annotation.Name == "Range" && annotation.Arguments.Count == 1 && value.Element is { } element)
            CheckConcept(frame, element, annotation.Arguments[0], span);
    }

    private void AttachConcept(Frame frame, StorageConstraint storage, TypeSyntax annotation)
    {
        var definition = ReferencedConcept(frame, annotation);
        if (definition is null) return;
        RememberConceptStorage(storage);
        storage.ConceptContracts = storage.ConceptContracts.SetItem(frame.SourceName + ":" + annotation.Span.Start,
            new(definition, annotation, frame.Concepts));
    }

    private static ConceptDefinition? ReferencedConcept(Frame frame, TypeSyntax annotation)
    {
        if (frame.Concepts.TryGetValue(annotation.Name, out var definition)) return definition;
        foreach (var nested in annotation.Arguments.Concat(annotation.Alternatives))
            if (ReferencedConcept(frame, nested) is { } found) return found;
        return null;
    }

    private void CheckConceptStore(Frame frame, StorageConstraint storage, Info value, SourceSpan span)
    {
        foreach (var contract in storage.ConceptContracts.Values)
        {
            Frame lexical = CloneFrame(frame); lexical.Concepts = contract.Environment;
            CheckConcept(lexical, Current(frame, value), contract.Annotation, span);
        }
        foreach (var contract in storage.ConceptMemberContracts.Values)
            CheckConceptMember(frame, value, contract.Definition, contract.Requirement, contract.Associated, span);
    }

    private void CheckConceptDefinition(Frame frame, Info value, ConceptDefinition definition, bool nullable, SourceSpan span, Dictionary<string, Info>? supplied = null)
    {
        if (value.Type == TypeRef.Null && nullable) return;
        if (value.Type.Nullable && !nullable)
            throw RequirementError(frame, definition, span, "a non-null Scope", value.Type.ToString());
        if (value.Scope is not { } scope)
            throw RequirementError(frame, definition, span, "a structural Scope", value.Type.ToString());
        if (!checkingConcepts.Add((definition, scope.Identity))) return;
        try
        {
            Dictionary<string, Info> associated = supplied is null ? new(StringComparer.Ordinal) : new(supplied, StringComparer.Ordinal);
            foreach (var type in AssociatedTypes(definition))
                if (scope.Members.TryGetValue(type.Name, out var member) && member.TypeValue is { } denoted)
                    associated[type.Name] = new(denoted);
            foreach (var (owner, requirement) in Requirements(definition))
            {
                var actual = RequiredMember(frame, value, owner, requirement, span);
                if (requirement.Type is { } type)
                    InferAssociated(owner, requirement.Nullable ? type with { Nullable = true } : type, actual, associated);
                else if (actual.Function is { } function && requirement.Parameters is { } parameters)
                    for (int index = 0; index < Math.Min(parameters.Count, function.Syntax.Parameters.Count); index++)
                        if (parameters[index].Type is { } expected && function.Syntax.Parameters[index].Type is { } declared
                            && LanguageTypes.TryResolve(declared.Name, out var declaredType))
                            InferAssociated(owner, expected, new(declared.Nullable ? declaredType.WithNull : declaredType), associated);
            }
            foreach (var parent in definition.Syntax.Bases)
                CheckConceptDefinition(frame, value, definition.Environment[parent.Name], nullable, span, associated);
            foreach (var requirement in definition.Syntax.Members)
            {
                var actual = RequiredMember(frame, value, definition, requirement, span);
                CheckConceptMember(frame, actual, definition, requirement, associated, span);
                if (attachConceptContracts && scope.Storage.TryGetValue(requirement.Name, out var storage))
                {
                    RememberConceptStorage(storage);
                    storage.ConceptMemberContracts = storage.ConceptMemberContracts.SetItem(
                        definition.SourceName + ":" + definition.Syntax.Span.Start + ":" + requirement.Name,
                        new(definition, requirement, associated.ToImmutableDictionary()));
                }
            }
        }
        finally { checkingConcepts.Remove((definition, scope.Identity)); }
    }

    private void CheckConceptMember(Frame frame, Info actual, ConceptDefinition definition, ConceptMember requirement,
        IReadOnlyDictionary<string, Info> associated, SourceSpan span)
    {
        if (requirement.Type is { } type)
        {
            CheckRequirementType(frame, definition, actual, requirement.Nullable ? type with { Nullable = true } : type,
                associated, span, requirement.Name);
            return;
        }
        if (requirement.Nullable && actual.Type == TypeRef.Null) return;
        if (actual.Type.WithoutNull != TypeRef.Function || actual.Type.Nullable && !requirement.Nullable)
            throw RequirementError(frame, definition, span, $"function member '{requirement.Name}'", actual.Type.ToString());
        var arguments = requirement.Parameters!.Select(parameter =>
        {
            var argument = parameter.Type is null ? new Info(TypeRef.Any, EditorUnknown: true)
                : RequirementInfo(frame, definition, parameter.Type, associated, span);
            return parameter.Nullable ? argument with { Type = argument.Type.WithNull } : argument;
        }).ToArray();
        Frame probe = CloneFrame(frame);
        Info result;
        try { result = Invoke(probe, actual with { Storage = null, Type = actual.Type.WithoutNull }, arguments, span); }
        catch (CompileException error)
        {
            var failure = RequirementError(frame, definition, span,
                $"'{requirement.Name}' accepting ({string.Join(", ", arguments.Select(argument => argument.Type))})", error.Diagnostic.Message);
            failure.Diagnostic.Related.Add(new(definition.SourceName, definition.Source, requirement.Span, "required operation declared here"));
            throw failure;
        }
        CheckRequirementType(frame, definition, result, requirement.ReturnType!, associated, span, requirement.Name + " return");
    }

    private static IEnumerable<ConceptAssociatedType> AssociatedTypes(ConceptDefinition definition) =>
        definition.Syntax.Types.Concat(definition.Syntax.Bases.SelectMany(parent => AssociatedTypes(definition.Environment[parent.Name])));

    private static IEnumerable<(ConceptDefinition Owner, ConceptMember Member)> Requirements(ConceptDefinition definition) =>
        definition.Syntax.Members.Select(member => (definition, member)).Concat(
            definition.Syntax.Bases.SelectMany(parent => Requirements(definition.Environment[parent.Name])));

    private static void InferAssociated(ConceptDefinition definition, TypeSyntax type, Info actual, Dictionary<string, Info> associated)
    {
        if (type.Nullable && actual.Type.WithoutNull.Kind == TypeKind.Never) return;
        bool declared = AssociatedTypes(definition).Any(item => item.Name == type.Name);
        bool refineShape = associated.TryGetValue(type.Name, out var existing) && existing.Type.WithoutNull == actual.Type.WithoutNull
            && existing.Scope is null && actual.Scope is not null;
        if (declared && (!associated.ContainsKey(type.Name) || refineShape))
            associated[type.Name] = actual with { Type = type.Nullable ? actual.Type.WithoutNull : actual.Type,
                KnownBool = null, KnownText = null, KnownPath = null, KnownConnection = null, Storage = null };
        if (type.Name == "Range" && type.Arguments.Count == 1 && actual.Element is { } element)
            InferAssociated(definition, type.Arguments[0], element, associated);
    }

    private Info RequiredMember(Frame frame, Info value, ConceptDefinition definition, ConceptMember requirement, SourceSpan span)
    {
        var scope = value.Scope!;
        if (!scope.Members.TryGetValue(requirement.Name, out var member))
            throw RequirementError(frame, definition, span, $"member '{requirement.Name}'", "missing member");
        if (scope.Storage.TryGetValue(requirement.Name, out var storage) && storage.Type is { } storageType)
            member = member with { Type = storageType };
        return member;
    }

    private Info RequirementInfo(Frame frame, ConceptDefinition definition, TypeSyntax syntax, IReadOnlyDictionary<string, Info> associated, SourceSpan span)
    {
        Info info;
        if (syntax.Name == "Range" && syntax.Arguments.Count == 1)
        {
            var element = RequirementInfo(frame, definition, syntax.Arguments[0], associated, span);
            info = new(new(TypeKind.Range, element.Type), Element: element);
        }
        else if (associated.TryGetValue(syntax.Name, out var inferred)) info = inferred;
        else if (AssociatedTypes(definition).Any(item => item.Name == syntax.Name))
            throw RequirementError(frame, definition, span, $"associated type '{syntax.Name}' inferable from a field, operation annotation, or Type member", "unresolved associated type");
        else
        {
            Frame lexical = CloneFrame(frame); lexical.Concepts = definition.Environment;
            info = definition.Environment.ContainsKey(syntax.Name) ? ConceptInfo(lexical, syntax) : new(ResolveType(lexical, syntax with { Alternatives = [] }));
        }
        foreach (var alternative in syntax.Alternatives)
            info = Join(info, RequirementInfo(frame, definition, alternative, associated, span));
        return syntax.Nullable ? info with { Type = info.Type.WithNull } : info;
    }

    private void CheckRequirementType(Frame frame, ConceptDefinition definition, Info actual, TypeSyntax syntax,
        IReadOnlyDictionary<string, Info> associated, SourceSpan span, string member)
    {
        Frame lexical = CloneFrame(frame); lexical.Concepts = definition.Environment;
        if (definition.Environment.ContainsKey(syntax.Name))
        {
            CheckConcept(lexical, actual, syntax, span);
            return;
        }
        var expected = RequirementInfo(frame, definition, syntax, associated, span);
        if (!expected.Type.Accepts(actual.Type))
            throw RequirementError(frame, definition, span, $"'{member}: {expected.Type}'", actual.Type.ToString());
        CheckAssociatedShape(frame, definition, expected, actual, span, member, []);
    }

    private static void CheckAssociatedShape(Frame frame, ConceptDefinition definition, Info expected, Info actual,
        SourceSpan span, string member, HashSet<(object Expected, object Actual)> visited)
    {
        if (!expected.Type.Accepts(actual.Type))
            throw RequirementError(frame, definition, span, $"'{member}: {expected.Type}'", actual.Type.ToString());
        if (expected.Element is { } expectedElement && actual.Element is { } actualElement)
            CheckAssociatedShape(frame, definition, expectedElement, actualElement, span, member + " element", visited);
        if (expected.Scope is not { } shape) return;
        if (actual.Scope is not { } actualScope)
            throw RequirementError(frame, definition, span, $"'{member}' retaining its associated Scope fields", "unknown Scope shape");
        if (!visited.Add((shape.Identity, actualScope.Identity))) return;
        foreach (var (name, field) in shape.Members)
        {
            if (!actualScope.Members.TryGetValue(name, out var found))
                throw RequirementError(frame, definition, span, $"'{member}' retaining associated field '{name}: {field.Type}'", "missing field");
            var expectedType = shape.Storage.GetValueOrDefault(name)?.Type ?? field.Type;
            var actualType = actualScope.Storage.GetValueOrDefault(name)?.Type ?? found.Type;
            CheckAssociatedShape(frame, definition, field with { Type = expectedType }, found with { Type = actualType },
                span, member + "::" + name, visited);
        }
    }

    private Info ConceptInfo(Frame frame, TypeSyntax annotation)
    {
        if (!frame.Concepts.TryGetValue(annotation.Name, out var definition)) return new(ResolveType(frame, annotation));
        Dictionary<string, Info> members = new(StringComparer.Ordinal);
        Frame lexical = CloneFrame(frame); lexical.Concepts = definition.Environment;
        foreach (var parent in definition.Syntax.Bases)
            foreach (var member in ConceptInfo(lexical, parent).Scope!.Members) members[member.Key] = member.Value;
        foreach (var requirement in definition.Syntax.Members)
        {
            var type = requirement.Type;
            Info info = requirement.Parameters is not null ? new(TypeRef.Function) : type is not null && LanguageTypes.TryResolve(type.Name, out var known)
                ? new(type.Nullable ? known.WithNull : known) : type is not null && definition.Environment.ContainsKey(type.Name)
                    ? ConceptInfo(lexical, type) : new(TypeRef.Any, EditorUnknown: true);
            if (requirement.Nullable) info = info with { Type = info.Type.WithNull };
            DescribeConceptRequirement(info, definition, requirement);
            members[requirement.Name] = info;
        }
        return new(annotation.Nullable ? TypeRef.Scope.WithNull : TypeRef.Scope, Scope: new(members));
    }

    private static CompileException ConceptError(Frame frame, string message, SourceSpan span) => Error(frame, message, span,
        label: "invalid concept declaration", help: "Declare a named structural concept before using it.", code: "E0214");

    private static CompileException RequirementError(Frame frame, ConceptDefinition definition, SourceSpan span, string expected, string actual)
    {
        var error = Error(frame, $"Value does not satisfy concept '{definition.Syntax.Name}': expected {expected}, found {actual}.", span,
            label: $"does not satisfy {definition.Syntax.Name}", help: "Provide the required members and operation signatures; concepts do not add runtime types.", code: "E0214");
        error.Diagnostic.Related.Add(new(definition.SourceName, definition.Source, definition.Syntax.NameSpan ?? definition.Syntax.Span,
            $"concept '{definition.Syntax.Name}' declared here"));
        return error;
    }
}
