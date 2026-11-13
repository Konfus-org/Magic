using Magic.Assets;
using Magic.Projects;
using Magic.Worlds;

namespace Magic.CSharpScripting;

/// <summary>
/// The gem: it runs the C# scripts that entities have.
/// </summary>
public sealed class EntryPoint(World world, AssetManager assets, IServiceProvider services) : IGem, IDisposable
{
    private readonly Runner _runner = new(world, assets, services);

    public void Dispose() => _runner.Dispose();

    public void Update(in Frame frame) => _runner.Update(frame);

    public void FixedUpdate(in Frame frame) => _runner.FixedUpdate(frame);

    public void LateUpdate(in Frame frame) => _runner.LateUpdate(frame);
}
