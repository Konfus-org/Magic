using System.Collections.Immutable;
using Magic.Assets;
using Magic.DeferredRendering.View;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;
using Magic.Worlds;

namespace Magic.DeferredRendering;

/// <summary>
/// What the sky's tables are made for: the view.
/// </summary>
public readonly record struct SkyInput(GpuView View);

/// <summary>
/// Makes the sky's tables for each view while an entity has a sky, as the view's <see cref="SkyTables"/>.
/// </summary>
/// <remarks>
/// Shading and the indirect light take the tables. With none, they light the world with the open sky.
/// </remarks>
public sealed class SkyRenderer : IRender<SkyInput, SkyTables>, IRender, IHookIntoDomainLoading
{
    /// <summary>
    /// Done once the sky's shaders are built or failed.
    /// </summary>
    /// <remarks>
    /// A domain loading waits for it, so a shader that fails is told and logged before the world shows.
    /// </remarks>
    private readonly TaskCompletionSource _built = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly IEcs _ecs;

    private readonly Mailbox<EcsSnapshot<Sky>> _skies = new();

    private readonly ShaderEntry _transmittanceShader;

    private readonly ShaderEntry _multiScatteringShader;

    private readonly ShaderEntry _viewShader;

    internal SkyRenderer(AssetManager assets, IEcs ecs)
    {
        _ecs = ecs;
        _transmittanceShader = PassShaders.GetEntry(assets, "SkyLuts", "FillSkyTransmittance");
        _multiScatteringShader = PassShaders.GetEntry(assets, "SkyLuts", "FillSkyMultiScattering");
        _viewShader = PassShaders.GetEntry(assets, "SkyLuts", "FillSkyView");
    }

    /// <summary>
    /// Fills the view's sky tables and writes their row into the view's frame buffer.
    /// </summary>
    /// <returns>
    /// The view's tables, or none while no entity has a sky, the shaders build, or the tables are not made. With none,
    /// the open sky lights the world.
    /// </returns>
    public SkyTables? Render(in Frame frame, RenderGraph graph, in SkyInput input)
    {
        if (_skies.TakeNewest() is not { Count: > 0 })
            return null;

        GpuState shaders = graph.GetState(_transmittanceShader, _multiScatteringShader, _viewShader);
        if (shaders == GpuState.Failed)
            graph.Fail(RenderFailure.Sky, "Sky is not drawn", "a shader of the sky was not built.");

        if (shaders != GpuState.Ready)
            return null;

        Result<GpuTexture> transmittance = graph.Texture(GetTable("Sky transmittance", 256, 64));
        Result<GpuTexture> multiScattering = graph.Texture(GetTable("Sky multi-scattering", 32, 32));
        Result<GpuTexture> view = graph.Texture(GetTable("Sky view", 192, 108));
        if (Result.FindFailure(transmittance, multiScattering, view) is { Ok: false } failed)
        {
            graph.Fail(RenderFailure.Resource, "Sky is not drawn", failed.Error);

            return null;
        }

        // Not staged: nothing is recorded, and the frame's row stays the view's 0, which the shaders read as no sky.
        GpuView gpu = input.View;
        GpuSkyTables row = new()
        {
            Transmittance = transmittance.Value.Id,
            MultiScattering = multiScattering.Value.Id,
            View = view.Value.Id,
        };
        if (!graph.Upload(gpu.Frame, [row], GpuSkyTables.Offset))
            return null;

        // Each table filled over its texels, from the ones before it.
        GpuConstants header = new(gpu.Frame.Id);
        graph
            .Compute("Sky: transmittance", _transmittanceShader, header, RenderStage.Prepare)
            .Reads(gpu.Frame)
            .Writes(transmittance.Value)
            .DispatchOver(256, 64);
        graph
            .Compute("Sky: multi-scattering", _multiScatteringShader, header, RenderStage.Prepare)
            .Reads(gpu.Frame, transmittance.Value)
            .Writes(multiScattering.Value)
            .DispatchOver(32, 32);
        graph
            .Compute("Sky: view", _viewShader, header, RenderStage.Prepare)
            .Reads(gpu.Frame, transmittance.Value, multiScattering.Value)
            .Writes(view.Value)
            .DispatchOver(192, 108);

        return new SkyTables(transmittance.Value, multiScattering.Value, view.Value);
    }

    /// <summary>
    /// Asks for every shader of the sky each frame, with or without a sky, so each is built or failed before a domain
    /// shows.
    /// </summary>
    public void Render(in Frame frame, RenderGraph graph)
    {
        if (!graph.IsAnyLoading(_transmittanceShader, _multiScatteringShader, _viewShader))
            _built.TrySetResult();
    }

    public Task OnDomainLoadingAsync(ImmutableArray<ImmutableArray<IComponent>> entities, CancellationToken cancel) =>
        _built.Task.WaitAsync(cancel);

    /// <summary>
    /// Publishes the world's skies for the frame's tables.
    /// </summary>
    internal void PublishSkies()
    {
        _skies.Back.Fill(_ecs);
        _skies.Publish();
    }

    private static TextureDescription GetTable(string name, int width, int height) =>
        new(name, width, height, GpuFormat.R16G16B16A16Sfloat);
}
