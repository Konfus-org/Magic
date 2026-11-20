using Magic.Assets;
using Magic.Projects;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.CSharpScripting;

/// <summary>
/// The gem: it runs the C# scripts that entities have.
/// </summary>
public sealed class EntryPoint : Disposable, IGem
{
    public EntryPoint(World world, AssetManager assets, IServiceProvider services)
    {
        Scripts = new Runner(world, assets, services);
        Track(Scripts);
    }

    /// <summary>
    /// Runs the copies of the scripts and keeps their state across a save.
    /// </summary>
    public Runner Scripts { get; }

    public void Update(in Frame frame) => Scripts.Update(frame);

    public void FixedUpdate(in Frame frame) => Scripts.FixedUpdate(frame);

    public void LateUpdate(in Frame frame) => Scripts.LateUpdate(frame);
}
