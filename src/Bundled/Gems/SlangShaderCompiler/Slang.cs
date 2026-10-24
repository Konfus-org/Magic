using System.Runtime.InteropServices;

namespace Magic.SlangShaderCompiler;

/// <summary>
/// Slang's API, bound by hand from its slang.h, which only C++ reads: each COM interface's methods called through the
/// object's table at the slot slang.h declares them in, its base interfaces' methods first, the reflection's C
/// functions, and the structs and values they take. Whoever is handed an object releases it (<see cref="Release"/>),
/// but for a module, which its session keeps.
/// </summary>
internal static unsafe partial class Slang
{
    internal const int Ok = 0;

    /// <summary>
    /// <c>SLANG_E_NOT_FOUND</c>: what a file system answers for a file it does not have.
    /// </summary>
    internal const int NotFound = unchecked((int)0x82000005);

    /// <summary>
    /// <c>E_NOINTERFACE</c>: what an object answers when asked for an interface it does not implement.
    /// </summary>
    internal const int NoInterface = unchecked((int)0x80004002);

    /// <summary>
    /// <c>SLANG_SPIRV</c>, the target format.
    /// </summary>
    internal const int Spirv = 6;

    /// <summary>
    /// <c>SLANG_MATRIX_LAYOUT_ROW_MAJOR</c>: a <c>float4x4</c> is laid out row after row, as System.Numerics keeps it,
    /// and takes a row vector, <c>mul(v, M)</c>.
    /// </summary>
    internal const uint RowMajor = 1;

    /// <summary>
    /// <c>SLANG_TARGET_FLAG_GENERATE_SPIRV_DIRECTLY</c>, slang.h's default flags for a target.
    /// </summary>
    internal const uint GenerateSpirvDirectly = 1 << 10;

    /// <summary>
    /// <c>SLANG_LANGUAGE_VERSION_2025</c>, the oldest language a global session takes, and 2026, the one every shader
    /// is written in.
    /// </summary>
    internal const uint LanguageVersion2025 = 2025;
    internal const int LanguageVersion2026 = 2026;

    /// <summary>
    /// <c>SLANG_LAYOUT_RULES_DEFAULT_STRUCTURED_BUFFER</c>: a type laid out as a buffer holds it.
    /// </summary>
    internal const uint StructuredBufferLayout = 2;

    /// <summary>
    /// The interfaces an object of Slang's says what it is with, as slang.h's SLANG_COM_INTERFACE gives them.
    /// </summary>
    internal static readonly Guid UnknownInterface = new("00000000-0000-0000-c000-000000000046");

    internal static readonly Guid CastableInterface = new("87ede0e1-4852-44b0-8bf2-cb31874de239");

    internal static readonly Guid FileSystemInterface = new("003a09fc-3a4d-4ba0-ad60-1fd863a915ab");

    /// <summary>
    /// ISlangUnknown's release, the third method of every object; none for none.
    /// </summary>
    internal static void Release(nint instance)
    {
        if (instance != 0) ((delegate* unmanaged<nint, uint>)GetMethod(instance, 2))(instance);
    }

    /// <summary>
    /// The bytes an ISlangBlob holds: its getBufferPointer and getBufferSize.
    /// </summary>
    internal static ReadOnlySpan<byte> GetBytes(nint blob)
    {
        void* start = ((delegate* unmanaged<nint, void*>)GetMethod(blob, 3))(blob);
        nuint size = ((delegate* unmanaged<nint, nuint>)GetMethod(blob, 4))(blob);

        return new ReadOnlySpan<byte>(start, (int)size);
    }

    /// <summary>
    /// IGlobalSession's createSession.
    /// </summary>
    internal static int CreateSession(nint globalSession, SessionDesc* desc, nint* session)
        => ((delegate* unmanaged<nint, SessionDesc*, nint*, int>)GetMethod(globalSession, 3))(
            globalSession,
            desc,
            session);

