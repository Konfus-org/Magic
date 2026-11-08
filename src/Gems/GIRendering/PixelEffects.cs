using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs.Components;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The effects run over the lit picture before the output, the air's and then the picture's, in order: each a material
/// whose shader implements the pixel effect contract, its row packed as a surface material's is, and a pass of the entry
/// specialised to its shader. An effect is left out while its material, its textures or its pass load, and when one of
/// them failed.
/// </summary>
internal sealed class PixelEffects(AssetManager assets, GpuResources resources)
{
    private readonly ShaderEntry _applyShader = PassShaders.GetEntry(assets, "ApplyPixelEffect", "ApplyPixelEffect");

    /// <summary>
    /// The apply entry specialised to each effect shader, kept, so a frame makes none.
    /// </summary>
    private readonly Dictionary<ShaderTypeArgument, ShaderEntry> _applyShaders = [];

    /// <summary>
    /// The entries of the effects a frame runs, in order: a reusable buffer for <see cref="Record"/>.
    /// </summary>
    private readonly List<ShaderEntry> _running = [];

    /// <summary>
    /// Runs the effects of each of <paramref name="lists"/>, in order.
    /// </summary>
    public void Record(in RenderView view, ReadOnlySpan<MaterialSlots> lists)
    {
        // The rows and entries of the effects ready to run, in order.
        Span<GpuMaterial> rows = stackalloc GpuMaterial[lists.Length * MaterialSlots.Capacity];
        _running.Clear();
        for (int i = 0; i < rows.Length; i++)
        {
            MaterialInstance slot = lists[i / MaterialSlots.Capacity][i % MaterialSlots.Capacity];
            if (slot.Handle == Handle<Material>.None)
                continue;

            if (resources.GetEffect(slot, out GpuMaterial row, out ShaderTypeArgument shader) != ResourceState.Ready)
                continue;

            ShaderEntry entry = GetApplyShader(shader);
            if (view.Graph.GetState(entry) != GpuState.Ready)
                continue;

            rows[_running.Count] = row;
            _running.Add(entry);
        }

        int count = _running.Count;
        if (count == 0)
            return;

        Result<GpuBuffer> table = view.Graph.Buffer(
            GpuDescriptions.GetBuffer("Effect rows", rows.Length * GpuMaterial.Bytes)
        );
        if (!table.Ok)
        {
            Log.Error($"The picture's effects are not drawn: {table.Error}");

            return;
        }

        view.Graph.Upload(table.Value, rows[..count]);
        for (int effect = 0; effect < count; effect++)
        {
            view.Graph.Compute(
                    "Apply effect",
                    _running[effect],
                    new GpuConstants(view.Constants.Id, table.Value.Id, (uint)effect)
                )
                .Reads(view.Constants, view.ExposureMeter, table.Value, view.Depth)
                .Writes(view.Hdr)
                .DispatchOver(view.Width, view.Height);
        }
    }

    private ShaderEntry GetApplyShader(ShaderTypeArgument shader)
    {
        if (!_applyShaders.TryGetValue(shader, out ShaderEntry entry))
        {
            entry = _applyShader with { TypeArguments = [shader] };
            _applyShaders[shader] = entry;
        }

        return entry;
    }
}
