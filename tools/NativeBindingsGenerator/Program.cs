using System.Globalization;
using System.IO;
using System.Text;

namespace Magic.Tools.NativeBindingsGenerator;

/// <summary>
/// <c>Bindings @response.rsp</c>: reads one native library's headers for every target platform, checks that each
/// declaration reads the same on all of them, and writes the C# API. Errors are printed in MSBuild's format, so the
/// build that ran it fails naming the header line; the summary says what was made and what was skipped.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !args[0].StartsWith('@'))
        {
            Console.Error.WriteLine("Bindings : error BND000: usage: Bindings @response.rsp");
            return 1;
        }

        Options options;
        try
        {
            options = Options.Parse(File.ReadAllLines(args[0][1..]));
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"{args[0][1..]} : error BND000: {ex.Message}");
            return 1;
        }

        try
        {
            return TryGenerate(options) ? 0 : 1;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Console.Error.WriteLine($"Bindings : error BND999: {ex}");
            return 1;
        }
    }

    private static bool TryGenerate(Options options)
    {
        Dictionary<(DeclarationKind Kind, string CName), (Declaration Declaration, string Target)> merged = [];
        List<Declaration> ordered = [];
        HashSet<string> structs = new(StringComparer.Ordinal);
        Dictionary<string, Skip> skipped = new(StringComparer.Ordinal);
        HashSet<(DeclarationKind Kind, string CName)> dropped = [];
        List<string> errors = [];
        SortedDictionary<string, string> readerNames = new(StringComparer.Ordinal);

        // The platforms are read at once, each by a parser of its own, and merged in Target.All's order, so what is
        // written is what reading them one after another would write.
        Reader.MakeShims(options);
        Reader[] readers = new Reader[Target.All.Length];
        Parallel.For(0, readers.Length, i => readers[i] = Reader.Read(options, Target.All[i]));
        for (int i = 0; i < readers.Length; i++)
        {
            Target target = Target.All[i];
            Reader reader = readers[i];
            errors.AddRange(reader.Errors);
            structs.UnionWith(reader.Structs);
            foreach ((string cName, string name) in reader.NameMap)
            {
                readerNames.TryAdd(cName, name);
            }

            foreach (Skip skip in reader.Skipped)
            {
                skipped.TryAdd(skip.What, skip);
            }

            foreach (Declaration declaration in reader.Declarations)
            {
                (DeclarationKind Kind, string CName) key = (declaration.Kind, declaration.CName);
                if (dropped.Contains(key)) continue;

                if (!merged.TryGetValue(key, out (Declaration Declaration, string Target) seen))
                {
                    merged[key] = (declaration, target.Name);
                    ordered.Add(declaration);
                }
                else if (!string.Equals(seen.Declaration.Text, declaration.Text, StringComparison.Ordinal))
                {
                    if (Reconcile(seen.Declaration.Text, declaration.Text) is { } reconciledText)
                    {
                        Declaration reconciled = seen.Declaration with { Text = reconciledText };
                        merged[key] = (reconciled, seen.Target);
                        ordered[ordered.IndexOf(seen.Declaration)] = reconciled;
                        continue;
                    }

                    if (declaration.Kind == DeclarationKind.Constant)
                    {
                        merged.Remove(key);
                        ordered.Remove(seen.Declaration);
                        dropped.Add(key);
                        skipped.TryAdd($"macro {declaration.CName}", new(
                            $"macro {declaration.CName} (its value differs between {seen.Target} and {target.Name})",
                            "Bindings"));
                        continue;
                    }

                    (string first, string second) = FirstDifference(seen.Declaration.Text, declaration.Text);
                    errors.Add($"Bindings : error BND003: {declaration.CName} differs between {seen.Target} ({first}) "
                        + $"and {target.Name} ({second}); add a rule for it before it can be bound.");
                }
            }
        }

        Dictionary<(string Owner, string Name), string> names = [];
        foreach (Declaration declaration in ordered)
        {
            if (!names.TryAdd((declaration.Owner, declaration.Name), declaration.CName))
            {
                errors.Add("Bindings : error BND004: "
                    + $"{declaration.CName} and {names[(declaration.Owner, declaration.Name)]} would both "
                    + $"be {declaration.Name}.");
            }
        }

        if (errors.Count > 0)
        {
            foreach (string error in errors)
            {
                Console.Error.WriteLine(error);
            }

            return false;
        }

        WriteWhenChanged(options.Output, Writer.Write(options, ordered, structs));

        int constants = 0;
        int types = 0;
        int functions = 0;
        foreach (Declaration declaration in ordered)
        {
            if (declaration.Kind == DeclarationKind.Constant)
            {
                constants++;
            }
            else if (declaration.Kind == DeclarationKind.Type)
            {
                types++;
            }
            else
            {
                functions++;
            }
        }

        StringBuilder skips = new();
        int skippedMacros = 0;
        foreach (Skip skip in skipped.Values)
        {
            skips.Append(skip.What).Append(" (").Append(skip.Where).Append(")\n");
            if (skip.What.StartsWith("macro ", StringComparison.Ordinal))
            {
                skippedMacros++;
            }
            else
            {
                Console.WriteLine($"Bindings: {options.Library}: skipped {skip.What} ({skip.Where})");
            }
        }

        string skippedPath = Path.ChangeExtension(options.Output, ".skipped.txt");
        WriteWhenChanged(skippedPath, skips.ToString());
        StringBuilder nameMap = new();
        foreach ((string cName, string name) in readerNames)
        {
            nameMap.Append(cName).Append('\t').Append(name).Append('\n');
        }

        WriteWhenChanged(Path.ChangeExtension(options.Output, ".names.txt"), nameMap.ToString());
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Bindings: {options.Library}: {types} types, "
            + $"{functions} functions, {constants} constants -> {options.Output}; {skippedMacros} macros skipped, "
            + $"listed in {skippedPath}"));
        return true;
    }

    /// <summary>
    /// Writes the file only when its text changed, so a project that compiles it builds again only when the API did;
    /// the build's stamp, not the file, records that generation ran.
    /// </summary>
    private static void WriteWhenChanged(string path, string text)
    {
        if (File.Exists(path) && string.Equals(File.ReadAllText(path), text, StringComparison.Ordinal)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    /// <summary>
    /// The two texts as one, when they differ only where a type whose spelling is the OS's sits. On the 64-bit
    /// targets, a 64-bit integer (<c>uint64_t</c>, and <c>size_t</c> with it) is <c>unsigned long long</c> on
    /// Windows and <c>unsigned long</c> elsewhere, so <c>ulong</c> on one side facing <c>CULong</c> on the other is
    /// 64 bits everywhere and written <c>ulong</c>, as <c>long</c> facing <c>CLong</c> is written <c>long</c>. A
    /// <c>wchar_t</c> is two bytes on Windows and four elsewhere, so <c>ushort</c> facing <c>int</c> before a
    /// <c>*</c> is a <c>wchar_t</c> pointer, written <c>void*</c>: its width is the OS's, and the caller encodes for
    /// the OS it runs on. Null when they differ in any other way.
    /// </summary>
    private static string? Reconcile(string first, string second)
    {
        List<string> firstTokens = Tokenize(first);
        List<string> secondTokens = Tokenize(second);
        if (firstTokens.Count != secondTokens.Count) return null;

        StringBuilder text = new(first.Length);
        for (int i = 0; i < firstTokens.Count; i++)
        {
            string a = firstTokens[i];
            string b = secondTokens[i];
            if (string.Equals(a, b, StringComparison.Ordinal))
            {
                text.Append(a);
            }
            else if (IsPair(a, b, "ulong", "CULong", "ulong"))
            {
                text.Append("ulong");
            }
            else if (IsPair(a, b, "long", "CLong", "long"))
            {
                text.Append("long");
            }
            else if (IsPair(a, b, "ushort", "int", "void") && i + 1 < firstTokens.Count
                && firstTokens[i + 1].StartsWith('*'))
            {
                text.Append("void");
            }
            else
            {
                return null;
            }
        }

        return text.ToString();
    }

    /// <summary>Both tokens are the Windows, Unix or already reconciled spelling of one pointer-sized
    /// integer.</summary>
    private static bool IsPair(string a, string b, string windows, string unix, string reconciled)
        => IsOneOf(a, windows, unix, reconciled) && IsOneOf(b, windows, unix, reconciled);

    private static bool IsOneOf(string token, string first, string second, string third)
        => string.Equals(token, first, StringComparison.Ordinal)
            || string.Equals(token, second, StringComparison.Ordinal)
            || string.Equals(token, third, StringComparison.Ordinal);

    /// <summary>The text as runs of identifier characters and runs of everything else, so a type name is one
    /// token.</summary>
    private static List<string> Tokenize(string text)
    {
        List<string> tokens = [];
        int start = 0;
        for (int i = 1; i <= text.Length; i++)
        {
            if (i == text.Length || IsWord(text[i]) != IsWord(text[start]))
            {
                tokens.Add(text[start..i]);
                start = i;
            }
        }

        return tokens;
    }

    private static bool IsWord(char c) => char.IsAsciiLetterOrDigit(c) || c == '_';

    private static (string First, string Second) FirstDifference(string first, string second)
    {
        string[] firstLines = first.Split('\n');
        string[] secondLines = second.Split('\n');
        for (int i = 0; i < Math.Max(firstLines.Length, secondLines.Length); i++)
        {
            string a = i < firstLines.Length ? firstLines[i].Trim() : "";
            string b = i < secondLines.Length ? secondLines[i].Trim() : "";
            if (!string.Equals(a, b, StringComparison.Ordinal)) return (a, b);
        }

        return ("", "");
    }
}
