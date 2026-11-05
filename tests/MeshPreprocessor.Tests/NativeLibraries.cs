using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Magic.MeshPreprocessor.Tests;

/// <summary>
/// Finds the gem's native libraries for the tests: the gem is loaded straight into the test, not through its load
/// context, so its <c>runtimes/&lt;rid&gt;/native/</c> folder beside the test is tried here instead, under each name
/// a platform gives a library.
/// </summary>
internal static class NativeLibraries
{
    [ModuleInitializer]
    internal static void Register() => NativeLibrary.SetDllImportResolver(typeof(ClusterBuilder).Assembly, Resolve);

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? path)
    {
        string folder = $"{AppContext.BaseDirectory}runtimes/{RuntimeInformation.RuntimeIdentifier}/native/";
        foreach (string file in (ReadOnlySpan<string>)[$"lib{name}.so", $"lib{name}.dylib", $"{name}.dll"])
        {
            if (NativeLibrary.TryLoad(folder + file, out nint handle))
                return handle;
        }

        return nint.Zero;
    }
}
