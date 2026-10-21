using System.Globalization;
using System.Text;
using CppAst;

namespace Magic.Tools.NativeBindingsGenerator;

/// <summary>
/// Turns an object-like macro into a C# constant. The macros it uses are expanded first, function-like ones with
/// their arguments and <c>##</c> pastes (<c>SDL_UINT64_C(1)</c> becomes <c>1ULL</c>), a cast to an integer type
/// becomes the C# cast, and what is left must be literals, arithmetic, and constants already made; a constant takes
/// the type of what it is made from. A string is UTF-8 bytes, as a C string is. Anything else is a reason the macro
/// is skipped, named for the log.
/// </summary>
internal sealed class Macros
{
    private static readonly HashSet<string> _operators = new(StringComparer.Ordinal) {
        "(",
        ")",
        "+",
        "-",
        "*",
        "/",
        "%",
        "<<",
        ">>",
        "|",
        "&",
        "^",
        "~" };
    private static readonly Dictionary<string, string> _keywordCasts = new(StringComparer.Ordinal)
    {
        ["int"] = "int",
        ["signed"] = "int",
        ["signed int"] = "int",
        ["unsigned"] = "uint",
        ["unsigned int"] = "uint",
        ["short"] = "short",
        ["unsigned short"] = "ushort",
        ["long long"] = "long",
        ["unsigned long long"] = "ulong",
        ["long"] = "CLong",
        ["unsigned long"] = "CULong",
        ["char"] = "byte",
        ["unsigned char"] = "byte",
        ["signed char"] = "sbyte",
        ["float"] = "float",
        ["double"] = "double",
        ["_Bool"] = "bool",
        ["bool"] = "bool",
    };

    private readonly Dictionary<string, CppMacro> _known = new(StringComparer.Ordinal);
    private readonly Func<string, string?> _typedefType;

    /// <param name="compilation">The parse, every macro of which, the compiler's and the system headers' included, can
    /// be expanded.</param>
    /// <param name="typedefType">The C# primitive a typedef name stands for, or null when it is no primitive.</param>
    internal Macros(CppCompilation compilation, Func<string, string?> typedefType)
    {
        _typedefType = typedefType;
        foreach (CppMacro macro in compilation.Macros)
        {
            _known.TryAdd(macro.Name, macro);
        }

        foreach (CppMacro macro in compilation.System.Macros)
        {
            _known.TryAdd(macro.Name, macro);
        }
    }

