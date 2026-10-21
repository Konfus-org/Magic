using System.Text;

namespace Magic.Tools.NativeBindingsGenerator;

/// <summary>
/// How a C name becomes a C# one: its owner (the prefix before the first underscore, or a configured one) becomes the
/// class or struct it is declared in, and the rest is PascalCase: SCREAMING_SNAKE words are title-cased
/// (<c>INIT_VIDEO</c> to <c>InitVideo</c>), a camelCase name gets a capital (<c>contextCreate</c> to
/// <c>ContextCreate</c>), and a name without underscores otherwise keeps its capitals (<c>CreateGPUDevice</c>,
/// <c>HRTF</c>). A pure function of the name and the prefixes, so what a C symbol is called in C# can be worked out
/// by eye.
/// </summary>
internal static class Names
{
    private static readonly HashSet<string> _keywords = new(StringComparer.Ordinal)
    {
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class", "const",
        "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event", "explicit", "extern",
        "false", "finally", "fixed", "float", "for", "foreach", "goto", "if", "implicit", "in", "int", "interface",
        "internal", "is", "lock", "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof",
        "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
        "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
    };

    /// <summary>
    /// The owner a C name belongs to and the rest of the name. <c>SDL_CreateWindow</c> is <c>SDL</c> and
    /// <c>CreateWindow</c>; with <c>ipl=IPL</c> configured, <c>iplContextCreate</c> is <c>IPL</c> and
    /// <c>ContextCreate</c>. A name with neither has no owner and is declared at the namespace.
    /// </summary>
    internal static (string Owner, string Name) Split(
        string name,
        IReadOnlyList<(string Prefix, string Owner)> prefixes)
    {
        foreach ((string prefix, string owner) in prefixes)
        {
            if (name.StartsWith(prefix, StringComparison.Ordinal))
            {
                string rest = name[prefix.Length..].TrimStart('_');
                if (rest.Length > 0) return (owner, rest);
            }
        }

        int underscore = name.IndexOf('_', StringComparison.Ordinal);
        if (underscore > 0 && underscore < name.Length - 1) return (name[..underscore], name[(underscore + 1)..]);

        return ("", name);
    }

    /// <summary>
    /// The PascalCase form of a C name part, a C# member or type name. A snake_case or SCREAMING_SNAKE name is
    /// title-cased word by word (<c>NUM_BANDS</c> to <c>NumBands</c>); a name without underscores keeps its capitals
    /// and gets a first one (<c>error</c> to <c>Error</c>; <c>HRTFSettings</c> and <c>HRTF</c> stay).
    /// </summary>
    internal static string ToPascal(string name)
    {
        name = name.Trim('_');
        if (name.Length == 0) return "_";

        string text = name.Contains('_', StringComparison.Ordinal)
            ? ToTitle(name)
            : char.ToUpperInvariant(name[0]) + name[1..];
        return ToLegal(text);
    }

    /// <summary>The camelCase form of a C name, a C# parameter name.</summary>
    internal static string ToCamel(string name)
    {
        string pascal = ToPascal(name).TrimEnd('_');
        string text = char.ToLowerInvariant(pascal[0]) + pascal[1..];
        return _keywords.Contains(text) ? "@" + text : text;
    }

    internal static string[] MakeEnumMembers(
        string enumName,
        IReadOnlyList<string> names,
        IReadOnlyList<(string Prefix, string Owner)> prefixes)
    {
        // Members named after the enum lose its name: ImGuiKey_A, or SpvDecorationBinding where the next letter
        // starts a word.
        string own = enumName.TrimEnd('_') + "_";
        string[] result = new string[names.Count];
        int owned = CountOwned(names, own);
        if (owned == 0)
        {
            own = enumName.TrimEnd('_');
            owned = CountOwned(names, own);
        }

        if (owned > 0)
        {
            for (int i = 0; i < names.Count; i++)
            {
                result[i] = MakeMember(enumName, IsOwned(names[i], own) ? names[i][own.Length..] : names[i]);
            }

            return result;
        }

        string[][] pieces = new string[names.Count][];
        for (int i = 0; i < names.Count; i++)
        {
            pieces[i] = names[i].Split('_', StringSplitOptions.RemoveEmptyEntries);
        }

        int shared;
        if (names.Count == 1)
        {
            (_, string rest) = Split(names[0], prefixes);
            shared = pieces[0].Length - rest.Split('_', StringSplitOptions.RemoveEmptyEntries).Length;
        }
        else
        {
            shared = pieces[0].Length;
            for (int i = 1; i < pieces.Length; i++)
            {
                int common = 0;
                while (common < shared && common < pieces[i].Length
                    && string.Equals(pieces[0][common], pieces[i][common], StringComparison.Ordinal))
                {
                    common++;
                }

                shared = common;
            }

            foreach (string[] member in pieces)
            {
                if (member.Length <= shared) shared = member.Length - 1;
            }
        }

        for (int i = 0; i < names.Count; i++)
        {
            result[i] = MakeMember(enumName, string.Join('_', pieces[i], shared, pieces[i].Length - shared));
        }

        return result;
    }

