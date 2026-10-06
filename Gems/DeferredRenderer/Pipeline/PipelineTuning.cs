using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace DeferredRendererGem;

/// <summary>
/// The passes of the current pipeline with their parameters, for the settings window (<see cref="IPipelineTuning"/>):
/// reads the pass table of the render context there is. Without a context (no renderer) it lists nothing.
/// </summary>
internal sealed class PipelineTuning(Func<RenderContext?> context) : IPipelineTuning
{
    public IReadOnlyList<TunablePass> Passes
    {
        get
        {
            List<TunablePass> passes = [];
            if (context() is not { } ctx)
                return passes;

            foreach (List<PassState> stage in ctx.Pipeline.Stages)
            {
                foreach (PassState pass in stage)
                    passes.Add(Describe(ctx, pass));
            }

            return passes;
        }
    }

    /// <summary>
    /// Where a stage's passes show in the settings window when their file names no group: lighting together, the
    /// scene together, each post under its own name.
    /// </summary>
    private static string GroupOf(PipelineStage stage)
    {
        return stage switch
        {
            PipelineStage.Shadows => "Lighting/Shadows",
            PipelineStage.Gi => "Lighting/Global illumination",
            PipelineStage.Lighting => "Lighting/Lights",
            PipelineStage.Sky => "Lighting/Sky",
            PipelineStage.Scene => "Scene/Drawing",
            PipelineStage.Transparency => "Scene/Transparency",
            _ => "Post",
        };
    }

    private static TunablePass Describe(RenderContext ctx, PassState pass)
    {
        string group = pass.Pass.Group.Length > 0 ? pass.Pass.Group : GroupOf(pass.Stage);
        List<TunableParam> parameters = [];
        if (pass.Layout is { } layout)
        {
            foreach (ParamField field in layout.Fields)
            {
                TunableKind kind = field.Type switch
                {
                    ParamType.Float2 => TunableKind.Float2,
                    ParamType.Float3 => TunableKind.Float3,
                    ParamType.Float4 => TunableKind.Float4,
                    ParamType.Int or ParamType.Int2 or ParamType.Int3 or ParamType.Int4 => TunableKind.Int,
                    ParamType.Uint or ParamType.Uint2 or ParamType.Uint3 or ParamType.Uint4 => TunableKind.Uint,
                    ParamType.Bool => TunableKind.Bool,
                    ParamType.Color => TunableKind.Color,
                    _ => TunableKind.Float,
                };
                Param value = pass.Values.TryGetValue(field.Name, out Param set) ? set : field.DefaultParam;
                ParamTuning tuning = pass.Pass.Tuning.GetValueOrDefault(field.Name) ?? new ParamTuning();
                if (tuning.Group.Length == 0)
                    tuning = new ParamTuning { Group = group, Label = tuning.Label, Description = tuning.Description, Min = tuning.Min, Max = tuning.Max, Fixed = tuning.Fixed };
                parameters.Add(new TunableParam(field.Name, kind, value, tuning));
            }
        }

        string? problem = pass.Error ?? (ctx.Pipeline.Unfit.Contains(pass.Id) ? "does not fit the pipeline" : (pass.Ready ? null : "loading"));
        return new TunablePass(pass.Id, pass.Stage.ToString(), pass.Path.Length > 0 ? System.IO.Path.GetFileName(pass.Path) : pass.Id.ToString(), group, problem, parameters);
    }
}
