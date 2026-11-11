using System.Numerics;
using Magic.Assets;
using Magic.Configuration;
using Magic.Debugging;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Ecs;
using Magic.Ecs.Components;
using Magic.Graphics;
using Magic.Projects;
using Magic.Threading;
using Magic.Utils;

namespace Magic.Volumetrics;

/// <summary>
/// What the air is filled from: the view, and, where a gem makes it, the world as rays see it, which shadows the air.
/// </summary>
public readonly record struct VolumetricsInput(GpuView View, TracedWorld? Traced);

/// <summary>
/// Fills the air between the camera and what it sees with the world's haze: each frame, a volume over the view, lit by
/// the sky, the sun and the local lights where a ray through the world reaches it, blended over frames and summed away
/// from the camera, recorded in the light stage, written into the frame's buffer and made as the frame's
/// <see cref="Air"/>, which shading takes, with the clouds it draws over the sky. Clear air makes none. It
/// is public because nothing in its assembly makes it: Magic does, by reflection, when a project lists the gem.
/// </summary>
public sealed class VolumetricsGem : IGem, IHasSettings<VolumetricsSettings>, IRender<VolumetricsInput, Air>
{
    /// <summary>
    /// The volume's slices away from the camera: the library's <c>AirSlices</c>.
    /// </summary>
    private const int Slices = 64;

    /// <summary>
    /// The texels along each side of the clouds' picture of the sky.
    /// </summary>
    private const int CloudTexels = 512;

    private const int CloudBlock = 4;

    private static readonly TextureDescription _cloudsDescription = new()
    {
        Width = CloudTexels,
        Height = CloudTexels,
        Format = GpuFormat.R16G16B16A16Sfloat,
        Usage = TextureUsages.Storage | TextureUsages.Sampled,
        Name = "Clouds",
    };

    private static readonly string[] _scatteringNames = ["Air scattering 0", "Air scattering 1"];

    private readonly IEcs _ecs;
    private readonly Mailbox<EcsSnapshot<Weather>> _weather = new();
    private readonly ShaderEntry _scatterShader;
    private readonly ShaderEntry _integrateShader;
    private readonly ShaderEntry _cloudShader;

    /// <summary>
    /// The volume's size when it was last filled; none while its history holds nothing. Render thread.
    /// </summary>
    private (int Width, int Height)? _filled;

    /// <summary>
    /// The clouds' picture as it was last drawn; none while it holds nothing. Render thread.
    /// </summary>
    private GpuTexture? _drawnClouds;

    public VolumetricsGem(VolumetricsSettings settings, AssetManager assets, IEcs ecs)
    {
        Settings = settings;
        _ecs = ecs;
        _scatterShader = PassShaders.GetEntry(assets, "FillAir", "ScatterAir");
        _integrateShader = _scatterShader with { Name = "IntegrateAir" };
        _cloudShader = PassShaders.GetEntry(assets, "DrawClouds", "DrawClouds");
    }

    /// <summary>
    /// How finely the air is filled, which <see cref="Render"/> reads whole each frame. Game thread.
    /// </summary>
    public VolumetricsSettings Settings { get; set; }

    /// <summary>
    /// Publishes the world's weather for <see cref="Render"/>.
    /// </summary>
    public void LateUpdate(in Frame frame)
    {
        _ecs.Snapshot(_weather.Back);
        _weather.Publish();
    }

