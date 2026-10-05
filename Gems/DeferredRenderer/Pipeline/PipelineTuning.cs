using Magic.Contexts.Assets;
using Magic.Interfaces;

namespace DeferredRendererGem;

/// <summary>
/// The passes of the current pipeline with their parameters, for the settings window (<see cref="IPipelineTuning"/>):
/// reads the pass table of the render context there is, sets a value into a pass's live values and packs them again.
/// Without a context (no renderer) it lists nothing.
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

    public void Set(ulong pass, string name, Param value)
    {
        if (context() is not { } ctx || !ctx.Pipeline.Passes.TryGet(pass, out PassState state) || state.Layout is not { } layout)
            return;

        foreach (ParamField field in layout.Fields)
        {
            if (field.Name != name)
                continue;

            state.Values[name] = value;
            PassLoader.Repack(ctx, pass);
            return;
        }
    }

    private static TunablePass Describe(RenderContext ctx, PassState pass)
    {
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
                parameters.Add(new TunableParam(field.Name, kind, value));
            }
        }

        string? problem = pass.Error ?? (ctx.Pipeline.Unfit.Contains(pass.Id) ? "does not fit the pipeline" : (pass.Ready ? null : "loading"));
        return new TunablePass(pass.Id, pass.Stage.ToString(), pass.Path.Length > 0 ? System.IO.Path.GetFileName(pass.Path) : pass.Id.ToString(), problem, parameters);
    }
}