    /// <summary>
    /// IGlobalSession's findProfile: the id of the profile Slang calls <paramref name="name"/>.
    /// </summary>
    internal static uint FindProfile(nint globalSession, byte* name)
        => ((delegate* unmanaged<nint, byte*, uint>)GetMethod(globalSession, 4))(globalSession, name);

    /// <summary>
    /// ISession's loadModule: the module named as an import names it, loaded through the session's file system with
    /// what it imports, or none, with what went wrong in <paramref name="diagnostics"/>. The session keeps the module.
    /// </summary>
    internal static nint LoadModule(nint session, byte* moduleName, nint* diagnostics)
        => ((delegate* unmanaged<nint, byte*, nint*, nint>)GetMethod(session, 4))(session, moduleName, diagnostics);

    /// <summary>
    /// ISession's createCompositeComponentType: one program of the modules and entry points in
    /// <paramref name="components"/>.
    /// </summary>
    internal static int CreateCompositeComponentType(
        nint session,
        nint* components,
        nint count,
        nint* composite,
        nint* diagnostics)
        => ((delegate* unmanaged<nint, nint*, nint, nint*, nint*, int>)GetMethod(session, 6))(
            session,
            components,
            count,
            composite,
            diagnostics);

    /// <summary>
    /// IComponentType's getLayout: the program's reflection for the target at <paramref name="target"/>.
    /// </summary>
    internal static nint GetLayout(nint component, nint target, nint* diagnostics)
        => ((delegate* unmanaged<nint, nint, nint*, nint>)GetMethod(component, 4))(component, target, diagnostics);

    /// <summary>
    /// IComponentType's getEntryPointCode: an entry point compiled for a target, as a blob.
    /// </summary>
    internal static int GetEntryPointCode(nint component, nint entryPoint, nint target, nint* code, nint* diagnostics)
        => ((delegate* unmanaged<nint, nint, nint, nint*, nint*, int>)GetMethod(component, 6))(
            component,
            entryPoint,
            target,
            code,
            diagnostics);

    /// <summary>
    /// IComponentType's specialize: the program with its generic parameters bound to <paramref name="arguments"/>.
    /// </summary>
    internal static int Specialize(
        nint component,
        SpecializationArg* arguments,
        nint count,
        nint* specialized,
        nint* diagnostics)
        => ((delegate* unmanaged<nint, SpecializationArg*, nint, nint*, nint*, int>)GetMethod(component, 9))(
            component,
            arguments,
            count,
            specialized,
            diagnostics);

    /// <summary>
    /// IComponentType's link: the program with every module it imports.
    /// </summary>
    internal static int Link(nint component, nint* linked, nint* diagnostics)
        => ((delegate* unmanaged<nint, nint*, nint*, int>)GetMethod(component, 10))(component, linked, diagnostics);

    /// <summary>
    /// IModule's findEntryPointByName: the module's function marked <c>[shader("stage")]</c> named
    /// <paramref name="name"/>.
    /// </summary>
    internal static int FindEntryPointByName(nint module, byte* name, nint* entryPoint)
        => ((delegate* unmanaged<nint, byte*, nint*, int>)GetMethod(module, 17))(module, name, entryPoint);

    /// <summary>
    /// IModule's getDefinedEntryPointCount: how many functions the module marks <c>[shader("stage")]</c>.
    /// </summary>
    internal static int GetDefinedEntryPointCount(nint module)
        => ((delegate* unmanaged<nint, int>)GetMethod(module, 18))(module);

    /// <summary>
    /// IModule's getDefinedEntryPoint: the module's entry point at <paramref name="index"/>.
    /// </summary>
    internal static int GetDefinedEntryPoint(nint module, int index, nint* entryPoint)
        => ((delegate* unmanaged<nint, int, nint*, int>)GetMethod(module, 19))(module, index, entryPoint);

