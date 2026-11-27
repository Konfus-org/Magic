using System.Collections.Immutable;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Magic.Assets;
using Magic.DeferredRendering;
using Magic.DeferredRendering.View;
using Magic.Graphics;
using Magic.Utils;

namespace Magic.LightPropagationVolumes;

/// <summary>
/// What the grid takes for each view: the view, the sun's reflective shadow map of it with the lit surfaces drawn,
/// the sky's direction maps with their depth, which show the walls, and the sky's tables, which set the sun's light.
/// </summary>
public readonly record struct VolumeInput(
    GpuView View,
    BounceViews Views,
    BounceMaps Maps,
    SkyViews? Sky,
    SkyDepth? SkyDepth,
    SkyTables? Tables
);

/// <summary>
/// Spreads the sun's light that bounces off the lit surfaces through a grid of cells around the camera of each view,
/// and gives each pixel the light that reaches it as <see cref="BounceLight"/>.
/// </summary>
/// <remarks>
/// <para>
/// It is Crytek's light propagation volumes (Kaplanyan, SIGGRAPH 2009; Kaplanyan and Dachsbacher, I3D 2010), as the
/// master's thesis of Bozek, FIT VUT 2016, also builds them. Each frame the texels of the sun's reflective shadow map
/// put their light into the cells they lie in, the light moves one cell on a number of times, and each pixel reads
/// the sum. The walls between cells come from the sky's direction maps, so a sealed room gets no light from outside.
/// </para>
/// <para>
/// The grid has <see cref="Cells"/> cells along each side, each <see cref="LightPropagationVolumesSettings.CellSize"/>
/// metres, snapped to whole cells of the world so the light keeps still as the camera moves.
/// </para>
/// </remarks>
public sealed class Renderer : IRender<VolumeInput, BounceLight>
{
    /// <summary>
    /// The cells along each side of the grid.
    /// </summary>
    private const int Cells = 32;

    private const int MostSteps = 32;

    // A name for each step of the propagation: GPU timings are kept by name.
    private static readonly ImmutableArray<string> _stepNames =
    [
        .. Enumerable.Range(0, MostSteps).Select(i => $"Propagate light {i}"),
    ];

    private readonly LightPropagationVolumesSettings _settings;

    private readonly IGraphicsApi _gpu;

    private readonly ShaderEntry _injectShader;

    private readonly ShaderEntry _wallShader;

    private readonly ShaderEntry _propagateShader;

    private readonly ShaderEntry _pixelShader;

    private readonly ImmutableArray<ShaderTypeArgument> _openSky;

    internal Renderer(LightPropagationVolumesSettings settings, AssetManager assets, IGraphicsApi gpu)
    {
        _settings = settings;
        _gpu = gpu;
        _injectShader = PassShaders.GetEntry(assets, "PropagateLight", "InjectLight");
        _wallShader = PassShaders.GetEntry(assets, "PropagateLight", "FindWalls");
        _propagateShader = PassShaders.GetEntry(assets, "PropagateLight", "PropagateLight");
        _pixelShader = PassShaders.GetEntry(assets, "PropagateLight", "LightPixels");
        _openSky = SkyTables.GetOpenSky(assets);
    }

