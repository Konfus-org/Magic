using System.Runtime.InteropServices;

namespace SlangShaders;

/// <summary>
/// The compiler's API, bound by hand (README): its C entry points, and its C++ interfaces called through their
/// tables of functions, each method at the index slang.h declares it. The indices and layouts are those of the tag
/// the project builds; a new tag is checked against them.
/// </summary>
internal static unsafe partial class Slang
{
    private const string Library = "slang-compiler";

    /// <summary>
    /// SLANG_API_VERSION.
    /// </summary>
    private const long ApiVersion = 0;

    /// <summary>
    /// What slang_createGlobalSession makes; freed with <see cref="Release(nint)"/>.
    /// </summary>
    public static nint CreateGlobalSession()
    {
        void* session;
        int result = slang_createGlobalSession(ApiVersion, &session);

        return result >= 0 ? (nint)session : 0;
    }

    public static void Shutdown() => slang_shutdown();

    public static void Release(nint unknown) => Release((void*)unknown);

    public static void Release(void* unknown) => ((delegate* unmanaged<void*, uint>)VTable(unknown)[2])(unknown);

    public static int CreateSession(void* globalSession, SessionDesc* desc, void** outSession) =>
        ((delegate* unmanaged<void*, SessionDesc*, void**, int>)VTable(globalSession)[3])(
            globalSession,
            desc,
            outSession
        );

    public static int FindProfile(void* globalSession, byte* name) =>
        ((delegate* unmanaged<void*, byte*, int>)VTable(globalSession)[4])(globalSession, name);

    /// <summary>
    /// The module, or null with <paramref name="outDiagnostics"/> saying why.
    /// </summary>
    public static void* LoadModuleFromSourceString(
        void* session,
        byte* moduleName,
        byte* path,
        byte* source,
        void** outDiagnostics
    ) =>
        ((delegate* unmanaged<void*, byte*, byte*, byte*, void**, void*>)VTable(session)[20])(
            session,
            moduleName,
            path,
            source,
            outDiagnostics
        );

    public static int CreateCompositeComponentType(
        void* session,
        void** componentTypes,
        long count,
        void** outComposite,
        void** outDiagnostics
    ) =>
        ((delegate* unmanaged<void*, void**, long, void**, void**, int>)VTable(session)[6])(
            session,
            componentTypes,
            count,
            outComposite,
            outDiagnostics
        );

    public static int FindEntryPointByName(void* module, byte* name, void** outEntryPoint) =>
        ((delegate* unmanaged<void*, byte*, void**, int>)VTable(module)[17])(module, name, outEntryPoint);

    public static void* GetLayout(void* componentType, long targetIndex, void** outDiagnostics) =>
        ((delegate* unmanaged<void*, long, void**, void*>)VTable(componentType)[4])(
            componentType,
            targetIndex,
            outDiagnostics
        );

    public static int GetEntryPointCode(
        void* componentType,
        long entryPointIndex,
        long targetIndex,
        void** outCode,
        void** outDiagnostics
    ) =>
        ((delegate* unmanaged<void*, long, long, void**, void**, int>)VTable(componentType)[6])(
            componentType,
            entryPointIndex,
            targetIndex,
            outCode,
            outDiagnostics
        );

    public static int Specialize(
        void* componentType,
        SpecializationArg* args,
        long count,
        void** outSpecialized,
        void** outDiagnostics
    ) =>
        ((delegate* unmanaged<void*, SpecializationArg*, long, void**, void**, int>)VTable(componentType)[9])(
            componentType,
            args,
            count,
            outSpecialized,
            outDiagnostics
        );

    public static int Link(void* componentType, void** outLinked, void** outDiagnostics) =>
        ((delegate* unmanaged<void*, void**, void**, int>)VTable(componentType)[10])(
            componentType,
            outLinked,
            outDiagnostics
        );

    public static void* GetBlobPointer(void* blob) => ((delegate* unmanaged<void*, void*>)VTable(blob)[3])(blob);

    public static nuint GetBlobSize(void* blob) => ((delegate* unmanaged<void*, nuint>)VTable(blob)[4])(blob);

    [LibraryImport(Library)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial void* spReflection_FindTypeByName(void* programLayout, byte* name);

    [LibraryImport(Library)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial void* spReflection_getEntryPointByIndex(void* programLayout, ulong index);

    [LibraryImport(Library)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    public static partial void spReflectionEntryPoint_getComputeThreadGroupSize(
        void* entryPoint,
        ulong axisCount,
        ulong* outSizeAlongAxis
    );

    [LibraryImport(Library)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial int slang_createGlobalSession(long apiVersion, void** outGlobalSession);

    [LibraryImport(Library)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static partial void slang_shutdown();

    private static void** VTable(void* unknown) => *(void***)unknown;

    /// <summary>
    /// slang::SessionDesc, laid out as the C++ compiler lays it out on a 64-bit platform.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SessionDesc
    {
        public nuint StructureSize;
        public TargetDesc* Targets;
        public long TargetCount;
        public uint Flags;
        public int DefaultMatrixLayoutMode;
        public byte** SearchPaths;
        public long SearchPathCount;
        public void* PreprocessorMacros;
        public long PreprocessorMacroCount;
        public void* FileSystem;
        public byte EnableEffectAnnotations;
        public byte AllowGlslSyntax;
        public CompilerOptionEntry* CompilerOptionEntries;
        public uint CompilerOptionEntryCount;
        public byte SkipSpirvValidation;
    }

    /// <summary>
    /// slang::TargetDesc.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TargetDesc
    {
        public nuint StructureSize;
        public int Format;
        public int Profile;
        public uint Flags;
        public int FloatingPointMode;
        public int LineDirectiveMode;
        public byte ForceGlslScalarBufferLayout;
        public CompilerOptionEntry* CompilerOptionEntries;
        public uint CompilerOptionEntryCount;
    }

    /// <summary>
    /// slang::CompilerOptionEntry: an option by its number, with its value, which the value's pointers align to 8.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CompilerOptionEntry
    {
        public int Name;
        public CompilerOptionValue Value;
    }

    /// <summary>
    /// slang::CompilerOptionValue: an integer or a string, as its kind says.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct CompilerOptionValue
    {
        public int Kind;
        public int IntValue0;
        public int IntValue1;
        public byte* StringValue0;
        public byte* StringValue1;
    }

    /// <summary>
    /// slang::SpecializationArg of kind Type.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct SpecializationArg
    {
        public int Kind;
        public void* Type;
    }

    /// <summary>
    /// The compiler options the gem sets, by slang::CompilerOptionName's numbers.
    /// </summary>
    public static class Option
    {
        public const int MatrixLayoutRow = 9;
        public const int WarningsAsErrors = 19;
        public const int DisableWarning = 22;
        public const int DefaultImageFormatUnknown = 40;
        public const int DebugInformation = 44;
        public const int Optimization = 46;
        public const int VulkanUseEntryPointName = 52;
        public const int EmitSpirvDirectly = 58;
    }

    /// <summary>
    /// SlangCompileTarget and the other numbers the session is told.
    /// </summary>
    public static class Target
    {
        public const int Spirv = 6;
        public const int MatrixLayoutRowMajor = 1;
        public const int OptionKindInt = 0;
        public const int OptionKindString = 1;
        public const int SpecializationKindType = 1;
    }
}