    /// <summary>
    /// The frame's air, while the passes' shaders are built, what they read and write is made, and the world's weather
    /// holds a haze or clouds; none otherwise, and the air changes nothing.
    /// </summary>
    public Air? Render(in Frame frame, RenderGraph graph, in VolumetricsInput input)
    {
        VolumetricsSettings settings = Settings;
        bool isBuilt = graph.GetState(_scatterShader, _integrateShader) == GpuState.Ready;
        if (
            _weather.TakeNewest() is not { First: [var weather, ..] }
            || weather is { FogDensity: <= 0, CloudCover: <= 0 }
            || !isBuilt
        )
        {
            _filled = null;
            _drawnClouds = null;

            return null;
        }

        // Two scattering volumes swap every frame, the other holding last frame's, which this frame's blends with.
        GpuView gpu = input.View;
        (int Width, int Height) size = gpu.Scaled(settings.VolumetricsScale);
        int written = (int)(frame.Index % 2);
        Result<GpuTexture> scattering = graph.Texture(GetVolume(_scatteringNames[written], size));
        Result<GpuTexture> history = graph.Texture(GetVolume(_scatteringNames[1 - written], size));
        Result<GpuTexture> air = graph.Texture(GetVolume("Air", size));
        Result<GpuSampler> sampler = graph.Sampler(new SamplerDescription { Wrap = TextureWrap.Clamp });
        if (Result.FindFailure(scattering, history, air, sampler) is { Ok: false } failed)
        {
            Log.Error($"The air is not filled: {failed.Error}");

            return null;
        }

        GpuTexture? cloudy = null;
        if (weather.CloudCover > 0 && graph.GetState(_cloudShader) == GpuState.Ready)
        {
            Result<GpuTexture> clouds = graph.Texture(_cloudsDescription);
            if (clouds.Ok)
                cloudy = clouds.Value;
            else
                Log.Error($"The clouds are not drawn: {clouds.Error}");
        }

        GpuAir row = new()
        {
            Volume = air.Value.Id,
            Sampler = sampler.Value.Id,
            Distance = settings.VolumetricsDistance,
            Density = weather.FogDensity,
            Height = weather.FogHeight,
            HalfHeight = weather.FogHalfHeight,
            Anisotropy = weather.FogAnisotropy,
            Color = new Vector3(weather.FogColor.R, weather.FogColor.G, weather.FogColor.B),
            Clouds = cloudy?.Id ?? 0,
            CloudCover = weather.CloudCover,
            CloudHeight = weather.CloudHeight,
            Wind = new Vector2(weather.Wind.X, weather.Wind.Z),
        };
        graph.Upload(gpu.Frame, [row], GpuAir.Offset);

        // Each cell's light and haze, blended with last frame's where the cell was then, then summed along each column
        // away from the camera. The pass header's x is the frame's buffer, y the scattering written, z last frame's, 0
        // when it holds nothing, and w the air.
        uint last = _filled == size ? history.Value.Id : 0;
        GpuConstants header = new(gpu.Frame.Id, scattering.Value.Id, last, air.Value.Id);
        ReadOnlySpan<GpuResource> traced = input.Traced is { } world
            ? [world.HitMaterials, gpu.Instances, gpu.Meshes]
            : [];
        graph
            .Compute("Scatter air", _scatterShader, header, RenderStages.Light)
            .Reads(traced)
            .Reads(gpu.Frame, gpu.LightTable, history.Value)
            .Writes(scattering.Value)
            .DispatchOver(size.Width, size.Height, Slices);
        graph
            .Compute("Integrate air", _integrateShader, header, RenderStages.Light)
            .Reads(gpu.Frame, scattering.Value)
            .Writes(air.Value)
            .DispatchOver(size.Width, size.Height);
        _filled = size;

        // The clouds' picture of the sky: all of it when it holds nothing yet, else a sixteenth a frame, each texel the
        // next of its 4 by 4 block. The pass header's z is the block's side.
        if (cloudy is { } picture)
        {
            int block = _drawnClouds == picture ? CloudBlock : 1;
            graph
                .Compute(
                    "Draw clouds",
                    _cloudShader,
                    new GpuConstants(gpu.Frame.Id, picture.Id, (uint)block, 0),
                    RenderStages.Light
                )
                .Reads(gpu.Frame)
                .Writes(picture)
                .DispatchOver(CloudTexels / block, CloudTexels / block);
        }

        _drawnClouds = cloudy;

        return new Air(air.Value, cloudy);
    }

    /// <summary>
    /// The description of a volume of the air's cells.
    /// </summary>
    private static TextureDescription GetVolume(string name, (int Width, int Height) size) =>
        new()
        {
            Kind = TextureKind.Texture3D,
            Width = size.Width,
            Height = size.Height,
            Depth = Slices,
            Format = GpuFormat.R16G16B16A16Sfloat,
            Usage = TextureUsages.Storage | TextureUsages.Sampled,
            Name = name,
        };
}
