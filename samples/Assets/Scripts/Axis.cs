using Magic.Contexts.Input;
using Magic.Interfaces;

namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// Two controls read as one axis, for the samples' scripts that move or change something with a pair of keys.
/// </summary>
internal static class Axis
{
    /// <summary>
    /// How far <paramref name="positive"/> is held less how far <paramref name="negative"/> is: -1 to 1 for two keys.
    /// </summary>
    public static float Read(IInput input, Control positive, Control negative)
        => input[positive].Value - input[negative].Value;
}