    /// <summary>
    /// An enum's members without their prefix, title-cased word by word, as they are SCREAMING_SNAKE in C. A member
    /// that starts with the enum's own name loses it (<c>ImGuiWindowFlags_NoTitleBar</c> to <c>NoTitleBar</c>; in an
    /// enum where some do not, as <c>ImGuiKey</c> holds <c>ImGuiMod_Ctrl</c>, those keep their name). Otherwise the
    /// members lose the prefix they all share (<c>IPL_STATUS_SUCCESS</c>, <c>IPL_STATUS_FAILURE</c> to
    /// <c>Success</c>, <c>Failure</c>), and a lone member only its owner's. A member that would start with a digit
    /// is led by the enum's last word (<c>ImGuiKey_0</c> to <c>Key0</c>).
    /// </summary>
    private static int CountOwned(IReadOnlyList<string> names, string own)
    {
        int owned = 0;
        foreach (string name in names)
        {
            if (IsOwned(name, own)) owned++;
        }

        return owned;
    }

    private static bool IsOwned(string name, string own)
        => name.StartsWith(own, StringComparison.Ordinal) && name.Length > own.Length
            && (own.EndsWith('_') || char.IsUpper(name[own.Length]) || char.IsDigit(name[own.Length]));

    /// <summary>A member's name from what is left of it: title-cased, led by the enum's last word when it would start
    /// with a digit.</summary>
    private static string MakeMember(string enumName, string rest)
    {
        if (rest.Length == 0 || char.IsAsciiDigit(rest[0])) rest = GetLastWord(enumName) + "_" + rest;

        return ToLegal(ToTitle(rest));
    }

    /// <summary>The last word of a name: of <c>ImGuiKey</c>, <c>Key</c>; of <c>SDL_EventType</c>,
    /// <c>Type</c>.</summary>
    private static string GetLastWord(string name)
    {
        name = name.Trim('_');
        int underscore = name.LastIndexOf('_');
        if (underscore >= 0) name = name[(underscore + 1)..];

        for (int i = name.Length - 1; i > 0; i--)
        {
            if (char.IsAsciiLetterUpper(name[i]) && !char.IsAsciiLetterUpper(name[i - 1])) return name[i..];
        }

        return name;
    }

    /// <summary>Each underscore-separated word title-cased.</summary>
    private static string ToTitle(string name)
    {
        StringBuilder result = new(name.Length);
        foreach (string piece in name.Split('_', StringSplitOptions.RemoveEmptyEntries))
        {
            result.Append(ToWord(piece));
        }

        return result.Length == 0 ? "_" : result.ToString();
    }

    /// <summary>A name C# accepts as an identifier: not starting with a digit, not a keyword.</summary>
    private static string ToLegal(string text)
    {
        if (char.IsAsciiDigit(text[0])) text = "_" + text;

        return _keywords.Contains(text) ? text + "_" : text;
    }

    private static string ToWord(string piece)
    {
        bool lower = false;
        bool upper = false;
        foreach (char c in piece)
        {
            lower |= char.IsAsciiLetterLower(c);
            upper |= char.IsAsciiLetterUpper(c);
        }

        if (lower && upper) return char.ToUpperInvariant(piece[0]) + piece[1..];

        return char.ToUpperInvariant(piece[0]) + piece[1..].ToLowerInvariant();
    }
}