    /// <summary>
    /// Injects, spreads and reads the bounced light of the view of this turn.
    /// </summary>
    /// <returns>
    /// The bounced light at each pixel, or none while it is off, its shaders build, or what it writes is not made.
    /// </returns>
    public BounceLight? Render(in RenderFrame frame, RenderGraph graph, in VolumeInput input)
    {
        if (!_settings.IsBounceLightOn)
            return null;

        ShaderEntry injectShader = _injectShader with { TypeArguments = SkyTables.GetSkyType(input.Tables, _openSky) };
        GpuState state = _gpu.GetState(injectShader, _wallShader, _propagateShader, _pixelShader);
        if (state == GpuState.Failed)
            frame.Fail(RenderFailure.Shader, "Bounce light is not spread", "a shader of it was not built.");

        if (state != GpuState.Ready)
            return null;

        GpuView view = input.View;
        int halfWidth = Math.Max((view.Width + 1) / 2, 1);
        int halfHeight = Math.Max((view.Height + 1) / 2, 1);
        TextureDescription light = new("Light volume", 3 * Cells, Cells, GpuFormat.R16G16B16A16Sfloat)
        {
            Kind = TextureKind.Texture3D,
            Depth = Cells,
        };
        Result<GpuTexture> first = _gpu.GetTexture(light, frame.View);
        Result<GpuTexture> second = _gpu.GetTexture(light with { Name = "Light volume step" }, frame.View);
        Result<GpuTexture> sum = _gpu.GetTexture(light with { Name = "Light volume sum" }, frame.View);
        Result<GpuTexture> walls = _gpu.GetTexture(
            new("Light volume walls", Cells, Cells, GpuFormat.R16G16B16A16Sfloat)
            {
                Kind = TextureKind.Texture3D,
                Depth = Cells,
            },
            frame.View
        );
        Result<GpuTexture> output = _gpu.GetTexture(
            new("Bounce light", halfWidth, halfHeight, GpuFormat.R16G16B16A16Sfloat),
            frame.View
        );
        Result<GpuBuffer> constants = _gpu.GetBuffer(
            new("Light volume constants", Unsafe.SizeOf<VolumeConstants>()),
            frame.View
        );
        Result asked = Result.FindFailure(first, second, sum, walls, output, constants);
        if (!asked.Ok)
        {
            frame.Fail(RenderFailure.Resource, "Bounce light is not spread", asked.Error);

            return null;
        }

        // The grid's corner, snapped to whole cells of the world, relative to the camera.
        float cell = Math.Clamp(_settings.CellSize, 0.05f, 16);
        FrameConstants camera = view.Constants;
        Vector3 eye = new(camera.CameraPosition.X, camera.CameraPosition.Y, camera.CameraPosition.Z);
        Vector3 corner = new(
            (MathF.Floor(eye.X / cell) - (Cells / 2)) * cell,
            (MathF.Floor(eye.Y / cell) - (Cells / 2)) * cell,
            (MathF.Floor(eye.Z / cell) - (Cells / 2)) * cell
        );
        SkyViews? sky = input.SkyDepth is null ? null : input.Sky;
        VolumeConstants row = new()
        {
            ClipToRelative = input.Views.ClipToRelative,
            Origin = corner - eye,
            Cell = cell,
            ToAnchor = sky is { } anchored ? eye - anchored.Anchor : Vector3.Zero,
            Size = Cells,
            First = first.Value.Id,
            Second = second.Value.Id,
            Sum = sum.Value.Id,
            Walls = walls.Value.Id,
            MapView = input.Views.Views.Id,
            MapDepth = input.Maps.Depth.Id,
            MapAlbedo = input.Maps.Albedo.Id,
            MapNormal = input.Maps.Normal.Id,
            SkyMaps = sky?.NearMaps.Id ?? 0,
            SkyCount = (uint)(sky?.NearCount ?? 0),
            SkyAtlas = sky?.NearAtlas.Id ?? 0,
            Output = output.Value.Id,
            HalfWidth = (uint)halfWidth,
            HalfHeight = (uint)halfHeight,
            Brightness = Math.Max(_settings.Brightness, 0),
        };
        if (!graph.Upload(constants.Value, [row]))
            return null;

        GpuConstants header = new(view.Frame.Id, constants.Value.Id);
        ReadOnlySpan<GpuResource> tables = input.Tables is { HasTables: true } made
            ? [made.Transmittance, made.View]
            : [];
        graph
            .Compute("Inject light", injectShader, header, RenderStages.Light)
            .Reads(view.Frame, constants.Value, input.Views.Views)
            .Reads(input.Maps.Depth, input.Maps.Albedo, input.Maps.Normal)
            .Reads(tables)
            .Writes(first.Value, sum.Value)
            .DispatchOver(Cells, Cells, Cells);
        ComputePass findWalls = graph
            .Compute("Find light walls", _wallShader, header, RenderStages.Light)
            .Reads(view.Frame, constants.Value);
        if (sky is { } seen)
            findWalls.Reads(seen.NearMaps, seen.NearAtlas);

        findWalls.Writes(walls.Value).DispatchOver(Cells, Cells, Cells);

        // Each step reads the light of the last and writes the other volume, and adds to the sum.
        int steps = Math.Clamp(_settings.Steps, 1, MostSteps);
        for (int step = 0; step < steps; step++)
        {
            (GpuTexture from, GpuTexture to) =
                step % 2 == 0 ? (first.Value, second.Value) : (second.Value, first.Value);
            graph
                .Compute(
                    _stepNames[step],
                    _propagateShader,
                    new GpuConstants(view.Frame.Id, constants.Value.Id, (uint)step),
                    RenderStages.Light
                )
                .Reads(view.Frame, constants.Value, from, walls.Value)
                .Writes(to, sum.Value)
                .DispatchOver(Cells, Cells, Cells);
        }

        graph
            .Compute("Light pixels from the volume", _pixelShader, header, RenderStages.Light)
            .Reads(view.Frame, constants.Value, sum.Value, view.Depth, view.GBufferNormal)
            .Writes(output.Value)
            .DispatchOver(halfWidth, halfHeight);

        return new BounceLight(output.Value);
    }
}

/// <summary>
/// What the grid's passes read beyond the frame, 160 bytes, the twin of <c>PropagateLight.slang</c>'s.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal record struct VolumeConstants
{
    public Matrix4x4 ClipToRelative;

    public Vector3 Origin;

    public float Cell;

    public Vector3 ToAnchor;

    public uint Size;

    public uint First;

    public uint Second;

    public uint Sum;

    public uint Walls;

    public uint MapView;

    public uint MapDepth;

    public uint MapAlbedo;

    public uint MapNormal;

    public uint SkyMaps;

    public uint SkyCount;

    public uint SkyAtlas;

    public uint Output;

    public uint HalfWidth;

    public uint HalfHeight;

    public float Brightness;

    public uint Padding;
}
