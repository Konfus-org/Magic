using Magic.Contexts.Assets;

namespace DeferredRendererGem;

/// <summary>
/// How a pass's names are matched, in one place: names ignore case, and a created resource marked
/// <see cref="PassCreate.History"/> is also read as last frame's copy, its name with <see cref="Previous"/> after it.
/// </summary>
internal static class PassNames
{
    /// <summary>
    /// What a history's last-frame name ends in: <c>&lt;Name&gt;Previous</c>.
    /// </summary>
    public const string Previous = "Previous";

    public static bool Same(string left, string right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    public static bool Reads(Pass pass, string name)
    {
        foreach (PassRead read in pass.Reads)
        {
            if (Same(read.Name, name))
                return true;
        }

        return false;
    }

    public static bool Writes(Pass pass, string name)
    {
        foreach (PassWrite write in pass.Writes)
        {
            if (Same(write.Name, name))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="name"/> is what <paramref name="create"/> makes: the resource itself, or its last frame's
    /// copy when it keeps a history.
    /// </summary>
    public static bool Matches(PassCreate create, string name)
    {
        return Same(create.Name, name) || (create.History && name.Length == create.Name.Length + Previous.Length
            && name.StartsWith(create.Name, StringComparison.OrdinalIgnoreCase) && name.EndsWith(Previous, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// What of <paramref name="pass"/>'s own creates <paramref name="name"/> stands for, the history's included; null for none.
    /// </summary>
    public static PassCreate? CreateOf(Pass pass, string name)
    {
        foreach (PassCreate create in pass.Creates)
        {
            if (Matches(create, name))
                return create;
        }

        return null;
    }
}
