using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using KnotGarden.Core.Contracts;
using KnotGarden.Core.Domain;

namespace KnotGarden.NodeRuntime;

public enum TokenType
{
    Text,
    PlaceholderStart,
    PlaceholderEnd,
    Number,
    String,
    Boolean,
    Null,
    Identifier,
    Operator,
    OpenParenthesis,
    CloseParenthesis,
    Comma,
    EOF
}

public class Token
{
    public TokenType Type { get; }
    public string Value { get; }

    public Token(TokenType type, string value)
    {
        Type = type;
        Value = value;
    }

    public override string ToString() => $"{Type}: {Value}";
}

public abstract class ExpressionNode { }

public class LiteralNode : ExpressionNode
{
    public object? Value { get; }
    public LiteralNode(object? value) { Value = value; }
}

public class IdentifierNode : ExpressionNode
{
    public string Name { get; }
    public IdentifierNode(string name) { Name = name; }
}

public class BinaryOpNode : ExpressionNode
{
    public string Operator { get; }
    public ExpressionNode Left { get; }
    public ExpressionNode Right { get; }
    public BinaryOpNode(string op, ExpressionNode left, ExpressionNode right)
    {
        Operator = op;
        Left = left;
        Right = right;
    }
}

public class FunctionCallNode : ExpressionNode
{
    public string FunctionName { get; }
    public List<ExpressionNode> Arguments { get; }
    public FunctionCallNode(string name, List<ExpressionNode> args)
    {
        FunctionName = name;
        Arguments = args;
    }
}

public class PlaceholderParser
{
    private readonly List<Token> _tokens;
    private int _index;

    public PlaceholderParser(List<Token> tokens)
    {
        _tokens = tokens;
        _index = 0;
    }

    private Token Current => _index < _tokens.Count ? _tokens[_index] : new Token(TokenType.EOF, "");

    private Token Consume(TokenType type)
    {
        var token = Current;
        if (token.Type != type)
        {
            throw new InvalidOperationException($"Expected token of type {type}, but got {token.Type}");
        }
        _index++;
        return token;
    }

    private bool Match(TokenType type)
    {
        if (Current.Type == type)
        {
            _index++;
            return true;
        }
        return false;
    }

    private bool MatchOp(string op)
    {
        if (Current.Type == TokenType.Operator && Current.Value == op)
        {
            _index++;
            return true;
        }
        return false;
    }

    public ExpressionNode Parse()
    {
        var node = ParseOr();
        if (Current.Type != TokenType.EOF && Current.Type != TokenType.PlaceholderEnd)
        {
            throw new InvalidOperationException($"Unexpected token at end of expression: {Current}");
        }
        return node;
    }

    private ExpressionNode ParseOr()
    {
        var node = ParseAnd();
        while (MatchOp("||"))
        {
            var right = ParseAnd();
            node = new BinaryOpNode("||", node, right);
        }
        return node;
    }

    private ExpressionNode ParseAnd()
    {
        var node = ParseEquality();
        while (MatchOp("&&"))
        {
            var right = ParseEquality();
            node = new BinaryOpNode("&&", node, right);
        }
        return node;
    }

    private ExpressionNode ParseEquality()
    {
        var node = ParseAdditive();
        while (Current.Type == TokenType.Operator && (Current.Value == "==" || Current.Value == "!="))
        {
            string op = Current.Value;
            _index++;
            var right = ParseAdditive();
            node = new BinaryOpNode(op, node, right);
        }
        return node;
    }

    private ExpressionNode ParseAdditive()
    {
        var node = ParseMultiplicative();
        while (Current.Type == TokenType.Operator && (Current.Value == "+" || Current.Value == "-"))
        {
            string op = Current.Value;
            _index++;
            var right = ParseMultiplicative();
            node = new BinaryOpNode(op, node, right);
        }
        return node;
    }

    private ExpressionNode ParseMultiplicative()
    {
        var node = ParsePrimary();
        while (Current.Type == TokenType.Operator && (Current.Value == "*" || Current.Value == "/"))
        {
            string op = Current.Value;
            _index++;
            var right = ParsePrimary();
            node = new BinaryOpNode(op, node, right);
        }
        return node;
    }

