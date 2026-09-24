namespace Magic.Extensions;

public static class ProgressExtensions
{
    /// <summary>Maps a step's 0..1 progress onto <paramref name="from"/>..<paramref name="to"/> of the whole.</summary>
    public static IProgress<double>? Scale(this IProgress<double>? progress, double from, double to)
    {
        return progress is null ? null : new Progress<double>(p => progress.Report(from + ((to - from) * Math.Clamp(p, 0, 1))));
    }
}
