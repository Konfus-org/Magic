namespace Magic.Samples.Assets.Scripts;

/// <summary>
/// A back-and-forth for the samples' scripts that move, glow or sound in a cycle.
/// </summary>
internal static class Cycle
{
    /// <summary>
    /// 0 to 1 and back once every <paramref name="periodSec"/> of <paramref name="sec"/>, easing at each end.
    /// </summary>
    public static float GetRise(double sec, float periodSec)
        => (1 - MathF.Cos((float)(sec * Math.Tau / periodSec))) / 2;
}