    private ExpressionNode ParsePrimary()
    {
        var token = Current;
        if (Match(TokenType.String))
        {
            return new LiteralNode(token.Value);
        }
        if (Match(TokenType.Number))
        {
            if (double.TryParse(token.Value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double d))
            {
                if (d % 1 == 0 && d >= int.MinValue && d <= int.MaxValue)
                {
                    return new LiteralNode((int)d);
                }
                return new LiteralNode(d);
            }
            return new LiteralNode(token.Value);
        }
        if (Match(TokenType.Boolean))
        {
            return new LiteralNode(bool.Parse(token.Value));
        }
        if (Match(TokenType.Null))
        {
            return new LiteralNode(null);
        }
        if (Current.Type == TokenType.Identifier)
        {
            string name = Current.Value;
            _index++;

            if (Match(TokenType.OpenParenthesis))
            {
                var args = new List<ExpressionNode>();
                if (Current.Type != TokenType.CloseParenthesis)
                {
                    args.Add(ParseOr());
                    while (Match(TokenType.Comma))
                    {
                        args.Add(ParseOr());
                    }
                }
                Consume(TokenType.CloseParenthesis);
                return new FunctionCallNode(name, args);
            }

            return new IdentifierNode(name);
        }
        if (Match(TokenType.OpenParenthesis))
        {
            var node = ParseOr();
            Consume(TokenType.CloseParenthesis);
            return node;
        }

        throw new InvalidOperationException($"Unexpected token in expression: {token}");
    }
}

public static class ExpressionEvaluator
{
    public static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        if (string.IsNullOrEmpty(input))
            return tokens;

        int i = 0;
        int len = input.Length;
        bool inPlaceholder = false;

        while (i < len)
        {
            if (!inPlaceholder)
            {
                int nextPlaceholder = input.IndexOf("{{", i);
                if (nextPlaceholder == -1)
                {
                    tokens.Add(new Token(TokenType.Text, input.Substring(i)));
                    break;
                }

                if (nextPlaceholder > i)
                {
                    tokens.Add(new Token(TokenType.Text, input.Substring(i, nextPlaceholder - i)));
                }

                tokens.Add(new Token(TokenType.PlaceholderStart, "{{"));
                inPlaceholder = true;
                i = nextPlaceholder + 2;
            }
            else
            {
                while (i < len && char.IsWhiteSpace(input[i]))
                {
                    i++;
                }

                if (i >= len)
                    break;

                if (i + 1 < len && input[i] == '}' && input[i + 1] == '}')
                {
                    tokens.Add(new Token(TokenType.PlaceholderEnd, "}}"));
                    inPlaceholder = false;
                    i += 2;
                    continue;
                }

                char c = input[i];

                if (c == '\'' || c == '"')
                {
                    char quote = c;
                    var sb = new StringBuilder();
                    i++;
                    while (i < len && input[i] != quote)
                    {
                        sb.Append(input[i]);
                        i++;
                    }
                    if (i < len) i++;
                    tokens.Add(new Token(TokenType.String, sb.ToString()));
                    continue;
                }

                if (i + 1 < len)
                {
                    string op2 = input.Substring(i, 2);
                    if (op2 == "==" || op2 == "!=" || op2 == "&&" || op2 == "||")
                    {
                        tokens.Add(new Token(TokenType.Operator, op2));
                        i += 2;
                        continue;
                    }
                }

                if (c == '+' || c == '-' || c == '*' || c == '/' || c == '!')
                {
                    tokens.Add(new Token(TokenType.Operator, c.ToString()));
                    i++;
                    continue;
                }

                if (c == '(')
                {
                    tokens.Add(new Token(TokenType.OpenParenthesis, "("));
                    i++;
                    continue;
                }
                if (c == ')')
                {
                    tokens.Add(new Token(TokenType.CloseParenthesis, ")"));
                    i++;
                    continue;
                }
                if (c == ',')
                {
                    tokens.Add(new Token(TokenType.Comma, ","));
                    i++;
                    continue;
                }

                if (char.IsDigit(c) || (c == '.' && i + 1 < len && char.IsDigit(input[i + 1])))
                {
                    int start = i;
                    bool hasDot = false;
                    while (i < len && (char.IsDigit(input[i]) || input[i] == '.'))
                    {
                        if (input[i] == '.')
                        {
                            if (hasDot) break;
                            hasDot = true;
                        }
                        i++;
                    }
                    tokens.Add(new Token(TokenType.Number, input.Substring(start, i - start)));
                    continue;
                }

                if (i + 6 <= len && input.Substring(i, 6) == "$node.")
                {
                    int start = i;
                    i += 6;

                    int outputIdx = input.IndexOf(".output.", i);
                    if (outputIdx != -1)
                    {
                        string nodeId = input.Substring(start + 6, outputIdx - (start + 6));
                        i = outputIdx + 8;

                        int pathStart = i;
                        while (i < len && !IsDelimiter(input[i]))
                        {
                            i++;
                        }
                        string outputPath = input.Substring(pathStart, i - pathStart);

                        tokens.Add(new Token(TokenType.Identifier, $"$node.{nodeId}.output.{outputPath}"));
                    }
                    else
                    {
                        while (i < len && !IsDelimiter(input[i]))
                        {
                            i++;
                        }
                        tokens.Add(new Token(TokenType.Identifier, input.Substring(start, i - start)));
                    }
                    continue;
                }

                if (i + 11 <= len && input.Substring(i, 11) == "$variables.")
                {
                    int start = i;
                    i += 11;
                    while (i < len && !IsDelimiter(input[i]))
                    {
                        i++;
                    }
                    tokens.Add(new Token(TokenType.Identifier, input.Substring(start, i - start)));
                    continue;
                }

                if (char.IsLetter(c) || c == '_' || c == '$')
                {
                    int start = i;
                    while (i < len && (char.IsLetterOrDigit(input[i]) || input[i] == '_' || input[i] == '$'))
                    {
                        i++;
                    }
                    string ident = input.Substring(start, i - start);
                    if (ident == "true" || ident == "false")
                    {
                        tokens.Add(new Token(TokenType.Boolean, ident));
                    }
                    else if (ident == "null")
                    {
                        tokens.Add(new Token(TokenType.Null, ident));
                    }
                    else
                    {
                        tokens.Add(new Token(TokenType.Identifier, ident));
                    }
                    continue;
                }

                i++;
            }
        }

