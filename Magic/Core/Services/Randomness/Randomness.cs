using Magic.Contexts.Settings;
using Magic.Utils;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;

namespace Magic.Services;

/// <summary>
/// The service where every random thing gets its numbers. One seed for the run (<see cref="RandomSettings.Seed"/> when set,
/// else one rolled when the service is made), mixed with what the numbers are for, so each user has a sequence of its
/// own that another user drawing more or fewer numbers never shifts. The exception is minting asset ids, which must
/// never repeat and so stays cryptographically random.
/// </summary>
public sealed class Randomness
{
    private readonly RandomSettings _settings;

    public Randomness(RandomSettings settings)
    {
        _settings = settings;
        Log.Info($"Random seed {Seed} (--set Random.Seed=N repeats a run).");
    }

    /// <summary>
    /// The seed in use: the setting when it is not 0, else the one rolled for this run. Read live, so setting
    /// <c>Random.Seed</c> while running changes what is made next.
    /// </summary>
    public int Seed => _settings.Seed != 0 ? _settings.Seed : Rolled;

    /// <summary>
    /// The run's own seed, never 0, so a run is random unless a seed is asked for.
    /// </summary>
    private int Rolled { get; } = RandomNumberGenerator.GetInt32(1, int.MaxValue);

    /// <summary>
    /// A new sequence for <paramref name="purpose"/> (say <c>"Wander/12"</c>): the same seed and
    /// purpose always give the same numbers.
    /// </summary>
    public Random For(string purpose) =>
        new(Seed ^ (int)XxHash32.HashToUInt32(Encoding.UTF8.GetBytes(purpose)));
}
