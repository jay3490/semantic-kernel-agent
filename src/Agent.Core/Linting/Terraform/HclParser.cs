using System.Text;

namespace Agent.Core.Linting.Terraform;

/// <summary><c>name = expression</c>. <see cref="Expression"/> is the raw source text of the value.</summary>
public sealed record HclAttribute(string Name, string Expression, int Line);

/// <summary><c>type "label" "label" { ... }</c>.</summary>
public sealed record HclBlock(string Type, IReadOnlyList<string> Labels, HclBody Body, int Line);

/// <summary>The attributes and nested blocks between a pair of braces, or of a whole file.</summary>
public sealed record HclBody(IReadOnlyList<HclAttribute> Attributes, IReadOnlyList<HclBlock> Blocks)
{
    public HclAttribute? Attribute(string name) => Attributes.FirstOrDefault(a => a.Name == name);

    public IEnumerable<HclBlock> BlocksOfType(string type) => Blocks.Where(b => b.Type == type);
}

/// <summary>Raised for HCL that cannot be split into attributes and blocks, such as an unclosed brace.</summary>
public sealed class HclSyntaxException(string message, int line) : Exception(message)
{
    public int Line { get; } = line;
}

/// <summary>
/// Reads the structure of HCL (Terraform) files: blocks, their labels, and attributes. Expressions are not
/// evaluated; they are kept as source text, and <see cref="HclValues"/> reads the literal ones. Comments, strings
/// with interpolation, heredocs and multi-line lists and objects are handled.
/// </summary>
public static class HclParser
{
    public static HclBody Parse(string text)
    {
        var reader = new Reader(text);
        var body = ParseBody(reader, closing: null, commaSeparated: false);
        return body;
    }

    /// <summary>Parses the inside of an object expression such as <c>{ source = "hashicorp/aws", version = "~> 5.0" }</c>.</summary>
    public static HclBody ParseObject(string expression, int line)
    {
        var trimmed = expression.Trim();
        if (!trimmed.StartsWith('{') || !trimmed.EndsWith('}'))
        {
            return new HclBody([], []);
        }

        var reader = new Reader(trimmed[1..^1], line);
        return ParseBody(reader, closing: null, commaSeparated: true);
    }

    private static HclBody ParseBody(Reader reader, int? closing, bool commaSeparated)
    {
        var attributes = new List<HclAttribute>();
        var blocks = new List<HclBlock>();

        while (true)
        {
            reader.SkipTrivia(newlines: true, commas: commaSeparated);
            if (reader.AtEnd)
            {
                if (closing is { } openLine)
                {
                    throw new HclSyntaxException("A block opened here is never closed with '}'.", openLine);
                }

                return new HclBody(attributes, blocks);
            }

            if (reader.Peek == '}')
            {
                if (closing is null)
                {
                    throw new HclSyntaxException("Unexpected '}' without a matching '{'.", reader.Line);
                }

                reader.Advance();
                return new HclBody(attributes, blocks);
            }

            var line = reader.Line;
            var name = reader.ReadName();
            if (name.Length == 0)
            {
                throw new HclSyntaxException($"Unexpected '{reader.Peek}'; expected an attribute or block name.", line);
            }

            reader.SkipTrivia(newlines: false, commas: false);
            if (reader.Peek is '=' or ':' && reader.PeekAt(1) != '=')
            {
                reader.Advance();
                attributes.Add(new HclAttribute(name, reader.ReadExpression(stopAtComma: commaSeparated), line));
                continue;
            }

            var labels = new List<string>();
            while (!reader.AtEnd && reader.Peek != '{')
            {
                var label = reader.Peek == '"' ? Unquote(reader.ReadString()) : reader.ReadName();
                if (label.Length == 0)
                {
                    throw new HclSyntaxException($"Unexpected '{reader.Peek}' after '{name}'; expected a label, '=' or '{{'.", reader.Line);
                }

                labels.Add(label);
                reader.SkipTrivia(newlines: false, commas: false);
            }

            if (reader.AtEnd)
            {
                throw new HclSyntaxException($"Block '{name}' has no body.", line);
            }

            reader.Advance();
            blocks.Add(new HclBlock(name, labels, ParseBody(reader, line, commaSeparated: false), line));
        }
    }

    private static string Unquote(string quoted) => HclValues.String(quoted) ?? quoted.Trim('"');

    private sealed class Reader(string text, int firstLine = 1)
    {
        private int _position;

        public int Line { get; private set; } = firstLine;

        public bool AtEnd => _position >= text.Length;

        public char Peek => AtEnd ? '\0' : text[_position];

        public char PeekAt(int offset) => _position + offset < text.Length ? text[_position + offset] : '\0';

        public void Advance()
        {
            if (text[_position] == '\n')
            {
                Line++;
            }

            _position++;
        }

        public void SkipTrivia(bool newlines, bool commas)
        {
            while (!AtEnd)
            {
                var c = Peek;
                if (c == '\n' && !newlines)
                {
                    return;
                }

                if (char.IsWhiteSpace(c) || (commas && c == ','))
                {
                    Advance();
                }
                else if (c == '#' || (c == '/' && PeekAt(1) == '/'))
                {
                    while (!AtEnd && Peek != '\n')
                    {
                        Advance();
                    }
                }
                else if (c == '/' && PeekAt(1) == '*')
                {
                    SkipBlockComment();
                }
                else
                {
                    return;
                }
            }
        }

        public string ReadName()
        {
            var start = _position;
            while (!AtEnd && (char.IsLetterOrDigit(Peek) || Peek is '_' or '-'))
            {
                Advance();
            }

            return text[start.._position];
        }