        tokens.Add(new Token(TokenType.EOF, ""));
        return tokens;
    }

    private static bool IsDelimiter(char c)
    {
        return char.IsWhiteSpace(c) || c == '}' || c == ')' || c == '(' || c == ',' ||
               c == '=' || c == '!' || c == '&' || c == '|' || c == '+' || c == '-' || c == '*' || c == '/';
    }

    public static object? Evaluate(string input, IWorkflowState state)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        var tokens = Tokenize(input);
        if (tokens.Count == 0)
            return input;

        var activeTokens = tokens.FindAll(t => t.Type != TokenType.EOF);
        if (activeTokens.Count >= 3 &&
            activeTokens[0].Type == TokenType.PlaceholderStart &&
            activeTokens[activeTokens.Count - 1].Type == TokenType.PlaceholderEnd)
        {
            bool hasText = tokens.Exists(t => t.Type == TokenType.Text);
            if (!hasText)
            {
                var insideTokens = activeTokens.GetRange(1, activeTokens.Count - 2);
                insideTokens.Add(new Token(TokenType.EOF, ""));
                var parser = new PlaceholderParser(insideTokens);
                var ast = parser.Parse();
                return EvaluateNode(ast, state);
            }
        }

        var sb = new StringBuilder();
        int i = 0;
        while (i < tokens.Count)
        {
            var token = tokens[i];
            if (token.Type == TokenType.Text)
            {
                sb.Append(token.Value);
                i++;
            }
            else if (token.Type == TokenType.PlaceholderStart)
            {
                i++;
                var placeholderTokens = new List<Token>();
                while (i < tokens.Count && tokens[i].Type != TokenType.PlaceholderEnd)
                {
                    placeholderTokens.Add(tokens[i]);
                    i++;
                }
                if (i < tokens.Count && tokens[i].Type == TokenType.PlaceholderEnd)
                {
                    i++;
                }
                placeholderTokens.Add(new Token(TokenType.EOF, ""));
                var parser = new PlaceholderParser(placeholderTokens);
                var ast = parser.Parse();
                var val = EvaluateNode(ast, state);
                sb.Append(val?.ToString() ?? "");
            }
            else
            {
                i++;
            }
        }
        return sb.ToString();
    }

    private static object? EvaluateNode(ExpressionNode node, IWorkflowState state)
    {
        if (node is LiteralNode lit)
        {
            return lit.Value;
        }

        if (node is IdentifierNode ident)
        {
            string name = ident.Name;
            if (name.StartsWith("$node.", StringComparison.OrdinalIgnoreCase))
            {
                int outputIdx = name.IndexOf(".output.", StringComparison.OrdinalIgnoreCase);
                if (outputIdx == -1)
                    return null;

                string nodeIdStr = name.Substring(6, outputIdx - 6);
                string outputPath = name.Substring(outputIdx + 8); // skip ".output."
                // The shared parser splits the output name (head) from any nested path,
                // so string-key brackets (result["k"]) and indices work uniformly.
                if (!VariablePath.TryParse(outputPath, out var nodePath))
                    return null;

                var element = state.GetNodeOutput(NodeId.Create(nodeIdStr), nodePath!.Head);
                if (element == null)
                    return null;

                if (nodePath.Segments.Count > 0)
                {
                    var nav = NavigateSegments(element.Value, nodePath.Segments);
                    return ConvertJsonElement(nav);
                }

                return ConvertJsonElement(element.Value);
            }

            if (name.StartsWith("$variables.", StringComparison.OrdinalIgnoreCase))
            {
                string reference = name.Substring(11);
                // Malformed path → fall back to a literal flat name (back-compat).
                if (!VariablePath.TryParse(reference, out var varPath))
                    return state.GetVariable<object>(reference);

                var value = state.GetVariable<object>(varPath!.Head);
                if (varPath.Segments.Count == 0)
                    return value;

                if (value is JsonElement je)
                {
                    var nav = NavigateSegments(je, varPath.Segments);
                    return ConvertJsonElement(nav);
                }
                // A path was given but the value isn't navigable JSON → miss.
                return null;
            }

            return state.GetVariable<object>(name);
        }

        if (node is BinaryOpNode bin)
        {
            var leftVal = EvaluateNode(bin.Left, state);

            if (bin.Operator == "&&")
            {
                return AsBoolean(leftVal) && AsBoolean(EvaluateNode(bin.Right, state));
            }
            if (bin.Operator == "||")
            {
                return AsBoolean(leftVal) || AsBoolean(EvaluateNode(bin.Right, state));
            }

            var rightVal = EvaluateNode(bin.Right, state);

            switch (bin.Operator)
            {
                case "==":
                    return ValuesEqual(leftVal, rightVal);
                case "!=":
                    return !ValuesEqual(leftVal, rightVal);
                case "+":
                    if (leftVal is string || rightVal is string)
                        return (leftVal?.ToString() ?? "") + (rightVal?.ToString() ?? "");
                    return AsDouble(leftVal) + AsDouble(rightVal);
                case "-":
                    return AsDouble(leftVal) - AsDouble(rightVal);
                case "*":
                    return AsDouble(leftVal) * AsDouble(rightVal);
                case "/":
                    double denom = AsDouble(rightVal);
                    if (denom == 0) return 0;
                    return AsDouble(leftVal) / denom;
                default:
                    throw new InvalidOperationException($"Unsupported operator: {bin.Operator}");
            }
        }

        if (node is FunctionCallNode func)
        {
            var evaluatedArgs = new List<object?>();
            foreach (var arg in func.Arguments)
            {
                evaluatedArgs.Add(EvaluateNode(arg, state));
            }

            switch (func.FunctionName.ToLowerInvariant())
            {
                case "now":
                    return DateTimeOffset.UtcNow.ToString("o");
                case "uuid":
                    return Guid.NewGuid().ToString();
                case "coalesce":
                    foreach (var arg in evaluatedArgs)
                    {
                        if (arg != null && (arg is not string s || !string.IsNullOrEmpty(s)))
                            return arg;
                    }
                    return null;
                case "length":
                    if (evaluatedArgs.Count > 0 && evaluatedArgs[0] != null)
                    {
                        var arg = evaluatedArgs[0];
                        if (arg is string str) return str.Length;
                        if (arg is JsonElement je && je.ValueKind == JsonValueKind.String) return je.GetString()?.Length ?? 0;
                        if (arg is JsonElement je2 && je2.ValueKind == JsonValueKind.Array) return je2.GetArrayLength();
                        if (arg is System.Collections.IEnumerable list)
                        {
                            int count = 0;
                            foreach (var _ in list) count++;
                            return count;
                        }
                    }
                    return 0;
                default:
                    throw new InvalidOperationException($"Function '{func.FunctionName}' is not in the strictly allowed set.");
            }
        }

        return null;
    }

    /// <summary>
    /// Navigate a JsonElement by an ordered list of parsed path segments. Member
    /// segments index object properties (string keys); Index segments index arrays.
    /// Any miss (wrong kind, absent key, out-of-range, null) returns null — never throws.
    /// </summary>
    internal static JsonElement? NavigateSegments(JsonElement element, IReadOnlyList<PathSegment> segments)
    {
        var current = element;
        foreach (var segment in segments)
        {
            if (current.ValueKind == JsonValueKind.Null || current.ValueKind == JsonValueKind.Undefined)
                return null;

            if (segment is PathSegment.Member member)
            {
                if (current.ValueKind != JsonValueKind.Object ||
                    !current.TryGetProperty(member.Name, out var next))
                    return null;
                current = next;
            }
            else if (segment is PathSegment.Index index)
            {
                if (current.ValueKind != JsonValueKind.Array ||
                    index.Value < 0 || index.Value >= current.GetArrayLength())
                    return null;
                current = current[index.Value];
            }
        }
        return current;
    }

    internal static JsonElement? NavigateJson(JsonElement element, string path)
    {
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var current = element;
        foreach (var part in parts)
        {
            if (current.ValueKind == JsonValueKind.Null || current.ValueKind == JsonValueKind.Undefined)
                return null;

            var cleanPart = part;
            int? index = null;
            if (part.EndsWith("]") && part.Contains("["))
            {
                var openBracketIdx = part.IndexOf('[');
                cleanPart = part.Substring(0, openBracketIdx);
                var indexStr = part.Substring(openBracketIdx + 1, part.Length - openBracketIdx - 2);
                if (int.TryParse(indexStr, out var parsedIndex))
                {
                    index = parsedIndex;
                }
            }

            if (current.ValueKind == JsonValueKind.Object)
            {
                if (current.TryGetProperty(cleanPart, out var nextProp))
                {
                    current = nextProp;
                }
                else
                {
                    return null;
                }
            }
            else
            {
                return null;
            }

            if (index.HasValue)
            {
                if (current.ValueKind == JsonValueKind.Array && index.Value >= 0 && index.Value < current.GetArrayLength())
                {
                    current = current[index.Value];
                }
                else
                {
                    return null;
                }
            }
        }
        return current;
    }

    internal static object? ConvertJsonElement(JsonElement? element)
    {
        if (element == null) return null;
        var el = element.Value;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            _ => el
        };
    }

    private static bool AsBoolean(object? val)
    {
        if (val == null) return false;
        if (val is bool b) return b;
        if (val is string s) return !string.IsNullOrEmpty(s) && !string.Equals(s, "false", StringComparison.OrdinalIgnoreCase);
        if (val is double d) return d != 0;
        if (val is int i) return i != 0;
        if (val is JsonElement je)
        {
            return je.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => false,
                JsonValueKind.String => !string.IsNullOrEmpty(je.GetString()),
                JsonValueKind.Number => je.GetDouble() != 0,
                _ => true
            };
        }
        return true;
    }

    private static double AsDouble(object? val)
    {
        if (val == null) return 0;
        if (val is double d) return d;
        if (val is int i) return i;
        if (val is float f) return f;
        if (val is decimal dec) return (double)dec;
        if (val is string s && double.TryParse(s, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out double parsed))
            return parsed;
        if (val is JsonElement je && je.ValueKind == JsonValueKind.Number)
            return je.GetDouble();
        return 0;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (left == null && right == null) return true;
        if (left == null || right == null) return false;

        if (left is JsonElement le) left = ConvertJsonElement(le);
        if (right is JsonElement re) right = ConvertJsonElement(re);

        if (left == null && right == null) return true;
        if (left == null || right == null) return false;

        if (left.GetType() == right.GetType())
        {
            return left.Equals(right);
        }

        if (IsNumeric(left) && IsNumeric(right))
        {
            return AsDouble(left) == AsDouble(right);
        }

        return string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal);
    }

    private static bool IsNumeric(object? val)
    {
        if (val == null) return false;
        return val is double || val is int || val is float || val is decimal || val is long || val is short || val is byte;
    }
}
