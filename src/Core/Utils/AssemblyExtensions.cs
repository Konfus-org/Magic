using System.Reflection;

namespace Magic.Utils;

public static class AssemblyExtensions
{
    extension(Assembly assembly)
    {
        /// <summary>
        /// What the assembly's build recorded as <paramref name="key"/> (<c>&lt;AssemblyMetadata Include="key"&gt;</c>);
        /// null when nothing, or an empty string, was.
        /// </summary>
        public string? GetMetadata(string key)
        {
            foreach (AssemblyMetadataAttribute metadata in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
            {
                if (
                    string.Equals(metadata.Key, key, StringComparison.Ordinal)
                    && metadata.Value is { Length: > 0 } value
                )
                    return value;
            }

            return null;
        }
    }
}
