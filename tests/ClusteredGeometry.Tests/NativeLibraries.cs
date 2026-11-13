using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Magic.ClusteredGeometry.Tests;

/// <summary>
/// Finds the gem's native libraries for the tests, in its <c>runtimes/&lt;rid&gt;/native/</c> folder beside the test.
/// </summary>
/// <remarks>
/// The gem loads straight into the test, not through its load context, so this tries each name that a platform gives
/// a library.
/// </remarks>
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
