using Msync.Core;

namespace Msync.Compiler;

internal sealed partial class Compilation
{
    private Dictionary<Value, Info> acceptedValues = new(ReferenceEqualityComparer.Instance);

    private void RefreshSessionValues(Func<int, Value?> resolve, IReadOnlyDictionary<string, Binding>? submittedEnvironment = null)
    {
        SessionValues snapshot = new(this, acceptedValues, submittedValues);
        foreach (var (name, binding) in sessionEnvironment)
            if (resolve(binding.Slot) is { } value)
            {
                Info hint = submittedEnvironment?.TryGetValue(name, out var submitted) == true && ReferenceEquals(binding.Identity, submitted.Identity)
                    ? submitted.Info : binding.Info;
                sessionEnvironment = sessionEnvironment.SetItem(name, binding with { Info = snapshot.Read(hint, value) });
            }
        acceptedValues = snapshot.Values;
    }

    private sealed class SessionValues(Compilation compilation, IReadOnlyDictionary<Value, Info> previous, IEnumerable<Info> candidates)
    {
        public Dictionary<Value, Info> Values { get; } = new(ReferenceEqualityComparer.Instance);
        private readonly List<Info> shapes = Shapes(candidates);

        public Info Read(Info hint, Value value)
        {
            if (value is CellValue cell) value = cell.Data;
            if (Values.TryGetValue(value, out var known)) return known;
            if (previous.TryGetValue(value, out var accepted)) hint = accepted;
            Info result = value switch
            {
                ScopeValue scope => Scope(hint, scope),
                FunctionValue function when function.NativeName is null => Function(hint, function),
                FunctionValue function => new(TypeRef.Function, NativeName: function.NativeName),
                BoolValue boolean => new(TypeRef.Bool, KnownBool: boolean.Data),
                ConnectionValue connection => new(TypeRef.Connection, KnownConnection: connection),
                PathValue path => new(TypeRef.Path, KnownPath: path),
                StringValue text => new(TypeRef.String, KnownText: text.Data),
                TypeValue type => new(TypeRef.Type, TypeValue: type.Data),
                RangeValue => hint with { KnownBool = null, KnownConnection = null, KnownPath = null, KnownText = null },
                _ => new(value.Type)
            };
            return result == hint ? hint : result;
        }

        private Info Scope(Info hint, ScopeValue value)
        {
            ScopeInfo shape = Shape(hint, value);
            Dictionary<string, Info> members = new(StringComparer.Ordinal);
            Dictionary<string, Binding> bindings = new(StringComparer.Ordinal);
            ScopeInfo scope = shape with { Members = members, FieldBindings = bindings };
            Info result = new(TypeRef.Scope, Scope: scope);
            Values.Add(value, result);
            foreach (var (name, member) in value.Members)
            {
                Info info = Read(shape.Members.GetValueOrDefault(name) ?? new(member.Type), member);
                members[name] = info;
                if (shape.FieldBindings.TryGetValue(name, out var binding)) bindings[name] = binding with { Info = info };
            }
            if (ReferenceEquals(hint.Scope, shape) && members.All(pair => ReferenceEquals(pair.Value, shape.Members[pair.Key])))
            {
                Values[value] = hint;
                return hint;
            }
            return result;
        }

        private ScopeInfo Shape(Info hint, ScopeValue value)
        {
            bool Matches(ScopeInfo shape) => shape.Members.Count == value.Members.Count
                && shape.Members.Keys.All(value.Members.ContainsKey);
            if (value.AllocationSite != 0)
            {
                if (hint.Scope is { } exact && exact.AllocationSite == value.AllocationSite) return exact;
                return shapes.First(info => info.Scope?.AllocationSite == value.AllocationSite).Scope!;
            }
            if (hint.Scope is { } preferred && Matches(preferred)) return preferred;
            foreach (var candidate in shapes)
                if (candidate.Scope is { } shape && Matches(shape)) return shape;
            throw new InvalidOperationException("An executed Scope has no compiler storage shape.");
        }

        private Info Function(Info hint, FunctionValue value)
        {
            Template template = SelectFunction(hint, value.Function);
            if (template.Captures.Count == 0)
            {
                Info plain = ReferenceEquals(hint.Function, template) ? hint : new(TypeRef.Function, Function: template);
                Values.Add(value, plain);
                return plain;
            }
            List<KeyValuePair<string, Binding>> captures = [];
            Template current = new(template.Id, template.Syntax, captures, template.Source, template.SourceName)
            { Instance = true, Concepts = template.Concepts, StandardShadowed = template.StandardShadowed };
            Info result = new(TypeRef.Function, Function: current);
            Values.Add(value, result);
            for (int index = 0; index < template.Captures.Count; index++)
            {
                var capture = template.Captures[index];
                Info info = Read(capture.Value.Info, value.Captures![index]);
                captures.Add(new(capture.Key, capture.Value with { Info = info }));
            }
            if (ReferenceEquals(hint.Function, template) && captures.Select((pair, index) => ReferenceEquals(pair.Value.Info, template.Captures[index].Value.Info)).All(item => item))
            {
                Values[value] = hint;
                return hint;
            }
            compilation.templates.Add(current);
            return result;
        }

        private Template SelectFunction(Info hint, int identity)
        {
            bool Matches(Template template) => template.Id == identity || template.Specializations.Values.Any(item => item.Id == identity);
            foreach (var choice in CallableChoices(hint))
                if (choice.Function is { } function && Matches(function)) return function;
            foreach (var template in compilation.templates)
                if (template.Specializations.Values.Any(item => item.Id == identity)) return template;
            return compilation.templates.First(template => template.Id == identity);
        }

        private static List<Info> Shapes(IEnumerable<Info> candidates)
        {
            List<Info> result = [];
            HashSet<Info> seen = new(ReferenceEqualityComparer.Instance);
            void Add(Info info)
            {
                if (!seen.Add(info)) return;
                result.Add(info);
                if (info.Scope is { } scope) foreach (var member in scope.Members.Values) Add(member);
                if (info.Alternatives is { } alternatives) foreach (var alternative in alternatives) Add(alternative);
            }
            foreach (var candidate in candidates) Add(candidate);
            return result;
        }
    }
}