    /// <summary>
    /// A variable's initializer as packed bytes, scalars in their natural order with no padding, or none for one with
    /// no initializer: slang.h's <c>VariableReflection::getDefaultValueBlob</c>, which Slang exports as a C++ member
    /// alone, so it is called by its symbol, the variable as its <c>this</c>.
    /// </summary>
    internal static int GetDefaultValueBlob(nint variable, nint* blob)
        => OperatingSystem.IsWindows()
            ? GetDefaultValueBlobMsvc(variable, blob)
            : GetDefaultValueBlobItanium(variable, blob);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial int slang_createGlobalSession2(GlobalSessionDesc* desc, nint* globalSession);

    /// <summary>
    /// A blob holding a copy of <paramref name="size"/> bytes, at least one.
    /// </summary>
    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nint slang_createBlob(void* data, nuint size);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial uint spReflection_GetParameterCount(nint reflection);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nint spReflection_GetParameterByIndex(nint reflection, uint index);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nint spReflection_getEntryPointByIndex(nint reflection, nuint index);

    /// <summary>
    /// The type named <paramref name="name"/> where the program can see it, or none.
    /// </summary>
    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nint spReflection_FindTypeByName(nint reflection, byte* name);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nint spReflection_GetTypeLayout(nint reflection, nint type, uint rules);

    /// <summary>
    /// Whether <paramref name="subType"/> is <paramref name="superType"/> or implements it; a C bool.
    /// </summary>
    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial byte spReflection_isSubType(nint reflection, nint subType, nint superType);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial Stage spReflectionEntryPoint_getStage(nint entryPoint);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial void spReflectionEntryPoint_getComputeThreadGroupSize(
        nint entryPoint,
        nuint axisCount,
        nuint* sizeAlongAxis);

    /// <summary>
    /// What an entry point's own parameters take, together.
    /// </summary>
    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nint spReflectionEntryPoint_getVarLayout(nint entryPoint);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nint spReflectionVariableLayout_GetTypeLayout(nint variableLayout);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nint spReflectionVariableLayout_GetVariable(nint variableLayout);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nuint spReflectionVariableLayout_GetOffset(nint variableLayout, ParameterCategory category);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial byte* spReflectionVariable_GetName(nint variable);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nint spReflectionVariable_GetType(nint variable);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial uint spReflectionParameter_GetBindingSpace(nint parameter);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial TypeKind spReflectionType_GetKind(nint type);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial ScalarType spReflectionType_GetScalarType(nint type);

    /// <summary>
    /// A vector's components, or an array's elements.
    /// </summary>
    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nuint spReflectionType_GetElementCount(nint type);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial byte* spReflectionType_GetName(nint type);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial TypeKind spReflectionTypeLayout_getKind(nint typeLayout);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial uint spReflectionTypeLayout_GetCategoryCount(nint typeLayout);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial ParameterCategory spReflectionTypeLayout_GetCategoryByIndex(nint typeLayout, uint index);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nuint spReflectionTypeLayout_GetSize(nint typeLayout, ParameterCategory category);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial uint spReflectionTypeLayout_GetFieldCount(nint typeLayout);

    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nint spReflectionTypeLayout_GetFieldByIndex(nint typeLayout, uint index);

