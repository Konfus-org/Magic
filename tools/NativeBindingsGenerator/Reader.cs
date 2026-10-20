using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using CppAst;

namespace Magic.Tools.NativeBindingsGenerator;

/// <summary>
/// Reads the headers for one platform and turns each C declaration into C# text. Anything it cannot turn into C#
/// faithfully is an error that names the construct and where it is, never a silent gap; inline and variadic functions,
/// which have no export to call, and function-like macros are skipped and listed.
/// </summary>
internal sealed class Reader
{
    private static readonly HashSet<string> _unsignedSizeTypedefs = new(StringComparer.Ordinal) { "size_t", "uintptr_t", "uintmax_t", "rsize_t" };
    private static readonly HashSet<string> _signedSizeTypedefs = new(StringComparer.Ordinal) { "ptrdiff_t", "intptr_t", "intmax_t", "ssize_t" };
    private static readonly HashSet<string> _fixedBufferTypes = new(StringComparer.Ordinal) { "bool", "byte", "sbyte", "short", "ushort", "int", "uint", "long", "ulong", "char", "float", "double" };

    private readonly Options _options;
    private readonly Target _target;
    private readonly CppCompilation _compilation;
    private readonly Macros _macros;
    private readonly List<Declaration> _declarations = [];
    private readonly List<Skip> _skipped = [];
    private readonly List<string> _errors = [];
    private readonly Dictionary<string, string> _constants = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Type, string Expression)> _constantParts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _structs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CppTypedef> _typedefs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _flagEnums = new(StringComparer.Ordinal);
    private readonly List<(string CName, string Name)> _names = [];
    private readonly Dictionary<CppClass, CppTypedef> _opaqueNames = [];
    private readonly Dictionary<CppClass, string> _classNames = [];
    private readonly Dictionary<CppEnum, string> _enumNames = [];

    private Reader(Options options, Target target)
    {
        _options = options;
        _target = target;
        _compilation = Parse();
        _macros = new(_compilation, TypedefType);
    }

    /// <summary>The C names of the structs the library defines: what a function can attach to.</summary>
    public IReadOnlySet<string> Structs => _structs;

    /// <summary>Every C name read and the qualified C# name it became, enum members included.</summary>
    public IReadOnlyList<(string CName, string Name)> NameMap => _names;

    public IReadOnlyList<Declaration> Declarations => _declarations;

    public IReadOnlyList<Skip> Skipped => _skipped;

    public IReadOnlyList<string> Errors => _errors;

    public static Reader Read(Options options, Target target)
    {
        Reader reader = new(options, target);
        reader.Read();
        return reader;
    }

    /// <summary>
    /// Makes the headers the library's build would generate, from their templates, as CMake would with every option
    /// off: a <c>#cmakedefine NAME</c> line becomes an undefined NAME, a <c>#cmakedefine01 NAME</c> a NAME of 0, and
    /// the rest is copied, into <see cref="ShimFolder"/>.
    /// </summary>
    private void Shims()
    {
        string folder = ShimFolder;
        foreach ((string header, string template) in _options.Shims)
        {
            string path = Path.Combine(folder, header);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? folder);
            StringBuilder made = new();
            foreach (string line in File.ReadAllLines(Path.Combine(_options.Root, template)))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("#cmakedefine01 ", StringComparison.Ordinal))
                {
                    made.Append("#define ").Append(trimmed["#cmakedefine01 ".Length..].Trim()).Append(" 0\n");
                }
                else if (trimmed.StartsWith("#cmakedefine ", StringComparison.Ordinal))
                {
                    made.Append("/* #undef ").Append(trimmed["#cmakedefine ".Length..].Trim().Split(' ')[0]).Append(" */\n");
                }
                else
                {
                    made.Append(line).Append('\n');
                }
            }

            File.WriteAllText(path, made.ToString(), new UTF8Encoding(false));
        }
    }

    /// <summary>Where the shims are made: beside the output, so a clean build remakes them.</summary>
    private string ShimFolder => Path.ChangeExtension(_options.Output, ".shims");

    /// <summary>The headers read for the target, with the generator's sysroot in place of the machine's.</summary>
    private CppCompilation Parse()
    {
        CppParserOptions parserOptions = new()
        {
            ParserKind = CppParserKind.C,
            ParseMacros = true,
            ParseComments = true,
            ParseSystemIncludes = true,
            AutoSquashTypedef = false,
            TargetCpu = _target.Cpu,
            TargetSystem = _target.System,
            TargetVendor = _target.Vendor,
            TargetAbi = _target.Abi,
        };
        parserOptions.AdditionalArguments.Add("-nostdinc");
        parserOptions.SystemIncludeFolders.Add(_options.Sysroot);
        foreach (string include in _options.IncludePaths)
        {
            parserOptions.IncludeFolders.Add(Path.Combine(_options.Root, include));
        }

        foreach (string include in _options.AbsoluteIncludePaths)
        {
            parserOptions.IncludeFolders.Add(include);
        }

        if (_options.Shims.Count > 0)
        {
            Shims();
            parserOptions.IncludeFolders.Add(ShimFolder);
        }

        foreach (string define in _options.Defines)
        {
            parserOptions.Defines.Add(define);
        }

        // A root header is in the archive, or is one the shims made (assimp/config.h), named by its include path.
        List<string> headers = [];
        foreach (string header in _options.Headers)
        {
            string shim = Path.Combine(ShimFolder, header);
            headers.Add(!File.Exists(Path.Combine(_options.Root, header)) && File.Exists(shim) ? shim : Path.Combine(_options.Root, header));
        }

        return CppParser.ParseFiles(headers, parserOptions);
    }

    private void Read()
    {
        CppCompilation compilation = _compilation;
        if (compilation.HasErrors)
        {
            foreach (CppDiagnosticMessage message in compilation.Diagnostics.Messages)
            {
                if (message.Type == CppLogMessageType.Error)
                {
                    _errors.Add($"{message.Location.File}({message.Location.Line.ToString(CultureInfo.InvariantCulture)}): error BND001: [{_target.Name}] {message.Text}");
                }
            }

            return;
        }

        foreach (CppTypedef typedef in compilation.System.Typedefs)
        {
            _typedefs.TryAdd(typedef.Name, typedef);
        }

        foreach (CppTypedef typedef in compilation.Typedefs)
        {
            _typedefs.TryAdd(typedef.Name, typedef);
        }

        // Dear ImGui's idiom, and Vulkan's: a typedef to an integer named X beside an enum named X_ (or XBits) that
        // holds its values, so a parameter typed X takes the enum.
        foreach (CppEnum cppEnum in compilation.Enums)
        {
            string? enumName = NameOf(cppEnum);
            if (enumName is not null && IsOurs(cppEnum) && enumName.EndsWith('_') && _typedefs.TryGetValue(enumName[..^1], out CppTypedef? flags) && Unqualified(flags.ElementType) is CppPrimitiveType)
            {
                _flagEnums[flags.Name] = enumName;
            }
        }

        foreach (CppTypedef typedef in compilation.Typedefs)
        {
            if (IsOurs(typedef))
            {
                Attempt(typedef, () => Alias(typedef));
            }

            CppType element = Unqualified(typedef.ElementType);
            if (element is CppPointerType pointer && Unqualified(pointer.ElementType) is CppClass { IsDefinition: false } opaque)
            {
                _opaqueNames.TryAdd(opaque, typedef);
            }
            else if (element is CppClass anonymousClass && !IsNamed(anonymousClass.Name))
            {
                _classNames.TryAdd(anonymousClass, typedef.Name);
            }
            else if (element is CppClass named && named.Name.StartsWith(typedef.Name + "_", StringComparison.Ordinal) && named.Name.Length > typedef.Name.Length + 1)
            {
                // A struct the header names with a suffix and gives its plain name by typedef (ImVec2 for ImVec2_c)
                // goes by the plain name, which is what the header's functions are named after.
                _classNames.TryAdd(named, typedef.Name);
            }
            else if (element is CppEnum anonymousEnum && !IsNamed(anonymousEnum.Name))
            {
                _enumNames.TryAdd(anonymousEnum, typedef.Name);
            }
        }

        foreach (CppClass cppClass in compilation.Classes)
        {
            if (cppClass.IsDefinition && IsOurs(cppClass) && NameOf(cppClass) is { } name)
            {
                _structs.Add(name);
            }
        }

        foreach (CppMacro macro in compilation.Macros)
        {
            if (IsOurs(macro))
            {
                Constant(macro);
            }
        }

        foreach (CppEnum cppEnum in compilation.Enums)
        {
            if (IsOurs(cppEnum))
            {
                Attempt(cppEnum, () => Enum(cppEnum));
            }
        }

        HashSet<string> declared = new(StringComparer.Ordinal);
        foreach (CppClass cppClass in compilation.Classes)
        {
            if (IsOurs(cppClass) && NameOf(cppClass) is { } name && declared.Add(name))
            {
                Attempt(cppClass, () => Struct(cppClass, name));
            }
        }

        foreach ((CppClass opaque, CppTypedef handle) in _opaqueNames)
        {
            if ((IsOurs(opaque) || IsOurs(handle)) && !declared.Contains(opaque.Name) && declared.Add(opaque.Name))
            {
                Attempt(opaque, () => Opaque(handle.Name, opaque));
            }
        }

        foreach (CppFunction function in compilation.Functions)
        {
            if (IsOurs(function))
            {
                Attempt(function, () => Function(function));
            }
        }

        ResolveClashes();
    }

    private void Attempt(CppElement element, Action emit)
    {
        try
        {
            emit();
        }
        catch (UnsupportedException ex)
        {
            _errors.Add($"{Where(element)}: error BND002: [{_target.Name}] {ex.Message}");
        }
    }

    /// <summary>Whether the element is declared by the library: in its archive, or in a header the shims made.</summary>
    private bool IsOurs(CppElement element)
    {
        string? file = element.Span.Start.File;
        if (string.IsNullOrEmpty(file))
        {
            return false;
        }

        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string full = Path.GetFullPath(file);
        return full.StartsWith(_options.Root, comparison) || full.StartsWith(ShimFolder, comparison);
    }

    private static string Where(CppElement element)
        => element.Span.Start.File is { Length: > 0 } file ? $"{file}({element.Span.Start.Line.ToString(CultureInfo.InvariantCulture)})" : "Bindings";

    private void Constant(CppMacro macro)
    {
        if (macro.Parameters is { Count: > 0 } || macro.Tokens.Count == 0 || macro.Name.StartsWith('_'))
        {
            return;
        }

        Macros.Value? value = _macros.Evaluate(macro, Constant, out string reason);
        if (value is null)
        {
            _skipped.Add(new($"macro {macro.Name} ({reason})", Where(macro)));
            return;
        }

        (string owner, string rest) = Names.Split(macro.Name, _options.Prefixes);
        string name = Names.Pascal(rest);
        _constants[macro.Name] = Qualified(macro.Name);
        _constantParts[macro.Name] = (value.Type, value.Expression);
        _declarations.Add(new(DeclarationKind.Constant, macro.Name, owner, false, name, ConstantText(value.Type, name, value.Expression)));
        _names.Add((macro.Name, Qualified(macro.Name)));
    }

    /// <summary>
    /// A typedef that names no type of its own in C# (<c>SDL_WindowFlags</c> is a <c>ulong</c>) goes in the name map
    /// as what it stands for, so code written against the C name can find the C# one.
    /// </summary>
    private void Alias(CppTypedef typedef)
    {
        if (_flagEnums.TryGetValue(typedef.Name, out string? flagEnum))
        {
            _names.Add((typedef.Name, Qualified(flagEnum)));
            return;
        }

        CppType element = Unqualified(typedef.ElementType);
        if (element is CppClass or CppEnum || (element is CppPointerType pointer && Unqualified(pointer.ElementType) is CppClass { IsDefinition: false }))
        {
            return;
        }

        try
        {
            _names.Add((typedef.Name, FunctionTypeOf(element) is { } functionType ? FunctionPointer(functionType) : Type(typedef, true)));
        }
        catch (UnsupportedException)
        {
        }
    }

    /// <summary>A constant already made from a macro, as its C# type and qualified name.</summary>
    private (string Type, string Name)? Constant(string cName)
        => _constants.TryGetValue(cName, out string? name) ? (_constantParts[cName].Type, name) : null;

    /// <summary>The C# primitive a typedef stands for, for a cast in a macro, or null when it is no primitive.</summary>
    private string? TypedefType(string name)
    {
        if (!_typedefs.TryGetValue(name, out CppTypedef? typedef))
        {
            return null;
        }

        try
        {
            string type = Type(typedef, false);
            return type.Contains('*', StringComparison.Ordinal) || type.Contains('.', StringComparison.Ordinal) ? null : type;
        }
        catch (UnsupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// A constant's declaration. A string is a <c>byte*</c> to the UTF-8 literal's bytes, which live in the assembly's
    /// static data and never move, so it is handed to C as a C string constant is, with no copy and no pinning.
    /// </summary>
    private static string ConstantText(string type, string name, string expression)
    {
        if (!string.Equals(type, "string", StringComparison.Ordinal))
        {
            return $"public const {type} {name} = {expression};\n";
        }

        return expression.StartsWith('"')
            ? $"public static byte* {name} => (byte*)Unsafe.AsPointer(ref MemoryMarshal.GetReference({expression}));\n"
            : $"public static byte* {name} => {expression};\n";
    }

    /// <summary>
    /// A constant that would be named like a type of the same owner (<c>SDL_HAPTIC_RAMP</c> beside
    /// <c>SDL_HapticRamp</c>) keeps its C spelling after the prefix instead, so both stand.
    /// </summary>
    private void ResolveClashes()
    {
        HashSet<(string Owner, string Name)> types = [];
        foreach (Declaration declaration in _declarations)
        {
            if (declaration.Kind == DeclarationKind.Type)
            {
                types.Add((declaration.Owner, declaration.Name));
            }
        }

        for (int i = 0; i < _declarations.Count; i++)
        {
            Declaration declaration = _declarations[i];
            if (declaration.Kind == DeclarationKind.Constant && types.Contains((declaration.Owner, declaration.Name)))
            {
                Rename(i, declaration.Owner, Names.Split(declaration.CName, _options.Prefixes).Name);
            }
            else if (declaration.Kind == DeclarationKind.Constant && declaration.Owner.Length > 0 && !_structs.Contains(declaration.Owner) && types.Contains(("", Names.Pascal(declaration.Owner))))
            {
                // A constant whose owner is a type with no members of its own to join (ImGuiKey_LegacyNativeKey_BEGIN
                // beside the enum ImGuiKey) would need a class of that name beside the type: it keeps its whole name in
                // the library's class instead.
                Rename(i, "", Names.Pascal(declaration.CName));
            }
        }
    }

    /// <summary>Gives the constant at <paramref name="index"/> another owner and name, in all the places it is recorded.</summary>
    private void Rename(int index, string owner, string name)
    {
        Declaration declaration = _declarations[index];
        (string type, string expression) = _constantParts[declaration.CName];
        _declarations[index] = declaration with { Owner = owner, Name = name, Text = ConstantText(type, name, expression) };
        _constants[declaration.CName] = owner.Length == 0 ? name : Container(owner) + "." + name;
        int recorded = _names.FindIndex(pair => string.Equals(pair.CName, declaration.CName, StringComparison.Ordinal));
        _names[recorded] = (declaration.CName, _constants[declaration.CName]);
    }

    private void Enum(CppEnum cppEnum)
    {
        string cName = NameOf(cppEnum) ?? throw new UnsupportedException("an enum with neither a name nor a typedef");
        (string owner, string rest) = Names.Split(cName, _options.Prefixes);
        string name = Names.Pascal(rest);
        string underlying = Underlying(cppEnum);
        List<string> memberNames = [];
        foreach (CppEnumItem item in cppEnum.Items)
        {
            memberNames.Add(item.Name);
        }

        string[] members = Names.EnumMembers(cName, memberNames, _options.Prefixes);
        HashSet<string> distinct = new(StringComparer.Ordinal);
        for (int i = 0; i < members.Length; i++)
        {
            if (!distinct.Add(members[i]))
            {
                throw new UnsupportedException($"enum {cName}: {memberNames[i]} becomes {members[i]}, which another member already is");
            }
        }

        bool flags = IsFlags(cppEnum);
        StringBuilder text = new();
        text.Append(Summary(cppEnum.Comment));
        if (flags)
        {
            text.Append("[Flags]\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"public enum {name} : {underlying}\n{{\n");
        for (int i = 0; i < members.Length; i++)
        {
            CppEnumItem item = cppEnum.Items[i];
            string value = flags && item.Value > 0 ? "0x" + item.Value.ToString("X", CultureInfo.InvariantCulture) : item.Value.ToString(CultureInfo.InvariantCulture);
            text.Append(Indent(Summary(item.Comment)));
            text.Append(CultureInfo.InvariantCulture, $"    {members[i]} = {value},\n");
        }

        text.Append("}\n");
        _declarations.Add(new(DeclarationKind.Type, cName, owner, false, name, text.ToString()));
        _names.Add((cName, Qualified(cName)));
        for (int i = 0; i < members.Length; i++)
        {
            _names.Add((memberNames[i], Qualified(cName) + "." + members[i]));
        }
    }

    /// <summary>
    /// A C enum is an int unless its values need more; which the C compiler picks for one that fits differs by ABI
    /// (unsigned where nothing is negative on GNU), so it is decided here by the values alone.
    /// </summary>
    private static string Underlying(CppEnum cppEnum)
    {
        long min = 0;
        long max = 0;
        foreach (CppEnumItem item in cppEnum.Items)
        {
            min = Math.Min(min, item.Value);
            max = Math.Max(max, item.Value);
        }

        if (min >= int.MinValue && max <= int.MaxValue)
        {
            return "int";
        }

        return min >= 0 && max <= uint.MaxValue ? "uint" : "long";
    }

    private static bool IsFlags(CppEnum cppEnum)
    {
        long bits = 0;
        int flagMembers = 0;
        foreach (CppEnumItem item in cppEnum.Items)
        {
            if (item.Value > 0 && (item.Value & (item.Value - 1)) == 0)
            {
                bits |= item.Value;
                flagMembers++;
            }
        }

        if (flagMembers < 2)
        {
            return false;
        }

        foreach (CppEnumItem item in cppEnum.Items)
        {
            if (item.Value < 0 || (item.Value & ~bits) != 0)
            {
                return false;
            }
        }

        return true;
    }

    private void Struct(CppClass cppClass, string cName)
    {
        if (!cppClass.IsDefinition)
        {
            Opaque(_opaqueNames.TryGetValue(cppClass, out CppTypedef? handle) ? handle.Name : cName, cppClass);
            return;
        }

        (string owner, string rest) = Names.Split(cName, _options.Prefixes);
        string name = Names.Pascal(rest);
        string text;
        try
        {
            text = StructText(cppClass, cName, name);
        }
        catch (LayoutException ex)
        {
            _skipped.Add(new($"the layout of {cName} ({ex.Message}; it is opaque)", Where(cppClass)));
            text = $"{Summary(cppClass.Comment)}/// <remarks>Opaque: its layout differs between compilers ({ex.Message}), so it is only ever used through a pointer.</remarks>\npublic partial struct {name}\n{{\n}}\n";
        }

        _declarations.Add(new(DeclarationKind.Type, cName, owner, false, name, Unshadowed(text, owner, name)));
        _names.Add((cName, Qualified(cName)));
        if (cppClass.Name.Length > 0 && !string.Equals(cppClass.Name, cName, StringComparison.Ordinal))
        {
            _names.Add((cppClass.Name, Qualified(cName)));
        }
    }

    /// <summary>
    /// Inside a struct nested in its owner (<c>ImVector.ImDrawCmd</c>), its own name hides a top-level type of that name
    /// (<c>ImDrawCmd</c>, which its <c>Data</c> points at), so those references are spelled out from the namespace, in
    /// the code lines after the declaration's own. A reference to the nested struct itself is already written with its
    /// owner.
    /// </summary>
    private string Unshadowed(string text, string owner, string name)
    {
        if (owner.Length == 0)
        {
            return text;
        }

        string declaration = $"public partial struct {name}";
        Regex reference = new($@"(?<![\w.]){Regex.Escape(name)}(?![\w])", RegexOptions.None, TimeSpan.FromSeconds(10));
        StringBuilder result = new();
        bool declared = false;
        foreach (string line in text.Split('\n'))
        {
            bool code = declared && !line.TrimStart().StartsWith("///", StringComparison.Ordinal);
            result.Append(code ? reference.Replace(line, $"global::{_options.Namespace}.{name}") : line).Append('\n');
            declared |= line.Contains(declaration, StringComparison.Ordinal);
        }

        result.Length--;
        return result.ToString();
    }

    /// <summary>
    /// A struct or union as C# text. A field whose type is an anonymous struct or union gets that type declared
    /// beside it, as a nested struct named after the field (<c>input</c> becomes <c>InputUnion</c>).
    /// </summary>
    private string StructText(CppClass cppClass, string cName, string name)
    {
        if (cppClass.ClassKind == CppClassKind.Class)
        {
            throw new UnsupportedException($"{cName} is a C++ class");
        }

        bool union = cppClass.ClassKind == CppClassKind.Union;
        StringBuilder text = new();
        text.Append(Summary(cppClass.Comment));

        // A struct packed tighter than its fields align (#pragma pack) says so, or its fields would drift.
        string pack = cppClass.AlignOf is 1 or 2 && Alignment(cppClass) > cppClass.AlignOf ? $", Pack = {cppClass.AlignOf.ToString(CultureInfo.InvariantCulture)}" : "";
        text.Append(union ? $"[StructLayout(LayoutKind.Explicit{pack})]\n" : $"[StructLayout(LayoutKind.Sequential{pack})]\n");
        text.Append(CultureInfo.InvariantCulture, $"public partial struct {name}\n{{\n");
        HashSet<string> fieldNames = new(StringComparer.Ordinal);
        bool first = true;
        (string Type, int Offset, string Name)? bits = null;
        int bitRuns = 0;
        foreach (CppField field in cppClass.Fields)
        {
            if (field.IsBitField)
            {
                string bitType = Type(Unqualified(field.Type), false);
                string storage = Storage(bitType, Unqualified(field.Type));
                if (bits is not null && Size(bits.Value.Type) != Size(storage))
                {
                    // MSVC starts a new unit when a bit field's type changes size; GCC packs on. No one layout serves.
                    throw new LayoutException("bit fields of different sizes in one run");
                }

                if (bits is null)
                {
                    bits = (storage, 0, $"_bits{bitRuns++}");
                    if (!first)
                    {
                        text.Append('\n');
                    }

                    first = false;
                    text.Append(CultureInfo.InvariantCulture, $"    private {Backing(storage)} {bits.Value.Name};\n");
                }

                string bitName = Names.Pascal(field.Name);
                _names.Add(($"{cName}.{field.Name}", bitName));
                text.Append('\n').Append(Indent(Summary(field.Comment)));
                text.Append(Indent(BitField(storage, bitType, bits.Value.Name, bitName, bits.Value.Offset, field.BitFieldWidth)));
                bits = (storage, bits.Value.Offset + field.BitFieldWidth, bits.Value.Name);
                continue;
            }

            bits = null;
            string fieldName = field.Name.Length == 0 ? "" : Names.Pascal(field.Name);
            string kind = "";
            if (Unqualified(field.Type) is CppClass anonymous && !IsNamed(anonymous.Name) && !_classNames.ContainsKey(anonymous))
            {
                // An unnamed union or struct member is a field named Union or Struct, of a nested type of that name.
                kind = anonymous.ClassKind == CppClassKind.Union ? "Union" : "Struct";
                if (fieldName.Length == 0)
                {
                    fieldName = kind;
                }
            }

            if (fieldName.Length == 0)
            {
                throw new UnsupportedException($"{cName} has an unnamed member");
            }

            if (string.Equals(fieldName, name, StringComparison.Ordinal) || !fieldNames.Add(fieldName))
            {
                fieldName += "_";
            }

            // The nested type is named from the field as it ends up, so a struct's second unnamed union, Union_, is a Union_Type.
            string fieldType = kind.Length == 0 ? "" : field.Name.Length == 0 ? fieldName + "Type" : fieldName + kind;

            _names.Add(($"{cName}.{(field.Name.Length == 0 ? fieldName : field.Name)}", fieldName));
            if (!first)
            {
                text.Append('\n');
            }

            first = false;
            if (fieldType.Length > 0 && Unqualified(field.Type) is CppClass anonymousType)
            {
                text.Append(Indent(StructText(anonymousType, $"{cName}.{fieldName}", fieldType)));
                text.Append('\n');
            }

            text.Append(Indent(Summary(field.Comment)));
            if (union)
            {
                text.Append("    [FieldOffset(0)]\n");
            }

            text.Append(Indent(fieldType.Length > 0 ? $"public {fieldType} {fieldName};\n" : Field(field, fieldName, cName)));
        }

        text.Append("}\n");
        return text.ToString();
    }

    private string Field(CppField field, string fieldName, string structName)
    {
        CppType type = Unqualified(field.Type);
        if (type is not CppArrayType array)
        {
            string fieldType = Type(type, false);
            return $"public {fieldType} {fieldName};\n";
        }

        int count = 1;
        CppType element = array;
        while (Unqualified(element) is CppArrayType dimension)
        {
            if (dimension.Size <= 0)
            {
                throw new UnsupportedException($"{structName}.{field.Name} is an array without a size");
            }

            count *= dimension.Size;
            element = dimension.ElementType;
        }

        string elementType = Type(Unqualified(element), false);
        string size = count.ToString(CultureInfo.InvariantCulture);
        if (_fixedBufferTypes.Contains(elementType))
        {
            return $"public fixed {elementType} {fieldName}[{size}];\n";
        }

        return $"[InlineArray({size})]\npublic partial struct {fieldName}Array\n{{\n    private {elementType} _element0;\n}}\n\npublic {fieldName}Array {fieldName};\n";
    }

    /// <summary>The alignment the struct's fields would give it unpacked: the largest of theirs.</summary>
    private static int Alignment(CppClass cppClass)
    {
        int alignment = 1;
        foreach (CppField field in cppClass.Fields)
        {
            CppType type = Unqualified(field.Type);
            while (type is CppArrayType array)
            {
                type = Unqualified(array.ElementType);
            }

            int fieldAlignment = type switch
            {
                CppClass nested => Math.Max(nested.AlignOf, 1),
                CppPointerType => 8,
                _ => Math.Clamp(type.SizeOf, 1, 8),
            };
            alignment = Math.Max(alignment, fieldAlignment);
        }

        return alignment;
    }

    /// <summary>
    /// The integer a bit field is stored as: its own type for a primitive, the integer of its enum, and
    /// <c>int</c> for an enum this library did not define (a typedef to one, as Dear ImGui's flags are).
    /// </summary>
    private static string Storage(string type, CppType cType)
    {
        if (type is "uint" or "int" or "ushort" or "short" or "byte" or "sbyte" or "bool")
        {
            return type;
        }

        if (cType is CppEnum cppEnum)
        {
            return Underlying(cppEnum);
        }

        return "int";
    }

    /// <summary>The size in bytes of a bit field's storage type, which is what a compiler packs a run by.</summary>
    private static int Size(string storage) => storage switch
    {
        "int" or "uint" => 4,
        "short" or "ushort" => 2,
        _ => 1,
    };

    /// <summary>The unsigned integer a run of bit fields stored as <paramref name="storage"/> is kept in.</summary>
    private static string Backing(string storage) => storage switch
    {
        "int" or "uint" => "uint",
        "short" or "ushort" => "ushort",
        _ => "byte",
    };

    /// <summary>
    /// A bit field as a property over <paramref name="width"/> bits at <paramref name="offset"/> of its backing field,
    /// read and written as C reads and writes it: a signed one sign-extends, a bool is its one bit, an enum goes
    /// through its integer.
    /// </summary>
    private static string BitField(string storage, string type, string backing, string name, int offset, int width)
    {
        string backingType = Backing(storage);
        string mask = backingType switch
        {
            "uint" => string.Create(CultureInfo.InvariantCulture, $"0x{(width >= 32 ? uint.MaxValue : (1u << width) - 1):X}u"),
            _ => string.Create(CultureInfo.InvariantCulture, $"0x{(1u << width) - 1:X}"),
        };
        string bitsOffset = offset.ToString(CultureInfo.InvariantCulture);
        string read = $"({backing} >> {bitsOffset}) & {mask}";
        string signedRead = $"(({storage})({backing} << {(32 - offset - width).ToString(CultureInfo.InvariantCulture)})) >> {(32 - width).ToString(CultureInfo.InvariantCulture)}";
        string Write(string value) => $"{backing} = ({backingType})(({backing} & ~({mask} << {bitsOffset})) | ((({backingType}){value} & {mask}) << {bitsOffset}))";
        if (string.Equals(storage, "bool", StringComparison.Ordinal))
        {
            return $"public bool {name}\n{{\n    get => ({read}) != 0;\n    set => {backing} = ({backingType})(({backing} & ~({mask} << {bitsOffset})) | ((value ? 1u : 0u) << {bitsOffset}));\n}}\n";
        }

        bool signed = storage is "int" or "short" or "sbyte";
        string get = signed ? signedRead : read;
        if (string.Equals(storage, type, StringComparison.Ordinal))
        {
            return $"public {type} {name}\n{{\n    get => ({type})({get});\n    set => {Write("value")};\n}}\n";
        }

        return $"public {type} {name}\n{{\n    get => ({type})({storage})({get});\n    set => {Write($"({storage})value")};\n}}\n";
    }

    private void Opaque(string cName, CppClass cppClass)
    {
        (string owner, string rest) = Names.Split(cName, _options.Prefixes);
        string name = Names.Pascal(rest);
        string text = $"{Summary(cppClass.Comment)}/// <remarks>Opaque: only ever used through a pointer.</remarks>\npublic partial struct {name}\n{{\n}}\n";
        _declarations.Add(new(DeclarationKind.Type, cName, owner, false, name, text));
        _names.Add((cName, Qualified(cName)));
    }

    private void Function(CppFunction function)
    {
        if (function.Flags.HasFlag(CppFunctionFlags.Variadic))
        {
            _skipped.Add(new($"variadic function {function.Name}", Where(function)));
            return;
        }

        if (function.Flags.HasFlag(CppFunctionFlags.Inline) || function.StorageQualifier == CppStorageQualifier.Static)
        {
            _skipped.Add(new($"inline function {function.Name}", Where(function)));
            return;
        }

        foreach (CppParameter parameter in function.Parameters)
        {
            if (IsVaList(parameter.Type))
            {
                _skipped.Add(new($"function {function.Name} (takes a va_list)", Where(function)));
                return;
            }
        }

        (string owner, string rest) = Names.Split(function.Name, _options.Prefixes);
        string name = Names.Pascal(rest);
        bool attaches = _structs.Contains(owner);
        if (attaches && string.Equals(name, Names.Pascal(Names.Split(owner, _options.Prefixes).Name), StringComparison.Ordinal))
        {
            // A C# member cannot be named like its type: a constructor named for its struct, ImDrawCmd_ImDrawCmd, is ImDrawCmd.ImDrawCmd_.
            name += "_";
        }

        StringBuilder parameters = new();
        int index = 0;
        HashSet<string> parameterNames = new(StringComparer.Ordinal);
        foreach (CppParameter parameter in function.Parameters)
        {
            if (index > 0)
            {
                parameters.Append(", ");
            }

            string parameterName = parameter.Name.Length == 0 ? "arg" + index.ToString(CultureInfo.InvariantCulture) : Names.Camel(parameter.Name);
            if (!parameterNames.Add(parameterName))
            {
                parameterName += index.ToString(CultureInfo.InvariantCulture);
            }

            CppType type = Unqualified(parameter.Type);
            string parameterType = type is CppArrayType array ? Type(Unqualified(array.ElementType), false) + "*" : Type(type, false);
            parameters.Append(CultureInfo.InvariantCulture, $"{parameterType} {parameterName}");
            index++;
        }

        string returnType = Type(Unqualified(function.ReturnType), true);
        string text = $"{Summary(function.Comment)}[DllImport(Library, EntryPoint = \"{function.Name}\", ExactSpelling = true)]\npublic static extern {returnType} {name}({parameters});\n";
        _declarations.Add(new(DeclarationKind.Function, function.Name, owner, attaches, name, attaches ? Unshadowed(text, Names.Split(owner, _options.Prefixes).Owner, Names.Pascal(Names.Split(owner, _options.Prefixes).Name)) : text));
        _names.Add((function.Name, (owner.Length == 0 ? _options.Class : Container(owner)) + "." + name));
    }

    /// <summary>A <c>va_list</c>, which no C# can build the same on every OS, so what takes one is skipped.</summary>
    private static bool IsVaList(CppType type)
    {
        type = Unqualified(type);
        while (type is CppTypedef typedef)
        {
            if (string.Equals(typedef.Name, "va_list", StringComparison.Ordinal) || string.Equals(typedef.Name, "__builtin_va_list", StringComparison.Ordinal))
            {
                return true;
            }

            type = Unqualified(typedef.ElementType);
        }

        return type is CppArrayType array && Unqualified(array.ElementType) is CppClass { Name: "__va_list_tag" };
    }

    /// <summary>The C# type for a C one, as it is written in a declaration.</summary>
    private string Type(CppType type, bool voidAllowed)
    {
        switch (type)
        {
            case CppQualifiedType qualified:
                return Type(qualified.ElementType, voidAllowed);
            case CppPrimitiveType primitive:
                string text = Primitive(primitive);
                if (string.Equals(text, "void", StringComparison.Ordinal) && !voidAllowed)
                {
                    throw new UnsupportedException("void where a value is needed");
                }

                return text;
            case CppPointerType pointer:
                CppType element = Unqualified(pointer.ElementType);
                return FunctionTypeOf(element) is { } functionType ? FunctionPointer(functionType) : Type(element, true) + "*";
            case CppTypedef typedef:
                if (_flagEnums.TryGetValue(typedef.Name, out string? flagEnum))
                {
                    return Qualified(flagEnum);
                }

                if (_unsignedSizeTypedefs.Contains(typedef.Name))
                {
                    return "nuint";
                }

                if (_signedSizeTypedefs.Contains(typedef.Name))
                {
                    return "nint";
                }

                CppType target = Unqualified(typedef.ElementType);
                if (target is CppPointerType handle && Unqualified(handle.ElementType) is CppClass { IsDefinition: false } opaque)
                {
                    return Qualified(_opaqueNames.TryGetValue(opaque, out CppTypedef? named) ? named.Name : opaque.Name) + "*";
                }

                return Type(target, voidAllowed);
            case CppClass cppClass:
                if (!cppClass.IsDefinition && _opaqueNames.TryGetValue(cppClass, out CppTypedef? handleTypedef))
                {
                    return Qualified(handleTypedef.Name);
                }

                return Qualified(NameOf(cppClass) ?? throw new UnsupportedException("a struct with neither a name nor a typedef"));
            case CppEnum cppEnum:
                return Qualified(NameOf(cppEnum) ?? throw new UnsupportedException("an enum with neither a name nor a typedef"));
            case CppArrayType:
                throw new UnsupportedException("an array where only a field may have one");
            case CppFunctionType:
                throw new UnsupportedException("a function type that is not a pointer");
            default:
                throw new UnsupportedException($"the type {type.FullName}");
        }
    }

    private string FunctionPointer(CppFunctionType functionType)
    {
        StringBuilder text = new("delegate* unmanaged<");
        foreach (CppParameter parameter in functionType.Parameters)
        {
            text.Append(Type(Unqualified(parameter.Type), false)).Append(", ");
        }

        text.Append(Type(Unqualified(functionType.ReturnType), true)).Append('>');
        return text.ToString();
    }

    private static string Primitive(CppPrimitiveType primitive) => primitive.Kind switch
    {
        CppPrimitiveKind.Void => "void",
        CppPrimitiveKind.Bool => "bool",
        CppPrimitiveKind.Char => "byte",
        CppPrimitiveKind.UnsignedChar => "byte",
        CppPrimitiveKind.Short => "short",
        CppPrimitiveKind.UnsignedShort => "ushort",
        CppPrimitiveKind.Int => "int",
        CppPrimitiveKind.UnsignedInt => "uint",
        CppPrimitiveKind.Long => "CLong",
        CppPrimitiveKind.UnsignedLong => "CULong",
        CppPrimitiveKind.LongLong => "long",
        CppPrimitiveKind.UnsignedLongLong => "ulong",
        CppPrimitiveKind.Float => "float",
        CppPrimitiveKind.Double => "double",
        CppPrimitiveKind.IntPtr => "nint",
        CppPrimitiveKind.UIntPtr => "nuint",
        CppPrimitiveKind.WChar => throw new UnsupportedException("a C++ wchar_t"),
        CppPrimitiveKind.LongDouble or CppPrimitiveKind.Int128 or CppPrimitiveKind.UInt128 or CppPrimitiveKind.Float16 or CppPrimitiveKind.BFloat16
            or CppPrimitiveKind.ObjCObject or CppPrimitiveKind.ObjCSel or CppPrimitiveKind.ObjCClass => throw new UnsupportedException($"the primitive type {primitive.Kind}"),
        _ => throw new UnsupportedException($"the primitive type {primitive.Kind}"),
    };

    private static CppType Unqualified(CppType type)
    {
        while (type is CppQualifiedType qualified)
        {
            type = qualified.ElementType;
        }

        return type;
    }

    /// <summary>
    /// The function type under any qualifiers and typedefs, or null: a header may name a function type
    /// (<c>typedef int b3AssertFcn(const char*)</c>) and take a pointer to it (<c>b3AssertFcn*</c>), which is a function
    /// pointer as much as a pointer typedef is.
    /// </summary>
    private static CppFunctionType? FunctionTypeOf(CppType type)
    {
        type = Unqualified(type);
        while (type is CppTypedef typedef)
        {
            type = Unqualified(typedef.ElementType);
        }

        return type as CppFunctionType;
    }

    private string? NameOf(CppClass cppClass)
        => _classNames.TryGetValue(cppClass, out string? name) ? name : IsNamed(cppClass.Name) ? cppClass.Name : null;

    /// <summary>
    /// An enum's C name: its own, the typedef's that names it, or, for one with neither whose members share a prefix
    /// that is a typedef to an integer (Dear ImGui's forward-declared enums: <c>typedef int ImGuiDir;</c> beside
    /// <c>enum { ImGuiDir_None, … }</c>), that prefix with the underscore the flags idiom expects, so the typedef takes
    /// the enum.
    /// </summary>
    private string? NameOf(CppEnum cppEnum)
    {
        if (IsNamed(cppEnum.Name))
        {
            return cppEnum.Name;
        }

        if (_enumNames.TryGetValue(cppEnum, out string? name))
        {
            return name;
        }

        // The prefix most members share: an enum may hold a few of another's (ImGuiKey holds the ImGuiMod_ values).
        Dictionary<string, int> prefixes = new(StringComparer.Ordinal);
        foreach (CppEnumItem item in cppEnum.Items)
        {
            int underscore = item.Name.IndexOf('_', StringComparison.Ordinal);
            if (underscore > 0)
            {
                string own = item.Name[..underscore];
                prefixes[own] = prefixes.GetValueOrDefault(own) + 1;
            }
        }

        string? prefix = null;
        foreach ((string own, int count) in prefixes)
        {
            if (prefix is null || count > prefixes[prefix])
            {
                prefix = own;
            }
        }

        return prefix is not null && _typedefs.TryGetValue(prefix, out CppTypedef? typedef) && IsInteger(typedef) ? prefix + "_" : null;
    }

    /// <summary>Whether a typedef is, through any typedefs between, an integer.</summary>
    private static bool IsInteger(CppTypedef typedef)
    {
        CppType type = Unqualified(typedef.ElementType);
        while (type is CppTypedef inner)
        {
            type = Unqualified(inner.ElementType);
        }

        return type is CppPrimitiveType { Kind: not (CppPrimitiveKind.Float or CppPrimitiveKind.Double or CppPrimitiveKind.Void or CppPrimitiveKind.Bool) };
    }

    /// <summary>Whether a name is one the header gave, not the placeholder libclang gives an anonymous type.</summary>
    private static bool IsNamed(string name)
        => name.Length > 0 && !name.Contains("(unnamed", StringComparison.Ordinal) && !name.Contains("(anonymous", StringComparison.Ordinal);

    /// <summary>A type's C# name as written from anywhere in the namespace: owner-qualified when it has one.</summary>
    private string Qualified(string cName)
    {
        (string owner, string rest) = Names.Split(cName, _options.Prefixes);
        string name = Names.Pascal(rest);
        return owner.Length == 0 ? name : Container(owner) + "." + name;
    }

    /// <summary>The C# name of an owner: a struct's PascalCase name, or a static class named as the prefix is written.</summary>
    private string Container(string owner)
    {
        if (_structs.Contains(owner))
        {
            return Qualified(owner);
        }

        return owner;
    }

    private static string Summary(CppComment? comment)
    {
        string? raw = comment?.ToString();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        StringBuilder text = new("/// <summary>\n");
        bool blank = false;
        foreach (string rawLine in raw.Split('\n'))
        {
            string line = rawLine.Trim().TrimStart('*').Trim();
            if (line.Length == 0)
            {
                blank = text.Length > "/// <summary>\n".Length;
                continue;
            }

            if (blank)
            {
                text.Append("///\n");
                blank = false;
            }

            text.Append("/// ").Append(line.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal)).Append('\n');
        }

        return text.Append("/// </summary>\n").ToString();
    }

    private static string Indent(string text)
    {
        if (text.Length == 0)
        {
            return text;
        }

        StringBuilder result = new(text.Length + 64);
        foreach (string line in text.TrimEnd('\n').Split('\n'))
        {
            result.Append(line.Length == 0 ? "" : "    ").Append(line).Append('\n');
        }

        return result.ToString();
    }
}

/// <summary>A struct whose layout differs between the platforms' compilers, so no one C# struct is it.</summary>
internal sealed class LayoutException : Exception
{
    public LayoutException()
    {
    }

    public LayoutException(string message)
        : base(message)
    {
    }

    public LayoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A C construct the generator has no faithful C# for.</summary>
internal sealed class UnsupportedException : Exception
{
    public UnsupportedException()
    {
    }

    public UnsupportedException(string message)
        : base(message)
    {
    }

    public UnsupportedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
