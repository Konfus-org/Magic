namespace Magic.Utils;

/// <summary>
/// How far one piece of long work is, as <c>Loading Materials/Checker.mat 0 of 1</c>.
/// </summary>
/// <remarks>
/// Every async load reports it through its <see cref="IProgress{T}"/>, from any thread, never less than last time for
/// the same work.
/// </remarks>
/// <param name="Work">What the work is, written for a person.</param>
/// <param name="Done">How much of it is done.</param>
/// <param name="Total">How much there is.</param>
public readonly record struct LoadProgress(string Work, int Done, int Total);
