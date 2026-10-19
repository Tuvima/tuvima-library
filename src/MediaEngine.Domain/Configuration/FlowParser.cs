using System.Text.Json;
using System.Text.RegularExpressions;

namespace MediaEngine.Domain.Configuration;

/// <summary>
/// Reads the JSON of a provider flow into a <see cref="FlowProgram"/>, reporting every unknown
/// property and bad shape with its path.
/// <para>
/// A step is a JSON object with one operation key: <c>set</c>, <c>slice</c>, <c>call</c>,
/// <c>pages</c>, <c>find</c>, <c>pick</c>, <c>stop_if</c>, <c>stop_if_all</c>, <c>skip_if</c>,
/// <c>skip_if_all</c>, <c>if</c>, <c>each</c>, <c>emit</c>, <c>claims</c> or <c>return</c>.
/// An expression is text (<c>$variable.path</c>, <c>@request.field</c>, <c>@hint.key</c>,
/// <c>@name</c>, <c>@limit</c>, or plain text), a list (the first that has a value), a number, or
/// an object with one operation key: <c>first_chars</c>, <c>format</c>, <c>url_on_hosts</c>,
/// <c>digits_of</c>, <c>first_non_null</c>, <c>clamp</c>, <c>int</c>, <c>object</c> or <c>literal</c>.
/// </para>
/// </summary>
public static class FlowParser
{
    private static readonly Regex FormatPlaceholder = new(@"\{([A-Za-z_][A-Za-z0-9_]*)(\.[^{}]*)?\}", RegexOptions.Compiled);

    public static readonly IReadOnlyList<string> Operations =
    [
        "set", "slice", "call", "pages", "find", "pick", "stop_if", "stop_if_all", "skip_if", "skip_if_all",
        "if", "each", "emit", "claims", "return",
    ];

    public static FlowProgram Parse(IReadOnlyList<JsonElement> steps, string path, List<string> errors)
    {
        var parser = new Parser(errors);
        parser.Program.Steps.AddRange(parser.ParseSteps(steps, path));
        return parser.Program;
    }

    private sealed class Parser(List<string> errors)
    {
        public FlowProgram Program { get; } = new();

        public List<FlowStep> ParseSteps(IEnumerable<JsonElement> elements, string path)
        {
            var steps = new List<FlowStep>();
            var index = 0;
            foreach (var element in elements)
            {
                if (ParseStep(element, $"{path}[{index}]") is { } step)
                {
                    steps.Add(step);
                }

                index++;
            }

            return steps;
        }

        private FlowStep? ParseStep(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path} must be an object.");
                return null;
            }

            var present = Operations.Where(operation => element.TryGetProperty(operation, out _)).ToList();
            if (present.Count != 1)
            {
                errors.Add($"{path} needs exactly one of {string.Join(", ", Operations)}.");
                return null;
            }

            var operation = present[0];
            var allowed = AllowedKeys(operation);
            foreach (var property in element.EnumerateObject().Where(property => !allowed.Contains(property.Name)))
            {
                errors.Add($"{path}.{property.Name} is not a known property of a {operation} step.");
            }

