using System.Numerics;
using Magic.Assets;
using Magic.Configuration;
using Magic.Debugging;
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
/// the sky and by the sun where a ray through the world reaches it, blended over frames and summed away from the camera,
/// recorded in the light stage, written into the frame's buffer and made as the frame's <see cref="Air"/>, which
/// shading takes. Clear air makes none. It is public because nothing in its assembly makes it: Magic does, by
/// reflection, when a project lists the gem.
/// </summary>
public sealed class VolumetricsGem : IGem, IHasSettings<VolumetricsSettings>, IRender<VolumetricsInput, Air>
{
    /// <summary>
    /// The volume's slices away from the camera: the library's <c>AirSlices</c>.
    /// </summary>
    private const int Slices = 64;

    private static readonly string[] _scatteringNames = ["Air scattering 0", "Air scattering 1"];

    private readonly IEcs _ecs;
    private readonly Mailbox<AirInputs> _inputs = new();
    private readonly ShaderEntry _scatterShader;
    private readonly ShaderEntry _integrateShader;

    /// <summary>
    /// The inputs <see cref="Render"/> fills the air from, taken from <see cref="_inputs"/>, until newer are. Render
    /// thread.
    /// </summary>
    private AirInputs? _current;

    /// <summary>
    /// The volume's size when it was last filled; none while its history holds nothing. Render thread.
    /// </summary>
    private (int Width, int Height)? _filled;

    public VolumetricsGem(VolumetricsSettings settings, AssetManager assets, IEcs ecs)
    {
        Settings = settings;
        _ecs = ecs;
        _scatterShader = new ShaderEntry
        {
            Module = assets.Find<Shader>("Shaders/FillAir.slang"),
            Name = "ScatterAir",
            Stage = ShaderStage.Compute,
        };
        _integrateShader = _scatterShader with { Name = "IntegrateAir" };
    }

    /// <summary>
    /// How finely the air is filled, which <see cref="Render"/> reads whole each frame. Game thread.
    /// </summary>
    public VolumetricsSettings Settings { get; set; }

    /// <summary>
    /// Publishes the settings and the world's weather for <see cref="Render"/>.
    /// </summary>
    public void LateUpdate(in Frame frame)
    {
        AirInputs back = _inputs.Back;
        back.Settings = Settings;
        _ecs.Snapshot(back.Weather);
        _inputs.Publish();
    }

    /// <summary>
    /// The frame's air, while the passes' shaders are built, what they read and write is made, and the world's weather
    /// holds a haze; none otherwise, and the air changes nothing.
    /// </summary>
    public Air? Render(in Frame frame, RenderGraph graph, in VolumetricsInput input)
    {
        if (_inputs.TryTake(out AirInputs? newest))
        {
            if (_current is not null)
                _inputs.Return(_current);

            _current = newest;
        }

        bool isBuilt =
            graph.GetState(_scatterShader) == GpuState.Ready && graph.GetState(_integrateShader) == GpuState.Ready;
        if (_current is not { Weather.Count: > 0 } inputs || inputs.Weather.First[0].FogDensity <= 0 || !isBuilt)
        {
            _filled = null;

            return null;
        }

        // Two scattering volumes swap every frame, the other holding last frame's, which this frame's blends with.
        GpuView gpu = input.View;
        Weather weather = inputs.Weather.First[0];
        VolumetricsSettings settings = inputs.Settings;
        (int Width, int Height) size = (
            Scale(gpu.Width, settings.VolumetricsScale),
            Scale(gpu.Height, settings.VolumetricsScale)
        );
        int written = (int)(frame.Index % 2);
        Result<GpuTexture> scattering = graph.Texture(GetVolume(_scatteringNames[written], size));
        Result<GpuTexture> history = graph.Texture(GetVolume(_scatteringNames[1 - written], size));
        Result<GpuTexture> air = graph.Texture(GetVolume("Air", size));
        Result<GpuSampler> sampler = graph.Sampler(new SamplerDescription { Wrap = TextureWrap.Clamp });
        foreach (Result made in (ReadOnlySpan<Result>)[scattering, history, air, sampler])
        {
            if (!made.Ok)
            {
                Log.Error($"The air is not filled: {made.Error}");

                return null;
            }
        }

        Color color = weather.FogColor;
        GpuAir row = new()
        {
            Volume = air.Value.Id,
            Sampler = sampler.Value.Id,
            Distance = settings.VolumetricsDistance,
            Density = weather.FogDensity,
            Height = weather.FogHeight,
            HalfHeight = weather.FogHalfHeight,
            Anisotropy = weather.FogAnisotropy,
            Color = new Vector3(color.R, color.G, color.B),
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
        uint groupsX = (uint)((size.Width + 7) / 8);
        uint groupsY = (uint)((size.Height + 7) / 8);
        graph
            .Compute("Scatter air", _scatterShader, header, RenderStage.Light)
            .Reads(traced)
            .Reads(gpu.Frame, history.Value)
            .Writes(scattering.Value)
            .Dispatch(groupsX, groupsY, Slices);
        graph
            .Compute("Integrate air", _integrateShader, header, RenderStage.Light)
            .Reads(gpu.Frame, scattering.Value)
            .Writes(air.Value)
            .Dispatch(groupsX, groupsY);
        _filled = size;

        return new Air(air.Value);
    }

    /// <summary>
    /// A side of the picture at <paramref name="scale"/>, from 1 cell up to the whole side.
    /// </summary>
    private static int Scale(int side, float scale) => Math.Clamp((int)MathF.Round(side * scale), 1, side);

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

    /// <summary>
    /// What the game thread hands a frame: the settings and the world's weather, copied as its frame ends.
    /// </summary>
    private sealed class AirInputs
    {
        public VolumetricsSettings Settings { get; set; } = new();

        public EcsSnapshot<Weather> Weather { get; } = new();
    }
}
