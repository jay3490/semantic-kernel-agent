using System.Globalization;
using System.Text;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Agent.Core.Linting.Helm;

/// <summary>A rendered template, with the template line every rendered line came from.</summary>
public sealed record RenderedTemplate(string Text, IReadOnlyList<int> SourceLines)
{
    /// <summary>Maps a 1-based line of <see cref="Text"/> back to the 1-based template line.</summary>
    public int SourceLineOf(int renderedLine) =>
        renderedLine >= 1 && renderedLine <= SourceLines.Count ? SourceLines[renderedLine - 1] : renderedLine;
}

/// <summary>Raised when a template cannot be parsed, for example an <c>{{ end }}</c> without a block.</summary>
public sealed class HelmTemplateException(string message, int line) : Exception(message)
{
    public int Line { get; } = line;
}

/// <summary>
/// Renders a Helm template well enough to lint the manifest it produces. It understands the parts of Go templates
/// that chart templates mostly use: <c>.Values</c>, <c>.Chart</c> and <c>.Release</c> lookups, pipelines, the
/// common Sprig functions (<c>default</c>, <c>quote</c>, <c>toYaml</c>, <c>nindent</c>, ...), <c>if</c>/<c>else</c>,
/// <c>with</c>, <c>range</c>, variables and whitespace trimming.
/// </summary>
/// <remarks>
/// It is not Helm. <c>include</c> and <c>template</c> render the name of the named template instead of its body, and
/// unknown functions render nothing but count as true in conditions, so optional blocks are linted rather than skipped.
/// </remarks>
public sealed class HelmTemplateRenderer
{
    /// <summary>Stands for a value the renderer cannot compute. Renders as nothing; true in conditions.</summary>
    private sealed class Unknown
    {
        public static readonly Unknown Value = new();
    }

    private readonly Dictionary<string, object?> _root;

