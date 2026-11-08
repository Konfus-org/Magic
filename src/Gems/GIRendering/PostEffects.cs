using Magic.Assets;
using Magic.Debugging;
using Magic.Ecs.Components;
using Magic.Rendering;
using Magic.Utils;

namespace Magic.GIRendering;

/// <summary>
/// The post effects run over the lit picture before the output, in order: each a material whose shader implements the
/// pixel effect contract, its row packed as a surface material's is, and a pass of the entry specialised to its shader.
/// An effect is left out while its material, its textures or its pass load, and when one of them failed.
/// </summary>
internal sealed class PostEffects(AssetManager assets, GpuResources resources)
{
    private readonly ShaderEntry _applyShader = PassShaders.GetEntry(assets, "ApplyPostEffect", "ApplyPostEffect");

    /// <summary>
    /// The apply entry specialised to each effect shader, kept, so a frame makes none.
    /// </summary>
    private readonly Dictionary<ShaderTypeArgument, ShaderEntry> _applyShaders = [];

    /// <summary>
    /// The entries of the effects a frame runs, in order: a reusable buffer for <see cref="Record"/>.
    /// </summary>
    private readonly ShaderEntry[] _running = new ShaderEntry[MaterialSlots.Capacity];

    public void Record(in RenderView view, in MaterialSlots effects)
    {
        // The rows and entries of the effects ready to run, in order.
        Span<GpuMaterial> rows = stackalloc GpuMaterial[MaterialSlots.Capacity];
        int count = 0;
        foreach (MaterialInstance slot in effects)
        {
            if (slot.Handle == Handle<Material>.None)
                continue;

            if (resources.GetEffect(slot, out rows[count], out ShaderTypeArgument shader) != ResourceState.Ready)
                continue;

            ShaderEntry entry = GetApplyShader(shader);
            if (view.Graph.GetState(entry) == GpuState.Ready)
                _running[count++] = entry;
        }

        if (count == 0)
            return;

        Result<GpuBuffer> table = view.Graph.Buffer(
            GpuDescriptions.GetBuffer("Post effect rows", MaterialSlots.Capacity * GpuMaterial.Bytes)
        );
        if (!table.Ok)
        {
            Log.Error($"The post effects are not drawn: {table.Error}");

            return;
        }

        view.Graph.Upload(table.Value, rows[..count]);
        for (int effect = 0; effect < count; effect++)
        {
            view.Graph.Compute(
                    "Apply post effect",
                    _running[effect],
                    new GpuConstants(view.Constants.Id, table.Value.Id, (uint)effect)
                )
                .Reads(view.Constants, table.Value)
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