            return operation switch
            {
                "set" => new SetStep(path, Name(element, "set", path), Expression(element, "from", path)),
                "slice" => new SliceStep(path, Name(element, "slice", path), Expression(element, "from", path), Expression(element, "take", path)),
                "call" => ParseCall(element, path, "call", Name(element, "into", path)),
                "pages" => ParsePages(element, path),
                "find" => new FindStep(path, Name(element, "find", path), Expression(element, "in", path), ElementTests(element, "where", path)),
                "pick" => ParsePick(element, path),
                "stop_if" => new StopStep(path, Conditions(element, "stop_if", path), All: false),
                "stop_if_all" => new StopStep(path, Conditions(element, "stop_if_all", path), All: true),
                "skip_if" => new SkipStep(path, Conditions(element, "skip_if", path), All: false),
                "skip_if_all" => new SkipStep(path, Conditions(element, "skip_if_all", path), All: true),
                "if" => ParseIf(element, path),
                "each" => ParseEach(element, path),
                "emit" => ParseEmit(element, path),
                "claims" => ParseClaims(element, path),
                _ => new ReturnStep(path),
            };
        }

        private static HashSet<string> AllowedKeys(string operation) => operation switch
        {
            "set" => ["set", "from"],
            "slice" => ["slice", "from", "take"],
            "call" => ["call", "args", "into", "keep_on_null", "on_error"],
            "pages" => ["pages", "args", "into"],
            "find" => ["find", "in", "where"],
            "pick" => ["pick", "from", "take", "candidate_id", "tests", "fallback"],
            "if" => ["if", "then", "else"],
            "each" => ["each", "in", "do"],
            "emit" => ["emit", "stop_at"],
            _ => [operation],
        };

        private string Name(JsonElement element, string key, string path)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                && Regex.IsMatch(value.GetString()!, "^[a-z][a-z0-9_]*$"))
            {
                var name = value.GetString()!;
                Program.Assigned.Add(name);
                return name;
            }

            errors.Add($"{path}.{key} must be a lower-case variable name.");
            return string.Empty;
        }

        private CallStep ParseCall(JsonElement element, string path, string key, string into)
        {
            var endpoint = Text(element, key, path);
            var arguments = Arguments(element, path);
            Program.Calls.Add((endpoint, arguments.Keys.ToList(), path));
            var ignore = false;
            if (element.TryGetProperty("on_error", out var policy))
            {
                ignore = policy.ValueKind == JsonValueKind.String && policy.GetString() == "ignore";
                if (!ignore)
                {
                    errors.Add($"{path}.on_error must be \"ignore\".");
                }
            }

            return new CallStep(path, endpoint, arguments, into, Flag(element, "keep_on_null", path), ignore);
        }

        private PagesStep ParsePages(JsonElement element, string path)
        {
            var endpoint = Text(element, "pages", path);
            var arguments = Arguments(element, path);
            Program.Calls.Add((endpoint, arguments.Keys.ToList(), path));
            return new PagesStep(path, endpoint, arguments, Name(element, "into", path));
        }

        private PickStep ParsePick(JsonElement element, string path)
        {
            var tests = new List<PickTest>();
            var index = 0;
            if (element.TryGetProperty("tests", out var testsElement) && testsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var test in testsElement.EnumerateArray())
                {
                    tests.Add(ParsePickTest(test, $"{path}.tests[{index++}]"));
                }
            }
            else
            {
                errors.Add($"{path}.tests must be a list.");
            }

            Program.Assigned.Add("candidate");
            Program.Assigned.Add("candidate_id");
            var ids = new List<string>();
            if (element.TryGetProperty("candidate_id", out var idPaths) && idPaths.ValueKind == JsonValueKind.Array)
            {
                ids.AddRange(idPaths.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!));
            }

            var fallback = element.TryGetProperty("fallback", out var fallbackElement) ? fallbackElement.GetString() : null;
            if (fallback is not null && fallback != "only_candidate")
            {
                errors.Add($"{path}.fallback must be \"only_candidate\".");
            }

            return new PickStep(
                path, Name(element, "pick", path), Expression(element, "from", path), Integer(element, "take", path),
                ids, tests, fallback == "only_candidate");
        }

        private PickTest ParsePickTest(JsonElement test, string path)
        {
            if (test.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path} must be an object.");
                return new PickTest(path, string.Empty, new LiteralExpression(string.Empty), false, null);
            }

            foreach (var property in test.EnumerateObject().Where(property => property.Name is not ("path" or "equals" or "ignore_case" or "via")))
            {
                errors.Add($"{path}.{property.Name} is not a known property of a pick test.");
            }

            CallStep? via = null;
            if (test.TryGetProperty("via", out var viaElement) && viaElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in viaElement.EnumerateObject().Where(property => property.Name is not ("call" or "args")))
                {
                    errors.Add($"{path}.via.{property.Name} is not a known property.");
                }

                var endpoint = Text(viaElement, "call", $"{path}.via");
                var arguments = Arguments(viaElement, $"{path}.via");
                Program.Calls.Add((endpoint, arguments.Keys.ToList(), $"{path}.via"));
                via = new CallStep($"{path}.via", endpoint, arguments, string.Empty, false, false);
            }

            return new PickTest(path, Text(test, "path", path), Expression(test, "equals", path), Flag(test, "ignore_case", path), via);
        }

        private IfStep ParseIf(JsonElement element, string path)
        {
            var then = element.TryGetProperty("then", out var thenElement) && thenElement.ValueKind == JsonValueKind.Array
                ? ParseSteps(thenElement.EnumerateArray(), $"{path}.then")
                : ErrorList($"{path}.then must be a list of steps.");
            var otherwise = element.TryGetProperty("else", out var elseElement) && elseElement.ValueKind == JsonValueKind.Array
                ? ParseSteps(elseElement.EnumerateArray(), $"{path}.else")
                : [];
            return new IfStep(path, Conditions(element, "if", path), then, otherwise);
        }

        private EachStep ParseEach(JsonElement element, string path)
        {
            var variable = Name(element, "each", path);
            var source = Expression(element, "in", path);
            var body = element.TryGetProperty("do", out var doElement) && doElement.ValueKind == JsonValueKind.Array
                ? ParseSteps(doElement.EnumerateArray(), $"{path}.do")
                : ErrorList($"{path}.do must be a list of steps.");
            return new EachStep(path, variable, source, body);
        }

        private List<FlowStep> ErrorList(string message)
        {
            errors.Add(message);
            return [];
        }

        private EmitStep ParseEmit(JsonElement element, string path)
        {
            var fields = element.GetProperty("emit");
            if (fields.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}.emit must be an object.");
                return new EmitStep(path, new LiteralExpression(string.Empty), null, null, null, null, null, 0, null,
                    new Dictionary<string, FlowExpression>(), null);
            }

            var emitPath = $"{path}.emit";
            foreach (var property in fields.EnumerateObject().Where(property =>
                property.Name is not ("title" or "author" or "description" or "year" or "thumbnail" or "id"
                    or "confidence" or "type" or "extra")))
            {
                errors.Add($"{emitPath}.{property.Name} is not a known property.");
            }

            FlowExpression? Optional(string key) => fields.TryGetProperty(key, out _) ? Expression(fields, key, emitPath) : null;
            var confidence = fields.TryGetProperty("confidence", out var confidenceElement) && confidenceElement.ValueKind == JsonValueKind.Number
                ? confidenceElement.GetDouble()
                : double.NaN;
            if (double.IsNaN(confidence) || confidence is <= 0 or > 1)
            {
                errors.Add($"{emitPath}.confidence must be a number above 0 and at most 1.");
                confidence = 0;
            }

            var extra = new Dictionary<string, FlowExpression>(StringComparer.Ordinal);
            if (fields.TryGetProperty("extra", out var extraElement))
            {
                if (extraElement.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{emitPath}.extra must be an object.");
                }
                else
                {
                    foreach (var property in extraElement.EnumerateObject())
                    {
                        extra[property.Name] = ParseExpression(property.Value, $"{emitPath}.extra.{property.Name}");
                    }
                }
            }

            return new EmitStep(
                path, Expression(fields, "title", emitPath), Optional("author"), Optional("description"), Optional("year"),
                Optional("thumbnail"), Optional("id"), confidence,
                fields.TryGetProperty("type", out var type) ? type.GetString() : null, extra,
                element.TryGetProperty("stop_at", out _) ? Expression(element, "stop_at", path) : null);
        }

        private ClaimsStep ParseClaims(JsonElement element, string path)
        {
            var claims = element.GetProperty("claims");
            var root = new Dictionary<string, FlowExpression>(StringComparer.Ordinal);
            var rules = new List<ClaimRuleConfiguration>();
            var add = ClaimAddModes.IfMissingValue;
            if (claims.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path}.claims must be an object.");
                return new ClaimsStep(path, root, rules, add);
            }

            foreach (var property in claims.EnumerateObject().Where(property => property.Name is not ("root" or "rules" or "add")))
            {
                errors.Add($"{path}.claims.{property.Name} is not a known property.");
            }

            if (claims.TryGetProperty("root", out var rootElement) && rootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in rootElement.EnumerateObject())
                {
                    root[property.Name] = ParseExpression(property.Value, $"{path}.claims.root.{property.Name}");
                }
            }
            else
            {
                errors.Add($"{path}.claims.root must be an object.");
            }

            if (claims.TryGetProperty("rules", out var rulesElement) && rulesElement.ValueKind == JsonValueKind.Array)
            {
                try
                {
                    rules = JsonSerializer.Deserialize<List<ClaimRuleConfiguration>>(rulesElement.GetRawText()) ?? [];
                }
                catch (JsonException ex)
                {
                    errors.Add($"{path}.claims.rules is not valid ({ex.Message}).");
                }
            }
            else
            {
                errors.Add($"{path}.claims.rules must be a list.");
            }

            if (claims.TryGetProperty("add", out var addElement) && addElement.ValueKind == JsonValueKind.String)
            {
                add = addElement.GetString()!;
            }

            return new ClaimsStep(path, root, rules, add);
        }

        // -- shared readers ---------------------------------------------------------------------

        private string Text(JsonElement element, string key, string path)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString()))
            {
                return value.GetString()!;
            }

            errors.Add($"{path}.{key} must be text.");
            return string.Empty;
        }

        private int Integer(JsonElement element, string key, string path)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
            {
                return number;
            }

            errors.Add($"{path}.{key} must be a whole number.");
            return 0;
        }

        private bool Flag(JsonElement element, string key, string path)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                return false;
            }

            if (value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return value.GetBoolean();
            }

            errors.Add($"{path}.{key} must be true or false.");
            return false;
        }

        private Dictionary<string, FlowExpression> Arguments(JsonElement element, string path)
        {
            var arguments = new Dictionary<string, FlowExpression>(StringComparer.Ordinal);
            if (element.TryGetProperty("args", out var args))
            {
                if (args.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{path}.args must be an object.");
                }
                else
                {
                    foreach (var property in args.EnumerateObject())
                    {
                        arguments[property.Name] = ParseExpression(property.Value, $"{path}.args.{property.Name}");
                    }
                }
            }

            return arguments;
        }

        private List<FlowElementTest> ElementTests(JsonElement element, string key, string path)
        {
            var tests = new List<FlowElementTest>();
            if (!element.TryGetProperty(key, out var list) || list.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"{path}.{key} must be a list.");
                return tests;
            }

            var index = 0;
            foreach (var item in list.EnumerateArray())
            {
                var itemPath = $"{path}.{key}[{index++}]";
                if (item.ValueKind != JsonValueKind.Object)
                {
                    errors.Add($"{itemPath} must be an object.");
                    continue;
                }

                foreach (var property in item.EnumerateObject().Where(property => property.Name is not ("path" or "equals" or "number_equals" or "ignore_case")))
                {
                    errors.Add($"{itemPath}.{property.Name} is not a known property.");
                }

                var numeric = item.TryGetProperty("number_equals", out _);
                if (numeric == item.TryGetProperty("equals", out _))
                {
                    errors.Add($"{itemPath} needs exactly one of equals and number_equals.");
                    continue;
                }

                tests.Add(new FlowElementTest(
                    Text(item, "path", itemPath), Expression(item, numeric ? "number_equals" : "equals", itemPath),
                    Flag(item, "ignore_case", itemPath), numeric));
            }

            return tests;
        }

        private List<FlowCondition> Conditions(JsonElement element, string key, string path)
        {
            var list = new List<FlowCondition>();
            if (!element.TryGetProperty(key, out var conditions) || conditions.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"{path}.{key} must be a list of conditions.");
                return list;
            }

            var index = 0;
            foreach (var condition in conditions.EnumerateArray())
            {
                if (ParseCondition(condition, $"{path}.{key}[{index++}]") is { } parsed)
                {
                    list.Add(parsed);
                }
            }

            return list;
        }

        private FlowCondition? ParseCondition(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{path} must be an object.");
                return null;
            }

            var keys = element.EnumerateObject().Select(property => property.Name).Where(name => name != "ignore_case").ToList();
            if (keys.Count != 1)
            {
                errors.Add($"{path} needs exactly one of blank, present, not_null, equals, number_equals, any, all, not.");
                return null;
            }

            if (element.TryGetProperty("ignore_case", out _) && keys[0] != "equals")
            {
                errors.Add($"{path}.ignore_case is only valid with equals.");
            }

            switch (keys[0])
            {
                case "blank":
                    return new BlankCondition(Expression(element, "blank", path));
                case "present":
                    return new PresentCondition(Expression(element, "present", path));
                case "not_null":
                    return new NotNullCondition(Expression(element, "not_null", path));
                case "equals":
                case "number_equals":
                    var pair = element.GetProperty(keys[0]);
                    if (pair.ValueKind != JsonValueKind.Array || pair.GetArrayLength() != 2)
                    {
                        errors.Add($"{path}.{keys[0]} must be a list of two expressions.");
                        return null;
                    }

                    var left = ParseExpression(pair[0], $"{path}.{keys[0]}[0]");
                    var right = ParseExpression(pair[1], $"{path}.{keys[0]}[1]");
                    return keys[0] == "equals"
                        ? new EqualsCondition(left, right, Flag(element, "ignore_case", path))
                        : new NumberEqualsCondition(left, right);
                case "any":
                    return new AnyCondition(Conditions(element, "any", path));
                case "all":
                    return new AllCondition(Conditions(element, "all", path));
                case "not":
                    return ParseCondition(element.GetProperty("not"), $"{path}.not") is { } inner ? new NotCondition(inner) : null;
                default:
                    errors.Add($"{path}.{keys[0]} is not a known condition.");
                    return null;
            }
        }

        private FlowExpression Expression(JsonElement element, string key, string path)
        {
            if (!element.TryGetProperty(key, out var value))
            {
                errors.Add($"{path}.{key} is required.");
                return new LiteralExpression(string.Empty);
            }

            return ParseExpression(value, $"{path}.{key}");
        }

        private FlowExpression ParseExpression(JsonElement element, string path)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Null:
                    return new CoalesceExpression([]);
                case JsonValueKind.Number:
                    return new NumberExpression(element.GetDouble());
                case JsonValueKind.String:
                    return ParseText(element.GetString()!, path);
                case JsonValueKind.Array:
                    var index = 0;
                    return new CoalesceExpression(element.EnumerateArray().Select(item => ParseExpression(item, $"{path}[{index++}]")).ToList());
                case JsonValueKind.Object:
                    return ParseOperation(element, path);
                default:
                    errors.Add($"{path} is not an expression.");
                    return new LiteralExpression(string.Empty);
            }
        }

        private FlowExpression ParseText(string text, string path)
        {
            if (text.StartsWith('$'))
            {
                var dot = text.IndexOf('.', StringComparison.Ordinal);
                var name = dot < 0 ? text[1..] : text[1..dot];
                if (!Regex.IsMatch(name, "^[a-z][a-z0-9_]*$"))
                {
                    errors.Add($"{path} '{text}' does not name a variable.");
                    return new LiteralExpression(string.Empty);
                }

                Program.Reads.Add((name, path));
                return new VariableExpression(name, dot < 0 ? null : text[(dot + 1)..]);
            }

            if (text.StartsWith('@'))
            {
                var parts = text[1..].Split('.', 2);
                switch (parts[0])
                {
                    case "request" when parts.Length == 2:
                        return new RequestExpression(parts[1]);
                    case "hint" when parts.Length == 2:
                        return new HintExpression(parts[1]);
                    case "name" or "limit" when parts.Length == 1:
                        return new BuiltInExpression(parts[0]);
                    default:
                        errors.Add($"{path} '{text}' must be @request.field, @hint.key, @name or @limit.");
                        return new LiteralExpression(string.Empty);
                }
            }

            return new LiteralExpression(text);
        }

        private FlowExpression ParseOperation(JsonElement element, string path)
        {
            var keys = element.EnumerateObject().Select(property => property.Name).ToList();
            var operation = keys.FirstOrDefault(key => key is "first_chars" or "format" or "url_on_hosts" or "digits_of" or "first_non_null" or "clamp" or "int" or "object" or "literal");
            if (operation is null)
            {
                errors.Add($"{path} is not an expression; use one of first_chars, format, url_on_hosts, digits_of, first_non_null, clamp, int, object, literal.");
                return new LiteralExpression(string.Empty);
            }

            foreach (var key in keys.Where(key => key != operation && !(operation == "url_on_hosts" && key == "hosts")))
            {
                errors.Add($"{path}.{key} is not a known property of {operation}.");
            }

            var value = element.GetProperty(operation);
            switch (operation)
            {
                case "literal":
                    return new LiteralExpression(value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty);
                case "format":
                    var template = value.ValueKind == JsonValueKind.String ? value.GetString()! : string.Empty;
                    foreach (Match match in FormatPlaceholder.Matches(template))
                    {
                        Program.Reads.Add((match.Groups[1].Value, path));
                    }

                    return new FormatExpression(template);
                case "int":
                    return new IntExpression(ParseExpression(value, $"{path}.int"));
                case "object":
                    if (value.ValueKind != JsonValueKind.Object)
                    {
                        errors.Add($"{path}.object must be an object.");
                        return new ObjectExpression(new Dictionary<string, FlowExpression>());
                    }

                    return new ObjectExpression(value.EnumerateObject()
                        .ToDictionary(property => property.Name, property => ParseExpression(property.Value, $"{path}.object.{property.Name}"), StringComparer.Ordinal));
                case "digits_of":
                    return new DigitsOfExpression(List(value, $"{path}.digits_of"));
                case "first_non_null":
                    return new FirstNonNullExpression(List(value, $"{path}.first_non_null"));
                case "url_on_hosts":
                    var hosts = element.TryGetProperty("hosts", out var hostsElement) && hostsElement.ValueKind == JsonValueKind.Array
                        ? hostsElement.EnumerateArray().Where(host => host.ValueKind == JsonValueKind.String).Select(host => host.GetString()!).ToList()
                        : [];
                    if (hosts.Count == 0)
                    {
                        errors.Add($"{path}.hosts must list at least one host.");
                    }

                    return new UrlOnHostsExpression(List(value, $"{path}.url_on_hosts"), hosts);
                case "first_chars":
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 2 || value[1].ValueKind != JsonValueKind.Number)
                    {
                        errors.Add($"{path}.first_chars must be [expression, count].");
                        return new LiteralExpression(string.Empty);
                    }

                    return new FirstCharsExpression(ParseExpression(value[0], $"{path}.first_chars[0]"), value[1].GetInt32());
                default:
                    if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3
                        || value[1].ValueKind != JsonValueKind.Number || value[2].ValueKind != JsonValueKind.Number)
                    {
                        errors.Add($"{path}.clamp must be [expression, min, max].");
                        return new LiteralExpression(string.Empty);
                    }

                    return new ClampExpression(ParseExpression(value[0], $"{path}.clamp[0]"), value[1].GetDouble(), value[2].GetDouble());
            }
        }

        private List<FlowExpression> List(JsonElement element, string path)
        {
            if (element.ValueKind != JsonValueKind.Array)
            {
                errors.Add($"{path} must be a list of expressions.");
                return [];
            }

            var index = 0;
            return element.EnumerateArray().Select(item => ParseExpression(item, $"{path}[{index++}]")).ToList();
        }
    }
}