    /// <summary>
    /// The constant <paramref name="macro"/> makes, with <paramref name="constants"/> giving each constant already
    /// made its C# type and qualified name, or null with <paramref name="reason"/> saying why it makes none.
    /// </summary>
    internal Value? Evaluate(CppMacro macro, Func<string, (string Type, string Name)?> constants, out string reason)
    {
        reason = "";
        List<(CppTokenKind Kind, string Text)> tokens;
        try
        {
            tokens = Expand(macro.Tokens, 0);
        }
        catch (UnsupportedException ex)
        {
            reason = ex.Message;
            return null;
        }

        StringBuilder expression = new();
        List<string> strings = [];
        bool isUnsigned = false;
        bool isLong = false;
        bool isFloat = false;
        bool isDouble = false;
        string? cast = null;

        // A C cast of a negative or too-wide value is an error in C#, so each cast is written
        // unchecked((T)(operand)): the operand is the next value, parenthesis group or signed value, which these
        // count down to close.
        Stack<int> casts = new();
        for (int i = 0; i < tokens.Count; i++)
        {
            (CppTokenKind kind, string text) = tokens[i];
            if (kind == CppTokenKind.Comment) continue;

            bool value = false;
            if (kind == CppTokenKind.Punctuation && string.Equals(text, "(", StringComparison.Ordinal)
                && TryCast(tokens, i, out string? castType, out int castEnd))
            {
                cast ??= castType;
                expression.Append("unchecked((").Append(castType).Append(")(");
                casts.Push(0);
                i = castEnd;
                continue;
            }

            if (kind == CppTokenKind.Literal && text.StartsWith('"'))
            {
                strings.Add(text);
                value = true;
            }
            else if (kind == CppTokenKind.Literal && ParseNumber(text) is { } number)
            {
                isUnsigned |= number.Unsigned;
                isLong |= number.Long;
                isFloat |= number.Float;
                isDouble |= number.Double;
                expression.Append(number.Text);
                value = true;
            }
            else if (kind == CppTokenKind.Punctuation && _operators.Contains(text))
            {
                expression.Append(text);
                if (casts.Count > 0 && string.Equals(text, "(", StringComparison.Ordinal))
                {
                    casts.Push(casts.Pop() + 1);
                }
                else if (casts.Count > 0 && string.Equals(text, ")", StringComparison.Ordinal))
                {
                    int depth = casts.Pop() - 1;
                    casts.Push(depth);
                    value = depth == 0;
                }
            }
            else if ((kind == CppTokenKind.Identifier || kind == CppTokenKind.Keyword)
                && constants(text) is { } constant)
            {
                if (string.Equals(constant.Type, "string", StringComparison.Ordinal))
                {
                    strings.Add(constant.Name);
                }
                else
                {
                    isUnsigned |= constant.Type is "uint" or "ulong";
                    isLong |= constant.Type is "long" or "ulong";
                    isFloat |= string.Equals(constant.Type, "float", StringComparison.Ordinal);
                    isDouble |= string.Equals(constant.Type, "double", StringComparison.Ordinal);
                    expression.Append(constant.Name);
                }

                value = true;
            }
            else
            {
                reason = $"uses {text}";
                return null;
            }

            while (value && casts.Count > 0 && casts.Peek() == 0)
            {
                casts.Pop();
                expression.Append("))");
            }
        }

        if (casts.Count > 0)
        {
            reason = "casts nothing";
            return null;
        }

        if (strings.Count > 0)
        {
            if (expression.Length > 0 || cast is not null)
            {
                reason = "mixes text and numbers";
                return null;
            }

            // Adjacent literals are one string in C; one that is another constant stands as that constant.
            StringBuilder literal = new("\"");
            foreach (string part in strings)
            {
                if (!part.StartsWith('"'))
                {
                    if (strings.Count > 1)
                    {
                        reason = "joins text to a constant";
                        return null;
                    }

                    return new("string", part);
                }

                literal.Append(part, 1, part.Length - 2);
            }

            return new("string", literal.Append("\"u8").ToString());
        }

        if (expression.Length == 0)
        {
            reason = "has no value";
            return null;
        }

        string type = cast ?? (isDouble ? "double" : isFloat ? "float" : (isUnsigned, isLong) switch
        {
            (true, true) => "ulong",
            (false, true) => "long",
            (true, false) => "uint",
            _ => "int",
        });
        return new(type, expression.ToString());
    }

    /// <summary>The arguments of a call whose <c>(</c> is at <paramref name="open"/>, split at its commas;
    /// <paramref name="close"/> is its <c>)</c>.</summary>
    private static List<List<(CppTokenKind Kind, string Text)>> Arguments(
        List<(CppTokenKind Kind, string Text)> tokens,
        int open,
        out int close)
    {
        List<List<(CppTokenKind Kind, string Text)>> arguments = [[]];
        int depth = 0;
        for (int i = open; i < tokens.Count; i++)
        {
            string text = tokens[i].Text;
            if (string.Equals(text, "(", StringComparison.Ordinal))
            {
                depth++;
                if (depth > 1) arguments[^1].Add(tokens[i]);
            }
            else if (string.Equals(text, ")", StringComparison.Ordinal))
            {
                depth--;
                if (depth == 0)
                {
                    close = i;
                    return arguments;
                }

                arguments[^1].Add(tokens[i]);
            }
            else if (depth == 1 && string.Equals(text, ",", StringComparison.Ordinal))
            {
                arguments.Add([]);
            }
            else
            {
                arguments[^1].Add(tokens[i]);
            }
        }

        throw new UnsupportedException("has an unclosed call");
    }