    public HelmTemplateRenderer(YamlMappingNode? values, HelmChartMetadata chart, string releaseName = "release-name", string releaseNamespace = "default")
    {
        _root = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["Values"] = values ?? new YamlMappingNode(),
            ["Chart"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Name"] = chart.Name,
                ["Version"] = chart.Version,
                ["AppVersion"] = chart.AppVersion,
            },
            ["Release"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["Name"] = releaseName,
                ["Namespace"] = releaseNamespace,
                ["Service"] = "Helm",
                ["IsInstall"] = true,
                ["IsUpgrade"] = false,
            },
            ["Capabilities"] = new Dictionary<string, object?>(StringComparer.Ordinal),
        };
    }

    public RenderedTemplate Render(string template)
    {
        var nodes = TemplateParser.Parse(template);
        var output = new Output();
        var scope = new Scope(_root, new Dictionary<string, object?>(StringComparer.Ordinal) { ["$"] = _root });
        RenderNodes(nodes, scope, output);
        return output.ToRendered();
    }

    private void RenderNodes(IEnumerable<Node> nodes, Scope scope, Output output)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case TextNode text:
                    output.AppendSource(text.Text, text.Line);
                    break;
                case ActionNode action:
                    var value = EvalPipeline(action.Pipeline, scope, action.Line);
                    if (action.Variables.Count > 0)
                    {
                        scope.Variables[action.Variables[0]] = value;
                    }
                    else
                    {
                        output.AppendGenerated(Stringify(value), action.Line);
                    }

                    break;
                case IfNode conditional:
                    var branch = conditional.Branches.FirstOrDefault(b => IsTrue(EvalPipeline(b.Condition, scope, conditional.Line)));
                    RenderNodes(branch?.Body ?? conditional.Else, scope, output);
                    break;
                case WithNode with:
                    var subject = EvalPipeline(with.Pipeline, scope, with.Line);
                    if (IsTrue(subject))
                    {
                        RenderNodes(with.Body, scope.With(subject), output);
                    }
                    else
                    {
                        RenderNodes(with.Else, scope, output);
                    }

                    break;
                case RangeNode range:
                    RenderRange(range, scope, output);
                    break;
            }
        }
    }

    private void RenderRange(RangeNode range, Scope scope, Output output)
    {
        var items = EvalPipeline(range.Pipeline, scope, range.Line) switch
        {
            YamlSequenceNode sequence => sequence.Children.Select((item, i) => ((object?)i.ToString(CultureInfo.InvariantCulture), Normalize(item))).ToList(),
            YamlMappingNode mapping => mapping.Children.Select(pair => ((object?)((YamlScalarNode)pair.Key).Value, Normalize(pair.Value))).ToList(),
            Dictionary<string, object?> dictionary => dictionary.Select(pair => ((object?)pair.Key, pair.Value)).ToList(),
            List<object?> list => list.Select((item, i) => ((object?)i.ToString(CultureInfo.InvariantCulture), item)).ToList(),
            _ => [],
        };

        if (items.Count == 0)
        {
            RenderNodes(range.Else, scope, output);
            return;
        }

        foreach (var (key, item) in items)
        {
            var inner = scope.With(item);
            switch (range.Variables.Count)
            {
                case 1:
                    inner.Variables[range.Variables[0]] = item;
                    break;
                case 2:
                    inner.Variables[range.Variables[0]] = key;
                    inner.Variables[range.Variables[1]] = item;
                    break;
            }

            RenderNodes(range.Body, inner, output);
        }
    }

    private object? EvalPipeline(IReadOnlyList<Token> tokens, Scope scope, int line)
    {
        object? value = null;
        var piped = false;
        foreach (var command in SplitTopLevel(tokens, TokenKind.Pipe))
        {
            value = EvalCommand(command, scope, piped, value, line);
            piped = true;
        }

        return value;
    }

    private object? EvalCommand(List<Token> tokens, Scope scope, bool hasPiped, object? piped, int line)
    {
        var operands = SplitOperands(tokens, line);
        if (operands.Count == 0)
        {
            return hasPiped ? piped : null;
        }

        var first = operands[0];
        if (first is [{ Kind: TokenKind.Identifier } function] && function.Text is not ("true" or "false" or "nil"))
        {
            var arguments = operands.Skip(1).Select(o => EvalOperand(o, scope, line)).ToList();
            if (hasPiped)
            {
                arguments.Add(piped);
            }

            return CallFunction(function.Text, arguments);
        }

        return EvalOperand(first, scope, line);
    }

    private object? EvalOperand(List<Token> operand, Scope scope, int line)
    {
        if (operand is [{ Kind: TokenKind.OpenParen }, .., { Kind: TokenKind.CloseParen }])
        {
            return EvalPipeline(operand[1..^1], scope, line);
        }

        var token = operand[0];
        return token.Kind switch
        {
            TokenKind.String => token.Text,
            TokenKind.Number => token.Text,
            TokenKind.Identifier => token.Text switch
            {
                "true" => true,
                "false" => false,
                "nil" => null,
                var name => CallFunction(name, []),
            },
            TokenKind.Field => Lookup(scope.Dot, token.Text),
            TokenKind.Variable => LookupVariable(scope, token.Text),
            _ => throw new HelmTemplateException($"Unexpected '{token.Text}' in an action.", line),
        };
    }

    private static object? LookupVariable(Scope scope, string text)
    {
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        var name = dot < 0 ? text : text[..dot];
        if (!scope.Variables.TryGetValue(name, out var value))
        {
            return Unknown.Value;
        }

        return dot < 0 ? value : Lookup(value, text[dot..]);
    }

    /// <summary>Follows a field chain such as <c>.Values.image.tag</c> from <paramref name="target"/>. <c>.</c> alone is the target.</summary>
    private static object? Lookup(object? target, string chain)
    {
        var current = target;
        foreach (var name in chain.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            current = current switch
            {
                Dictionary<string, object?> dictionary => dictionary.GetValueOrDefault(name),
                YamlMappingNode mapping => mapping.Children.TryGetValue(new YamlScalarNode(name), out var child) ? Normalize(child) : null,
                Unknown => Unknown.Value,
                _ => null,
            };
        }

        return current;
    }

    /// <summary>Plain YAML scalars become bools, null or strings; quoted scalars stay strings; collections stay nodes.</summary>
    internal static object? Normalize(YamlNode node) => node switch
    {
        YamlScalarNode { Style: ScalarStyle.Plain or ScalarStyle.Any } plain => plain.Value switch
        {
            null or "" or "~" or "null" or "Null" or "NULL" => null,
            "true" or "True" or "TRUE" => true,
            "false" or "False" or "FALSE" => false,
            var text => text,
        },
        YamlScalarNode scalar => scalar.Value ?? string.Empty,
        _ => node,
    };

    private object? CallFunction(string name, List<object?> args)
    {
        string Arg(int index) => index < args.Count ? Stringify(args[index]) : string.Empty;
        string Last() => args.Count > 0 ? Stringify(args[^1]) : string.Empty;
        int Count(int index) => int.TryParse(Arg(index), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

        return name switch
        {
            "default" => args.Count >= 2 && !IsEmpty(args[^1]) ? args[^1] : args.Count > 0 ? args[0] : null,
            "quote" => string.Join(' ', args.Select(a => $"\"{Stringify(a).Replace("\"", "\\\"", StringComparison.Ordinal)}\"")),
            "squote" => string.Join(' ', args.Select(a => $"'{Stringify(a)}'")),
            "toYaml" => args.Count > 0 ? ToYaml(args[^1]) : "null",
            "toJson" or "toPrettyJson" or "toRawJson" => args.Count > 0 ? ToFlow(args[^1]) : "null",
            "nindent" => "\n" + Indent(Last(), Count(0)),
            "indent" => Indent(Last(), Count(0)),
            "include" or "template" => Arg(0),
            "tpl" or "required" or "toString" or "int" or "int64" or "float64" or "b64enc" or "b64dec" or "sha256sum" => args.Count > 0 ? args[^1] : null,
            "printf" => Printf(Arg(0), args.Skip(1).ToList()),
            "print" => string.Concat(args.Select(Stringify)),
            "upper" => Last().ToUpperInvariant(),
            "lower" => Last().ToLowerInvariant(),
            "trim" => Last().Trim(),
            "trunc" => Last() is var text && Count(0) is > 0 and var length && text.Length > length ? text[..length] : Last(),
            "trimSuffix" => Last().EndsWith(Arg(0), StringComparison.Ordinal) && Arg(0).Length > 0 ? Last()[..^Arg(0).Length] : Last(),
            "trimPrefix" => Last().StartsWith(Arg(0), StringComparison.Ordinal) && Arg(0).Length > 0 ? Last()[Arg(0).Length..] : Last(),
            "replace" => Arg(0).Length > 0 ? Last().Replace(Arg(0), Arg(1), StringComparison.Ordinal) : Last(),
            "contains" => Last().Contains(Arg(0), StringComparison.Ordinal),
            "hasPrefix" => Last().StartsWith(Arg(0), StringComparison.Ordinal),
            "hasSuffix" => Last().EndsWith(Arg(0), StringComparison.Ordinal),
            "empty" => args.Count == 0 || IsEmpty(args[^1]),
            "not" => args.Count == 0 || !IsTrue(args[^1]),
            "and" => args.All(IsTrue) ? args.LastOrDefault() : args.First(a => !IsTrue(a)),
            "or" => args.FirstOrDefault(IsTrue) ?? args.LastOrDefault(),
            "eq" => args.Count >= 2 && args.Skip(1).Any(a => Stringify(a) == Arg(0)),
            "ne" => args.Count >= 2 && Arg(0) != Arg(1),
            "hasKey" => args.Count >= 2 && Lookup(args[0], "." + Arg(1)) is not null,
            "list" => args.ToList(),
            "dict" => args.Chunk(2).Where(pair => pair.Length == 2)
                .ToDictionary(pair => Stringify(pair[0]), pair => pair[1], StringComparer.Ordinal),
            _ => Unknown.Value,
        };
    }

    private static string Printf(string format, List<object?> args)
    {
        var result = new StringBuilder();
        var next = 0;
        for (var i = 0; i < format.Length; i++)
        {
            if (format[i] == '%' && i + 1 < format.Length)
            {
                var verb = format[++i];
                if (verb == '%')
                {
                    result.Append('%');
                    continue;
                }

                var value = next < args.Count ? Stringify(args[next++]) : string.Empty;
                result.Append(verb == 'q' ? $"\"{value}\"" : value);
                continue;
            }

            result.Append(format[i]);
        }

        return result.ToString();
    }

    private static string Indent(string text, int spaces)
    {
        var pad = new string(' ', Math.Max(0, spaces));
        return string.Join('\n', text.Split('\n').Select(line => pad + line));
    }

    private static bool IsEmpty(object? value) => value switch
    {
        null => true,
        Unknown => false,
        bool b => !b,
        string s => s.Length == 0,
        YamlMappingNode mapping => mapping.Children.Count == 0,
        YamlSequenceNode sequence => sequence.Children.Count == 0,
        Dictionary<string, object?> dictionary => dictionary.Count == 0,
        List<object?> list => list.Count == 0,
        _ => false,
    };

    private static bool IsTrue(object? value) => !IsEmpty(value);

    internal static string Stringify(object? value) => value switch
    {
        null or Unknown => string.Empty,
        bool b => b ? "true" : "false",
        string s => s,
        YamlNode node => ToFlow(node),
        Dictionary<string, object?> or List<object?> => ToFlow(value),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty,
    };

    /// <summary>Block-style YAML for a value, the way Helm's <c>toYaml</c> prints it (no trailing newline).</summary>
    internal static string ToYaml(object? value) => value switch
    {
        YamlMappingNode { Children.Count: 0 } or Dictionary<string, object?> { Count: 0 } => "{}",
        YamlSequenceNode { Children.Count: 0 } or List<object?> { Count: 0 } => "[]",
        YamlNode node => string.Join('\n', YamlLines(node)),
        null => "null",
        _ => FormatScalar(Stringify(value), quoted: false),
    };

    private static IEnumerable<string> YamlLines(YamlNode node)
    {
        switch (node)
        {
            case YamlMappingNode mapping:
                foreach (var (key, child) in mapping.Children)
                {
                    var name = ((YamlScalarNode)key).Value ?? string.Empty;
                    if (child is YamlScalarNode || IsEmptyCollection(child))
                    {
                        yield return $"{name}: {InlineValue(child)}";
                        continue;
                    }

                    yield return $"{name}:";
                    var indent = child is YamlMappingNode ? "  " : string.Empty;
                    foreach (var line in YamlLines(child))
                    {
                        yield return indent + line;
                    }
                }

                break;
            case YamlSequenceNode sequence:
                foreach (var item in sequence.Children)
                {
                    if (item is YamlScalarNode || IsEmptyCollection(item))
                    {
                        yield return $"- {InlineValue(item)}";
                        continue;
                    }

                    var first = true;
                    foreach (var line in YamlLines(item))
                    {
                        yield return (first ? "- " : "  ") + line;
                        first = false;
                    }
                }

                break;
            default:
                yield return InlineValue(node);
                break;
        }
    }

    private static bool IsEmptyCollection(YamlNode node) =>
        node is YamlMappingNode { Children.Count: 0 } or YamlSequenceNode { Children.Count: 0 };

    private static string InlineValue(YamlNode node) => node switch
    {
        YamlMappingNode => "{}",
        YamlSequenceNode => "[]",
        YamlScalarNode scalar => FormatScalar(scalar.Value ?? string.Empty, scalar.Style is not (ScalarStyle.Plain or ScalarStyle.Any)),
        _ => string.Empty,
    };

    private static string FormatScalar(string value, bool quoted) =>
        quoted || value.Contains('\n') || value.Length == 0
            ? System.Text.Json.JsonSerializer.Serialize(value)
            : value;

    /// <summary>Single-line flow form, used for <c>toJson</c> and for collections printed directly.</summary>
    private static string ToFlow(object? value) => value switch
    {
        null => "null",
        YamlMappingNode mapping => "{" + string.Join(",", mapping.Children.Select(p => $"\"{((YamlScalarNode)p.Key).Value}\":{ToFlow(Normalize(p.Value))}")) + "}",
        YamlSequenceNode sequence => "[" + string.Join(",", sequence.Children.Select(item => ToFlow(Normalize(item)))) + "]",
        Dictionary<string, object?> dictionary => "{" + string.Join(",", dictionary.Select(p => $"\"{p.Key}\":{ToFlow(p.Value)}")) + "}",
        List<object?> list => "[" + string.Join(",", list.Select(ToFlow)) + "]",
        bool b => b ? "true" : "false",
        _ => System.Text.Json.JsonSerializer.Serialize(Stringify(value)),
    };

    private static List<List<Token>> SplitTopLevel(IReadOnlyList<Token> tokens, TokenKind separator)
    {
        var parts = new List<List<Token>> { new() };
        var depth = 0;
        foreach (var token in tokens)
        {
            depth += token.Kind switch { TokenKind.OpenParen => 1, TokenKind.CloseParen => -1, _ => 0 };
            if (token.Kind == separator && depth == 0)
            {
                parts.Add([]);
                continue;
            }

            parts[^1].Add(token);
        }

        return parts.Where(p => p.Count > 0).ToList();
    }

    /// <summary>Groups a command's tokens into operands; a parenthesized pipeline is one operand.</summary>
    private static List<List<Token>> SplitOperands(List<Token> tokens, int line)
    {
        var operands = new List<List<Token>>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.OpenParen)
            {
                operands.Add([tokens[i]]);
                continue;
            }

            var depth = 0;
            var start = i;
            for (; i < tokens.Count; i++)
            {
                depth += tokens[i].Kind switch { TokenKind.OpenParen => 1, TokenKind.CloseParen => -1, _ => 0 };
                if (depth == 0)
                {
                    break;
                }
            }

            if (depth != 0)
            {
                throw new HelmTemplateException("Unbalanced parentheses in an action.", line);
            }

            operands.Add(tokens[start..(i + 1)]);
        }

        return operands;
    }

    private sealed class Scope(object? dot, Dictionary<string, object?> variables)
    {
        public object? Dot { get; } = dot;

        public Dictionary<string, object?> Variables { get; } = variables;

        public Scope With(object? dot) => new(dot, new Dictionary<string, object?>(Variables, StringComparer.Ordinal));
    }

    /// <summary>Collects rendered text and remembers which template line each rendered line starts on.</summary>
    private sealed class Output
    {
        private readonly StringBuilder _text = new();
        private readonly List<int> _lines = [];

        public void AppendSource(string text, int line)
        {
            if (_lines.Count == 0)
            {
                _lines.Add(line);
            }

            foreach (var c in text)
            {
                _text.Append(c);
                if (c == '\n')
                {
                    _lines.Add(++line);
                }
            }
        }

        public void AppendGenerated(string text, int line)
        {
            if (_lines.Count == 0)
            {
                _lines.Add(line);
            }

            foreach (var c in text)
            {
                _text.Append(c);
                if (c == '\n')
                {
                    _lines.Add(line);
                }
            }
        }

        public RenderedTemplate ToRendered() => new(_text.ToString(), _lines.Count == 0 ? [1] : _lines.ToArray());
    }

    // ---- parsing ----

    internal enum TokenKind
    {
        String,
        Number,
        Identifier,
        Field,
        Variable,
        Pipe,
        OpenParen,
        CloseParen,
        Comma,
        Declare,
        Assign,
    }

    internal readonly record struct Token(TokenKind Kind, string Text);

    private abstract record Node(int Line);

    private sealed record TextNode(string Text, int Line) : Node(Line);

    private sealed record ActionNode(IReadOnlyList<Token> Pipeline, IReadOnlyList<string> Variables, int Line) : Node(Line);

    private sealed record Branch(IReadOnlyList<Token> Condition, List<Node> Body);

    private sealed record IfNode(List<Branch> Branches, List<Node> Else, int Line) : Node(Line);

    private sealed record WithNode(IReadOnlyList<Token> Pipeline, List<Node> Body, List<Node> Else, int Line) : Node(Line);

    private sealed record RangeNode(IReadOnlyList<Token> Pipeline, IReadOnlyList<string> Variables, List<Node> Body, List<Node> Else, int Line) : Node(Line);

    private static class TemplateParser
    {
        private sealed record Segment(bool IsAction, string Text, int Line);

        public static List<Node> Parse(string template)
        {
            var segments = Lex(template);
            var position = 0;
            var nodes = ParseBlock(segments, ref position, out var terminator);
            if (terminator is not null)
            {
                throw new HelmTemplateException($"'{{{{ {terminator.Text} }}}}' has no block to close.", terminator.Line);
            }

            return nodes;
        }

        /// <summary>Parses nodes until an <c>end</c> or <c>else</c> action, which is returned in <paramref name="terminator"/>.</summary>
        private static List<Node> ParseBlock(List<Segment> segments, ref int position, out Segment? terminator)
        {
            var nodes = new List<Node>();
            terminator = null;

            while (position < segments.Count)
            {
                var segment = segments[position++];
                if (!segment.IsAction)
                {
                    nodes.Add(new TextNode(segment.Text, segment.Line));
                    continue;
                }

                var (keyword, rest) = SplitKeyword(segment.Text);
                switch (keyword)
                {
                    case "end" or "else":
                        terminator = segment;
                        return nodes;
                    case "if":
                        nodes.Add(ParseIf(segments, ref position, rest, segment.Line));
                        break;
                    case "with":
                    {
                        var body = ParseBlock(segments, ref position, out var end);
                        var otherwise = ParseElse(segments, ref position, end, segment.Line);
                        nodes.Add(new WithNode(Tokenize(rest, segment.Line), body, otherwise, segment.Line));
                        break;
                    }

                    case "range":
                    {
                        var (variables, pipeline) = SplitDeclaration(Tokenize(rest, segment.Line));
                        var body = ParseBlock(segments, ref position, out var end);
                        var otherwise = ParseElse(segments, ref position, end, segment.Line);
                        nodes.Add(new RangeNode(pipeline, variables, body, otherwise, segment.Line));
                        break;
                    }

                    case "define" or "block":
                        // Named templates are not rendered in place; skip the body.
                        ParseBlock(segments, ref position, out var defineEnd);
                        RequireEnd(defineEnd, keyword, segment.Line);
                        break;
                    case "/*":
                        break;
                    default:
                    {
                        var (variables, pipeline) = SplitDeclaration(Tokenize(segment.Text, segment.Line));
                        nodes.Add(new ActionNode(pipeline, variables, segment.Line));
                        break;
                    }
                }
            }

            return nodes;
        }

        private static IfNode ParseIf(List<Segment> segments, ref int position, string condition, int line)
        {
            var branches = new List<Branch>();
            var currentCondition = Tokenize(condition, line);
            while (true)
            {
                var body = ParseBlock(segments, ref position, out var end);
                branches.Add(new Branch(currentCondition, body));
                if (end is null)
                {
                    throw new HelmTemplateException("'{{ if }}' is never closed with '{{ end }}'.", line);
                }

                var (keyword, rest) = SplitKeyword(end.Text);
                if (keyword == "end")
                {
                    return new IfNode(branches, [], line);
                }

                var (elseKeyword, elseRest) = SplitKeyword(rest);
                if (elseKeyword == "if")
                {
                    currentCondition = Tokenize(elseRest, end.Line);
                    continue;
                }

                var elseBody = ParseBlock(segments, ref position, out var finalEnd);
                RequireEnd(finalEnd, "if", line);
                return new IfNode(branches, elseBody, line);
            }
        }

        private static List<Node> ParseElse(List<Segment> segments, ref int position, Segment? end, int line)
        {
            if (end is null)
            {
                throw new HelmTemplateException("A block is never closed with '{{ end }}'.", line);
            }

            if (SplitKeyword(end.Text).Keyword == "end")
            {
                return [];
            }

            var elseBody = ParseBlock(segments, ref position, out var finalEnd);
            RequireEnd(finalEnd, "else", line);
            return elseBody;
        }

        private static void RequireEnd(Segment? end, string keyword, int line)
        {
            if (end is null || SplitKeyword(end.Text).Keyword != "end")
            {
                throw new HelmTemplateException($"'{{{{ {keyword} }}}}' is never closed with '{{{{ end }}}}'.", line);
            }
        }

        private static (string Keyword, string Arguments) SplitKeyword(string action)
        {
            var text = action.TrimStart();
            if (text.StartsWith("/*", StringComparison.Ordinal))
            {
                return ("/*", string.Empty);
            }

            var space = text.IndexOfAny([' ', '\t', '\n', '\r']);
            return space < 0 ? (text, string.Empty) : (text[..space], text[(space + 1)..].Trim());
        }

        /// <summary>Splits <c>$a, $b := pipeline</c> into the variable names and the pipeline.</summary>
        private static (IReadOnlyList<string> Variables, IReadOnlyList<Token> Pipeline) SplitDeclaration(List<Token> tokens)
        {
            var declare = tokens.FindIndex(t => t.Kind is TokenKind.Declare or TokenKind.Assign);
            if (declare < 0 || tokens.Take(declare).Any(t => t.Kind is not (TokenKind.Variable or TokenKind.Comma)))
            {
                return ([], tokens);
            }

            var variables = tokens.Take(declare).Where(t => t.Kind == TokenKind.Variable).Select(t => t.Text).ToArray();
            return (variables, tokens[(declare + 1)..]);
        }

        /// <summary>Splits the template into text and action segments, applying <c>{{-</c> and <c>-}}</c> trimming.</summary>
        private static List<Segment> Lex(string template)
        {
            var segments = new List<Segment>();
            var position = 0;
            var line = 1;
            var trimNext = false;

            while (position < template.Length)
            {
                var open = template.IndexOf("{{", position, StringComparison.Ordinal);
                var textEnd = open < 0 ? template.Length : open;
                var text = template[position..textEnd];
                var textLine = line;
                line += Count(text, '\n');

                if (trimNext)
                {
                    var trimmed = text.TrimStart();
                    textLine += Count(text[..(text.Length - trimmed.Length)], '\n');
                    text = trimmed;
                }

                if (open < 0)
                {
                    AddText(segments, text, textLine);
                    break;
                }

                var close = template.IndexOf("}}", open + 2, StringComparison.Ordinal);
                if (close < 0)
                {
                    throw new HelmTemplateException("An action is opened with '{{' but never closed.", line);
                }

                var inner = template[(open + 2)..close];
                var trimBefore = inner.StartsWith('-') && (inner.Length == 1 || char.IsWhiteSpace(inner[1]));
                trimNext = inner.EndsWith('-') && (inner.Length == 1 || char.IsWhiteSpace(inner[^2]));
                if (trimBefore)
                {
                    text = text.TrimEnd();
                    inner = inner[1..];
                }

                if (trimNext)
                {
                    inner = inner[..^1];
                }

                AddText(segments, text, textLine);
                segments.Add(new Segment(IsAction: true, inner.Trim(), line));
                line += Count(inner, '\n');
                position = close + 2;
            }

            return segments;
        }

        private static void AddText(List<Segment> segments, string text, int line)
        {
            if (text.Length > 0)
            {
                segments.Add(new Segment(IsAction: false, text, line));
            }
        }

        private static int Count(string text, char c) => text.Count(x => x == c);

        internal static List<Token> Tokenize(string action, int line)
        {
            var tokens = new List<Token>();
            var i = 0;
            while (i < action.Length)
            {
                var c = action[i];
                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                switch (c)
                {
                    case '|':
                        tokens.Add(new Token(TokenKind.Pipe, "|"));
                        i++;
                        continue;
                    case '(':
                        tokens.Add(new Token(TokenKind.OpenParen, "("));
                        i++;
                        continue;
                    case ')':
                        tokens.Add(new Token(TokenKind.CloseParen, ")"));
                        i++;
                        continue;
                    case ',':
                        tokens.Add(new Token(TokenKind.Comma, ","));
                        i++;
                        continue;
                    case ':' when i + 1 < action.Length && action[i + 1] == '=':
                        tokens.Add(new Token(TokenKind.Declare, ":="));
                        i += 2;
                        continue;
                    case '=':
                        tokens.Add(new Token(TokenKind.Assign, "="));
                        i++;
                        continue;
                    case '"':
                        tokens.Add(new Token(TokenKind.String, ReadQuoted(action, ref i, line)));
                        continue;
                    case '`':
                    {
                        var end = action.IndexOf('`', i + 1);
                        if (end < 0)
                        {
                            throw new HelmTemplateException("Unterminated raw string in an action.", line);
                        }

                        tokens.Add(new Token(TokenKind.String, action[(i + 1)..end]));
                        i = end + 1;
                        continue;
                    }
                }

                var start = i;
                while (i < action.Length && !char.IsWhiteSpace(action[i]) && action[i] is not ('|' or '(' or ')' or ',' or '"' or '='))
                {
                    if (action[i] == ':' && i + 1 < action.Length && action[i + 1] == '=')
                    {
                        break;
                    }

                    i++;
                }

                var word = action[start..i];
                var kind = word[0] switch
                {
                    '.' => TokenKind.Field,
                    '$' => TokenKind.Variable,
                    '-' or '+' or (>= '0' and <= '9') => TokenKind.Number,
                    _ => TokenKind.Identifier,
                };
                tokens.Add(new Token(kind, word));
            }

            return tokens;
        }

        private static string ReadQuoted(string action, ref int i, int line)
        {
            var value = new StringBuilder();
            for (i++; i < action.Length; i++)
            {
                var c = action[i];
                if (c == '\\' && i + 1 < action.Length)
                {
                    var escaped = action[++i];
                    value.Append(escaped switch { 'n' => '\n', 't' => '\t', _ => escaped });
                    continue;
                }

                if (c == '"')
                {
                    i++;
                    return value.ToString();
                }

                value.Append(c);
            }

            throw new HelmTemplateException("Unterminated string in an action.", line);
        }
    }
}
