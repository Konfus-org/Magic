using System.IO;

namespace Magic.Tools.NativeBindingsGenerator;

/// <summary>
/// What one run generates: a NativeLibrary item's metadata, as Directory.Build.targets writes it to the response file
/// (<c>--key=value</c> per line, lists comma-separated).
/// </summary>
internal sealed class Options
{
    /// <summary>The library's logical name: what the generated <c>DllImport</c>s name.</summary>
    public string Library { get; private set; } = "";

    /// <summary>The namespace the API is declared in.</summary>
    public string Namespace { get; private set; } = "";

    /// <summary>The extracted archive; the headers and include paths are relative to it, and only declarations from under it are generated.</summary>
    public string Root { get; private set; } = "";

    public IReadOnlyList<string> Headers { get; private set; } = [];

    public IReadOnlyList<string> IncludePaths { get; private set; } = [];

    /// <summary>Include folders of other libraries' archives, absolute: headers this library's include but which are not its own.</summary>
    public IReadOnlyList<string> AbsoluteIncludePaths { get; private set; } = [];

    /// <summary>
    /// Headers to make from templates in the archive, as the library's build would (<c>assimp/config.h</c> from
    /// <c>include/assimp/config.h.in</c>), each the header's include path and the template's path under the root.
    /// </summary>
    public IReadOnlyList<(string Header, string Template)> Shims { get; private set; } = [];

    public IReadOnlyList<string> Defines { get; private set; } = [];

    /// <summary>
    /// C prefixes without an underscore and the C# owner each names (<c>ipl=IPL</c>), longest first. A prefix that
    /// ends in an underscore needs no entry: the text before the first underscore is the owner.
    /// </summary>
    public IReadOnlyList<(string Prefix, string Owner)> Prefixes { get; private set; } = [];

    /// <summary>The class a name with no owner is declared in; the library's name unless the item says.</summary>
    public string Class { get; private set; } = "";

    /// <summary>The standard headers the parse sees instead of the machine's, so every machine parses the same.</summary>
    public string Sysroot { get; private set; } = "";

    public string Output { get; private set; } = "";

    public static Options Parse(IEnumerable<string> lines)
    {
        Options options = new();
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || !line.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            int equals = line.IndexOf('=', StringComparison.Ordinal);
            string key = equals < 0 ? line[2..] : line[2..equals];
            string value = equals < 0 ? "" : line[(equals + 1)..];
            switch (key)
            {
                case "library":
                    options.Library = value;
                    break;
                case "namespace":
                    options.Namespace = value;
                    break;
                case "root":
                    options.Root = Path.GetFullPath(value);
                    break;
                case "headers":
                    options.Headers = List(value);
                    break;
                case "include":
                    options.IncludePaths = List(value);
                    break;
                case "include-absolute":
                    options.AbsoluteIncludePaths = List(value);
                    break;
                case "class":
                    options.Class = value;
                    break;
                case "shims":
                    options.Shims = PairList(value, "header=template");
                    break;
                case "defines":
                    options.Defines = List(value);
                    break;
                case "prefixes":
                    options.Prefixes = PrefixList(value);
                    break;
                case "sysroot":
                    options.Sysroot = Path.GetFullPath(value);
                    break;
                case "output":
                    options.Output = Path.GetFullPath(value);
                    break;
                default:
                    throw new ArgumentException($"Unknown option --{key}.", nameof(lines));
            }
        }

        if (options.Library.Length == 0 || options.Namespace.Length == 0 || options.Root.Length == 0 || options.Headers.Count == 0 || options.Output.Length == 0 || options.Sysroot.Length == 0)
        {
            throw new ArgumentException("--library, --namespace, --root, --headers, --sysroot and --output are required.", nameof(lines));
        }

        if (options.Class.Length == 0)
        {
            options.Class = Names.Pascal(options.Library);
        }

        return options;
    }

    private static List<string> List(string value)
    {
        List<string> items = [];
        foreach (string item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            items.Add(item);
        }

        return items;
    }

    private static List<(string Prefix, string Owner)> PrefixList(string value)
    {
        List<(string Prefix, string Owner)> prefixes = PairList(value, "cPrefix=Owner");
        prefixes.Sort((a, b) => b.Prefix.Length.CompareTo(a.Prefix.Length));
        return prefixes;
    }

    private static List<(string, string)> PairList(string value, string shape)
    {
        List<(string, string)> pairs = [];
        foreach (string item in List(value))
        {
            int equals = item.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0 || equals == item.Length - 1)
            {
                throw new ArgumentException($"An entry is written {shape}, not '{item}'.", nameof(value));
            }

            pairs.Add((item[..equals], item[(equals + 1)..]));
        }

        return pairs;
    }
}