    /// <summary>
    /// What a constant buffer holds, or an array's element.
    /// </summary>
    [LibraryImport("slang-compiler")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    internal static partial nint spReflectionTypeLayout_GetElementTypeLayout(nint typeLayout);

    /// <summary>
    /// A COM method of one of Slang's objects: the function at <paramref name="slot"/> of its table.
    /// </summary>
    private static nint GetMethod(nint instance, int slot) => (*(nint**)instance)[slot];

    [LibraryImport(
        "slang-compiler",
        EntryPoint = "?getDefaultValueBlob@VariableReflection@slang@@QEAAHPEAPEAUISlangBlob@@@Z")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int GetDefaultValueBlobMsvc(nint variable, nint* blob);

    [LibraryImport("slang-compiler", EntryPoint = "_ZN5slang18VariableReflection19getDefaultValueBlobEPP10ISlangBlob")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int GetDefaultValueBlobItanium(nint variable, nint* blob);

    /// <summary>
    /// <c>SlangStage</c>: what an entry point runs as.
    /// </summary>
    internal enum Stage : uint
    {
        None = 0,
        Vertex = 1,
        Fragment = 5,
        Compute = 6,
    }

    /// <summary>
    /// <c>SlangParameterCategory</c>: what a shader parameter takes up.
    /// </summary>
    internal enum ParameterCategory : uint
    {
        Uniform = 8,
        DescriptorTableSlot = 9,
        PushConstantBuffer = 11,
    }

    /// <summary>
    /// <c>SlangTypeKind</c>: what kind of type a type is.
    /// </summary>
    internal enum TypeKind : uint
    {
        Struct = 1,
        Array = 2,
        Vector = 4,
        Scalar = 5,
    }

    /// <summary>
    /// <c>SlangScalarType</c>: what a scalar, or a vector's component, is.
    /// </summary>
    internal enum ScalarType : uint
    {
        Bool = 2,
        Int32 = 3,
        UInt32 = 4,
        Float32 = 8,
    }

    /// <summary>
    /// <c>slang::CompilerOptionName</c>: the compile options set, by their place in slang.h's list.
    /// </summary>
    internal enum CompilerOptionName
    {
        WarningsAsErrors = 19,
        DisableWarning = 22,
        DebugInformation = 44,
        Optimization = 46,
        LanguageVersion = 107,
    }

    /// <summary>
    /// <c>SlangGlobalSessionDesc</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct GlobalSessionDesc
    {
        public uint StructureSize;
        public uint ApiVersion;
        public uint MinLanguageVersion;
        public bool EnableGlsl;
        public fixed uint Reserved[16];
    }

    /// <summary>
    /// <c>slang::TargetDesc</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct TargetDesc
    {
        public nuint StructureSize;
        public int Format;
        public uint Profile;
        public uint Flags;
        public uint FloatingPointMode;
        public uint LineDirectiveMode;
        public bool ForceGlslScalarBufferLayout;
        public CompilerOptionEntry* CompilerOptionEntries;
        public uint CompilerOptionEntryCount;
    }

    /// <summary>
    /// <c>slang::SessionDesc</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SessionDesc
    {
        public nuint StructureSize;
        public TargetDesc* Targets;
        public nint TargetCount;
        public uint Flags;
        public uint DefaultMatrixLayoutMode;
        public byte** SearchPaths;
        public nint SearchPathCount;
        public nint PreprocessorMacros;
        public nint PreprocessorMacroCount;
        public nint FileSystem;
        public bool EnableEffectAnnotations;
        public bool AllowGlslSyntax;
        public CompilerOptionEntry* CompilerOptionEntries;
        public uint CompilerOptionEntryCount;
        public bool SkipSpirvValidation;
    }

    /// <summary>
    /// <c>slang::CompilerOptionEntry</c>: an option and its value, an integer or a string.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CompilerOptionEntry
    {
        public CompilerOptionName Name;
        public CompilerOptionValue Value;

        internal static CompilerOptionEntry Integer(CompilerOptionName name, int value)
            => new() { Name = name, Value = new() { Kind = 0, IntValue0 = value } };

        internal static CompilerOptionEntry Text(CompilerOptionName name, byte* value)
            => new() { Name = name, Value = new() { Kind = 1, StringValue0 = value } };
    }

    /// <summary>
    /// <c>slang::CompilerOptionValue</c>.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct CompilerOptionValue
    {
        public int Kind;
        public int IntValue0;
        public int IntValue1;
        public byte* StringValue0;
        public byte* StringValue1;
    }

    /// <summary>
    /// <c>slang::SpecializationArg</c> of kind Type: a generic parameter bound to a type the program's reflection
    /// found.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SpecializationArg
    {
        public int Kind;
        public nint Type;

        internal static SpecializationArg Of(nint type) => new() { Kind = 1, Type = type };
    }
}