    /// <summary>A function-like macro's body with its parameters replaced, <c>#</c> stringized and <c>##</c>
    /// pasted.</summary>
    private static List<(CppTokenKind Kind, string Text)> Substitute(
        CppMacro macro,
        List<string> parameters,
        List<List<(CppTokenKind Kind, string Text)>> arguments)
    {
        List<(CppTokenKind Kind, string Text)> replaced = [];
        for (int i = 0; i < macro.Tokens.Count; i++)
        {
            CppToken token = macro.Tokens[i];
            if (token.Kind == CppTokenKind.Punctuation && string.Equals(token.Text, "#", StringComparison.Ordinal)
                && i + 1 < macro.Tokens.Count
                && parameters.IndexOf(macro.Tokens[i + 1].Text) is >= 0 and int stringized)
            {
                StringBuilder text = new("\"");
                foreach ((_, string part) in arguments[stringized])
                {
                    text.Append(part);
                }

                replaced.Add((CppTokenKind.Literal, text.Append('"').ToString()));
                i++;
            }
            else if (token.Kind == CppTokenKind.Identifier && parameters.IndexOf(token.Text) is >= 0 and int index)
            {
                replaced.AddRange(arguments[index]);
            }
            else
            {
                replaced.Add((token.Kind, token.Text));
            }
        }

        List<(CppTokenKind Kind, string Text)> pasted = [];
        for (int i = 0; i < replaced.Count; i++)
        {
            if (replaced[i].Kind == CppTokenKind.Punctuation
                && string.Equals(replaced[i].Text, "##", StringComparison.Ordinal)
                && pasted.Count > 0
                && i + 1 < replaced.Count)
            {
                string text = pasted[^1].Text + replaced[i + 1].Text;
                CppTokenKind kind = char.IsAsciiDigit(text[0]) || text[0] == '"' || text[0] == '\'' || text[0] == '.'
                    ? CppTokenKind.Literal
                    : CppTokenKind.Identifier;
                pasted[^1] = (kind, text);
                i++;
            }
            else
            {
                pasted.Add(replaced[i]);
            }
        }

        return pasted;
    }

    /// <summary>A C number literal as C# text with its kind, or null for one C# has no literal for (octal, a
    /// char).</summary>
    private static (string Text, bool Unsigned, bool Long, bool Float, bool Double)? ParseNumber(string literal)
    {
        if (literal.Length == 0 || (!char.IsAsciiDigit(literal[0]) && literal[0] != '.')) return null;

        bool hex = literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        int end = literal.Length;
        while (end > 0 && char.IsAsciiLetter(literal[end - 1]) && (!hex || !char.IsAsciiHexDigit(literal[end - 1])
            || "uUlL".Contains(literal[end - 1], StringComparison.Ordinal)))
        {
            end--;
        }

        string digits = literal[..end];
        string suffix = literal[end..].ToUpperInvariant();
        bool floating = !hex && (digits.Contains('.', StringComparison.Ordinal)
            || digits.Contains('E', StringComparison.OrdinalIgnoreCase));
        if (floating)
        {
            bool isFloat = string.Equals(suffix, "F", StringComparison.Ordinal);
            return (digits + (isFloat ? "f" : ""), false, false, isFloat, !isFloat);
        }

        if (!hex && digits.Length > 1 && digits[0] == '0') return null;

        bool isUnsigned = suffix.Contains('U', StringComparison.Ordinal);
        bool isLong = suffix.Contains('L', StringComparison.Ordinal);

        // As C types a literal with no suffix by its value: past int, a hexadecimal or octal one is unsigned, a decimal
        // one is long; past that, long or unsigned long.
        bool octal = !hex && digits.Length > 1 && digits[0] == '0';
        if (ulong.TryParse(
            hex ? digits[2..] : digits,
            hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture,
            out ulong value) && value > int.MaxValue)
        {
            if ((hex || octal) && value <= uint.MaxValue)
            {
                isUnsigned = true;
            }
            else
            {
                isLong = true;
                isUnsigned |= value > long.MaxValue;
            }
        }

        return (digits + (isUnsigned ? "u" : "") + (isLong ? "L" : ""), isUnsigned, isLong, false, false);
    }