        public string ReadString()
        {
            var start = _position;
            var line = Line;
            Advance();
            while (!AtEnd)
            {
                var c = Peek;
                if (c == '\\')
                {
                    Advance();
                    if (!AtEnd)
                    {
                        Advance();
                    }

                    continue;
                }

                if ((c == '$' || c == '%') && PeekAt(1) == '{')
                {
                    Advance();
                    Advance();
                    SkipTemplateInterpolation(line);
                    continue;
                }

                Advance();
                if (c == '"')
                {
                    return text[start.._position];
                }

                if (c == '\n')
                {
                    break;
                }
            }

            throw new HclSyntaxException("Unterminated string.", line);
        }

        /// <summary>
        /// Reads an expression up to the end of its line, continuing across lines while brackets are open.
        /// Stops before a '}' that closes the enclosing block, and before a ',' when reading object members.
        /// </summary>
        public string ReadExpression(bool stopAtComma)
        {
            var value = new StringBuilder();
            var depth = 0;
            var line = Line;

            while (!AtEnd)
            {
                var c = Peek;
                if (depth == 0 && (c == '\n' || c == '}' || (stopAtComma && c == ',')))
                {
                    break;
                }

                if (c == '"')
                {
                    value.Append(ReadString());
                    continue;
                }

                if (c == '<' && PeekAt(1) == '<' && (char.IsLetter(PeekAt(2)) || PeekAt(2) == '-'))
                {
                    value.Append(ReadHeredoc());
                    continue;
                }

                if (c == '#' || (c == '/' && PeekAt(1) == '/'))
                {
                    SkipTrivia(newlines: false, commas: false);
                    continue;
                }

                if (c == '/' && PeekAt(1) == '*')
                {
                    SkipBlockComment();
                    continue;
                }

                depth += c switch { '(' or '[' or '{' => 1, ')' or ']' or '}' => -1, _ => 0 };
                if (depth < 0)
                {
                    throw new HclSyntaxException($"Unbalanced '{c}' in an expression.", Line);
                }

                value.Append(c);
                Advance();
            }

            if (depth > 0)
            {
                throw new HclSyntaxException("An expression opened here is never closed.", line);
            }

            return value.ToString().Trim();
        }

        private string ReadHeredoc()
        {
            var start = _position;
            var line = Line;
            Advance();
            Advance();
            if (Peek == '-')
            {
                Advance();
            }

            var marker = ReadName();
            while (!AtEnd)
            {
                var lineStart = _position;
                while (!AtEnd && Peek != '\n')
                {
                    Advance();
                }

                if (text[lineStart.._position].Trim() == marker && lineStart > start + marker.Length + 2)
                {
                    return text[start.._position];
                }

                if (!AtEnd)
                {
                    Advance();
                }
            }

            throw new HclSyntaxException($"Heredoc '{marker}' is never terminated.", line);
        }

        private void SkipTemplateInterpolation(int line)
        {
            var depth = 1;
            while (!AtEnd && depth > 0)
            {
                depth += Peek switch { '{' => 1, '}' => -1, _ => 0 };
                Advance();
            }

            if (depth > 0)
            {
                throw new HclSyntaxException("Unterminated '${' interpolation.", line);
            }
        }

        private void SkipBlockComment()
        {
            var line = Line;
            Advance();
            Advance();
            while (!AtEnd && !(Peek == '*' && PeekAt(1) == '/'))
            {
                Advance();
            }

            if (AtEnd)
            {
                throw new HclSyntaxException("Unterminated '/*' comment.", line);
            }

            Advance();
            Advance();
        }
    }
}

/// <summary>Reads literal values out of raw HCL expressions; anything computed reads as null.</summary>
public static class HclValues
{
    /// <summary>The value of a quoted string without interpolation, such as <c>"~&gt; 5.0"</c>.</summary>
    public static string? String(string? expression)
    {
        var value = expression?.Trim();
        if (value is null || value.Length < 2 || value[0] != '"' || value[^1] != '"' || value.Contains("${", StringComparison.Ordinal))
        {
            return null;
        }

        var inner = value[1..^1];
        return inner.Contains('"') && !inner.Contains("\\\"", StringComparison.Ordinal) ? null : inner.Replace("\\\"", "\"", StringComparison.Ordinal);
    }

    public static bool? Bool(string? expression) => expression?.Trim() switch
    {
        "true" => true,
        "false" => false,
        _ => null,
    };

    public static int? Int(string? expression) =>
        int.TryParse(expression?.Trim(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var value)
            ? value
            : String(expression) is { } text && int.TryParse(text, out var quoted) ? quoted : null;

    /// <summary>The string literals of a list expression such as <c>["10.0.0.0/8", "0.0.0.0/0"]</c>; a lone string is a one-item list.</summary>
    public static IReadOnlyList<string> StringList(string? expression)
    {
        var value = expression?.Trim();
        if (value is null)
        {
            return [];
        }

        if (String(value) is { } single)
        {
            return [single];
        }

        if (!value.StartsWith('[') || !value.EndsWith(']'))
        {
            return [];
        }

        var items = new List<string>();
        var inner = value[1..^1];
        var i = 0;
        while (i < inner.Length)
        {
            var open = inner.IndexOf('"', i);
            if (open < 0)
            {
                break;
            }

            var close = open + 1;
            while (close < inner.Length && (inner[close] != '"' || inner[close - 1] == '\\'))
            {
                close++;
            }

            if (close >= inner.Length)
            {
                break;
            }

            if (String(inner[open..(close + 1)]) is { } item)
            {
                items.Add(item);
            }

            i = close + 1;
        }

        return items;
    }
}
