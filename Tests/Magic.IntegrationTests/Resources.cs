using Magic.Services;
using System.Reflection;

namespace Magic.IntegrationTests;

/// <summary>The repo's Resources folder, for the loader tests that decode the engine's own files.</summary>
internal static class Resources
{
    /// <summary>The bytes of a file under Resources; the repo root is stamped into Magic.dll.</summary>
    public static byte[] Read(string relative)
    {
        string repo = typeof(Project).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().First(a => a.Key == "MagicRoot").Value!;

        return File.ReadAllBytes(Path.Combine(repo, "Resources", relative));
    }
}