    /// <summary>The tokens with every macro among them expanded, function-like ones applied to their
    /// arguments.</summary>
    private List<(CppTokenKind Kind, string Text)> Expand(IReadOnlyList<CppToken> tokens, int depth)
    {
        List<(CppTokenKind Kind, string Text)> input = [];
        foreach (CppToken token in tokens)
        {
            input.Add((token.Kind, token.Text));
        }

        return Expand(input, depth);
    }

    private List<(CppTokenKind Kind, string Text)> Expand(List<(CppTokenKind Kind, string Text)> tokens, int depth)
    {
        if (depth > 32) throw new UnsupportedException("expands without end");

        List<(CppTokenKind Kind, string Text)> output = [];
        for (int i = 0; i < tokens.Count; i++)
        {
            (CppTokenKind kind, string text) = tokens[i];
            if (kind == CppTokenKind.Comment) continue;

            if (kind != CppTokenKind.Identifier || !_known.TryGetValue(text, out CppMacro? macro))
            {
                output.Add(tokens[i]);
                continue;
            }

            if (macro.Parameters is { Count: > 0 } parameters)
            {
                if (i + 1 >= tokens.Count || !string.Equals(tokens[i + 1].Text, "(", StringComparison.Ordinal))
                {
                    output.Add(tokens[i]);
                    continue;
                }

                List<List<(CppTokenKind Kind, string Text)>> arguments = Arguments(tokens, i + 1, out int end);
                if (arguments.Count != parameters.Count)
                {
                    throw new UnsupportedException(
                        $"calls {text} with {arguments.Count} arguments, not {parameters.Count}");
                }

                output.AddRange(Expand(Substitute(macro, parameters, arguments), depth + 1));
                i = end;
            }
            else
            {
                output.AddRange(Expand(macro.Tokens, depth + 1));
            }
        }

        return output;
    }

    /// <summary>Whether the <c>(</c> at <paramref name="open"/> starts a cast to a primitive, and to which C#
    /// type.</summary>
    private bool TryCast(List<(CppTokenKind Kind, string Text)> tokens, int open, out string type, out int close)
    {
        type = "";
        close = open;
        StringBuilder words = new();
        for (int i = open + 1; i < tokens.Count; i++)
        {
            (CppTokenKind kind, string text) = tokens[i];
            if (string.Equals(text, ")", StringComparison.Ordinal))
            {
                if (words.Length == 0 || i + 1 >= tokens.Count) return false;

                // A cast is followed by what it casts: a value, a parenthesis or a sign, never a binary operator.
                string next = tokens[i + 1].Text;
                bool operand = next is "(" or "-" or "~" || !_operators.Contains(next);
                if (!operand) return false;

                string spelled = words.ToString();
                string? mapped = _keywordCasts.TryGetValue(spelled, out string? keyword)
                    ? keyword
                    : _typedefType(spelled);
                if (mapped is null) return false;

                // A constant has no pointer-sized type in C#: a size_t cast makes a 64-bit one.
                type = mapped switch
                {
                    "nuint" => "ulong",
                    "nint" => "long",
                    _ => mapped,
                };
                close = i;
                return true;
            }

            if (kind is not (CppTokenKind.Identifier or CppTokenKind.Keyword)) return false;

            if (words.Length > 0) words.Append(' ');

            words.Append(text);
        }

        return false;
    }

    /// <summary>A constant's C# type and expression.</summary>
    internal sealed record Value(string Type, string Expression);
}
