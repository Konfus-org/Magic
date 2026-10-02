using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Components;
using Magic.Contexts.Rendering;
using Magic.Utils;
using System.Runtime.InteropServices;
using System.Text;

namespace Magic.Systems.Rendering;

/// <summary>
/// The custom passes: the <c>.pass</c> assets the cameras' <see cref="PostProcessing"/> components list, loaded while
/// one lists them and unloaded when none does, validated, compiled (on a worker; the pass is skipped until ready) and
/// run after the scene over each render target, in the order of its list. A pass is a fullscreen fragment shader or a
/// compute shader with the pass contract of <c>Include/Pass.hlsli</c>: its inputs bound in order, its parameters packed
/// after the frame constants, its output a named target (a new one is created, and made again when the pass changes its
/// format; one it also reads is ping-ponged). A pass that is itself wrong (its file, its shader) is disabled with one
/// logged line and the reason; one that does not fit the list it is in is skipped there, logged once per target.
/// </summary>
internal static class Passes
{
    private const GpuTextureUsage OutputUsage = GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampler | GpuTextureUsage.ComputeWrite;

    /// <summary>
    /// Before a frame is planned: every pass a view lists is loaded (its compile started), and every loaded pass no view
    /// lists any more is unloaded, its pipeline released.
    /// </summary>
    public static void Sync(RenderContext ctx, ReadOnlySpan<View> views)
    {
        List<PassState> states = ctx.Passes.States;
        HashSet<ulong> listed = ctx.Passes.Listed;
        listed.Clear();
        foreach (View view in views)
        {
            PassList list = view.Passes;
            ReadOnlySpan<Handle<Pass>> handles = list;
            foreach (Handle<Pass> handle in handles[..list.Count])
            {
                if (listed.Add(handle.Id) && ctx.Passes.IndexOf(handle.Id) < 0)
                    Load(ctx, handle.Id);
            }
        }

        for (int index = states.Count - 1; index >= 0; index--)
        {
            if (listed.Contains(states[index].Id))
                continue;

            ctx.Gpu.Release(states[index].Pipeline);
            Debugging.Log.Verbose($"Pass {states[index].Path} unloaded: no camera lists it.");
            states.RemoveAt(index);
        }
    }

    /// <summary>
    /// Before a frame is recorded: adds the passes of <paramref name="list"/> that run over the render target this frame
    /// to <paramref name="planned"/>, in order, each given its output among the target's textures (made, or made again
    /// when the pass changed its format or scale, with a twin when the pass reads it too). A pass that is not ready is
    /// left out, and so is one that does not fit the list: an input no earlier pass wrote, or an output an earlier pass
    /// writes in another format or scale. The textures only passes that have left the list used are released.
    /// </summary>
    public static void Prepare(RenderContext ctx, FrameTargets targets, PassList list, List<PassState> planned)
    {
        string? problem = null;
        targets.ClearUse();
        ReadOnlySpan<Handle<Pass>> handles = list;
        foreach (Handle<Pass> handle in handles[..list.Count])
        {
            PassState pass = ctx.Passes.States[ctx.Passes.IndexOf(handle.Id)];
            if (!pass.Ready)
                continue;

            FrameTargets.Target? output = targets.Get(pass.Pass.Output.Name);
            bool differs = output is null || output.Format != pass.OutputFormat || output.Scale != pass.Pass.Output.Scale;
            if (differs && output is { InUse: true })
            {
                problem ??= $"{pass.Path}: output {pass.Pass.Output.Name} is written by an earlier pass in another format or scale; a target has one of each.";
                continue;
            }

            if (MissingInput(targets, pass) is { } input)
            {
                problem ??= $"{pass.Path}: input {input} is not a target by then; a pass earlier in the list has to write it.";
                continue;
            }

            if (differs)
            {
                output = targets.Define(ctx.Gpu, pass.Pass.Output.Name, pass.OutputFormat, OutputUsage, pass.Pass.Output.Scale);
                targets.Ensure(ctx.Gpu, targets.Width, targets.Height);
            }

            output!.InUse = true;
            if (pass.PingPong)
            {
                targets.TwinOf(ctx.Gpu, output);
                output.TwinInUse = true;
            }

            planned.Add(pass);
        }

        targets.ReleaseUnused(ctx.Gpu);
        if (problem is not null && problem != targets.PassProblem)
            Debugging.Log.Error($"Pass skipped for {targets.RenderTarget}: {problem}");

        targets.PassProblem = problem;
    }

    /// <summary>
    /// Records <paramref name="passes"/> over the render target's textures, as <see cref="Prepare"/> left them, in order,
    /// with the target's first view's constants. Outside any pass. True when one of them wrote Ldr;
    /// <paramref name="dispatches"/> counts the compute ones. The only thing it changes is which of a ping-ponged
    /// target's two textures is the current one.
    /// </summary>
    public static bool Record(
        RenderContext ctx,
        RenderCommands commands,
        FrameTargets targets,
        ReadOnlySpan<PassState> passes,
        in FrameConstants frame,
        ref int draws,
        ref int dispatches)
    {
        bool wroteLdr = false;
        Span<GpuBinding> bindings = stackalloc GpuBinding[PassTable.MaxInputs];
        foreach (PassState pass in passes)
        {
            FrameTargets.Target output = targets.Get(pass.Pass.Output.Name)!;
            Span<GpuBinding> inputs = bindings[..pass.Pass.Inputs.Length];
            for (int i = 0; i < inputs.Length; i++)
            {
                FrameTargets.Target input = targets.Get(pass.Pass.Inputs[i])!;
                bool nearest = input.IsDepth || input.Format is GpuFormat.R32Float;
                inputs[i] = new GpuBinding(Texture: input.Texture, Sampler: nearest ? ctx.NearestClamp : ctx.LinearClamp);
            }

            PassConstants constants = new() { Frame = frame };
            pass.Params.CopyTo(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref constants, 1))[FrameConstants.Size..]);
            GpuTexture outputTexture = pass.PingPong ? output.Twin : output.Texture;
            CompiledShader shader = pass.Compiled!;
            if (shader.Stage == GpuStage.Compute)
            {
                commands.Push(GpuStage.Compute, constants);
                commands.BeginComputePass([new GpuBinding(Texture: outputTexture)]);
                commands.BindPipeline(pass.Pipeline);
                if (inputs.Length > 0)
                    commands.BindTextures(GpuStage.Compute, 0, inputs);
                uint groupsX = (output.Width + shader.ThreadCountX - 1) / Math.Max(1, shader.ThreadCountX);
                uint groupsY = (output.Height + shader.ThreadCountY - 1) / Math.Max(1, shader.ThreadCountY);
                commands.Dispatch(groupsX, groupsY);
                commands.EndComputePass();
                dispatches++;
            }
            else
            {
                commands.Push(GpuStage.Fragment, constants);
                commands.BeginRenderPass(outputTexture, GpuLoad.DontCare);
                commands.BindPipeline(pass.Pipeline);
                if (inputs.Length > 0)
                    commands.BindTextures(GpuStage.Fragment, 0, inputs);
                commands.Draw(3);
                commands.EndRenderPass();
                draws++;
            }

            if (pass.PingPong)
                FrameTargets.Swap(output);
            wroteLdr |= output == targets.Ldr;
        }

        return wroteLdr;
    }

    /// <summary>
    /// Reads the .pass file into a new state (keeping the pipeline until the recompile lands) and compiles it. For a new
    /// pass, and again when its file changed.
    /// </summary>
    public static void Load(RenderContext ctx, ulong id)
    {
        List<PassState> passes = ctx.Passes.States;
        int index = ctx.Passes.IndexOf(id);
        if (index < 0)
        {
            passes.Add(new PassState(id, "", new Pass(), false, 0, [], FrameTargets.LdrFormat, default, null, null));
            index = passes.Count - 1;
        }

        Pass? pass = ctx.Assets.Load(new Handle<Pass>(id));
        if (pass is null)
        {
            Disable(ctx, index, "the file could not be read.");
            return;
        }

        bool pingPong = Array.Exists(pass.Inputs, input => string.Equals(input, pass.Output.Name, StringComparison.OrdinalIgnoreCase));
        passes[index] = passes[index] with { Path = pass.Path, Pass = pass, PingPong = pingPong, OutputFormat = FrameTargets.Format(pass.Output), Error = null };

        if (string.IsNullOrEmpty(pass.Output.Name))
            Disable(ctx, index, "no output target named.");
        else if (pass.Inputs.Length > PassTable.MaxInputs)
            Disable(ctx, index, $"{pass.Inputs.Length} inputs; a pass binds at most {PassTable.MaxInputs}.");
        else
            Compile(ctx, index);
    }

    /// <summary>
    /// Composes the shader of the pass at <paramref name="index"/> (contract, inputs, generated parameter loader) and
    /// starts its compile on a worker; packs its parameters. When the pass is loaded, and again when its shader or an
    /// include of it changed.
    /// </summary>
    public static void Compile(RenderContext ctx, int index)
    {
        PassState state = ctx.Passes.States[index];
        Shader? shader = Shaders.Get(ctx, state.Pass.Shader);
        if (shader is null)
        {
            Disable(ctx, index, $"shader {state.Pass.Shader.Id} is not an asset.");
            return;
        }

        if (shader.Stage is not (ShaderStage.Fragment or ShaderStage.Compute))
        {
            Disable(ctx, index, $"{shader.Path} is a {shader.Stage} shader; a pass wants a .frag.hlsl or a .comp.hlsl.");
            return;
        }

        bool compute = shader.Stage == ShaderStage.Compute;
        byte[] parameters = new byte[ParamLayout.RecordBytes];
        string text = shader.Text;
        if (text.Contains("PassParams", StringComparison.Ordinal))
        {
            Result<ParamLayout> layout = ParamLayout.Parse(text, "PassParams", allowTextures: false);
            if (layout.Failed)
            {
                Disable(ctx, index, $"{shader.Path}: {layout.Message}");
                return;
            }

            layout.Payload.Write(state.Pass.Params, _ => TextureTable.None, parameters);
            foreach (string key in layout.Payload.UnknownKeys(state.Pass.Params))
                Debugging.Log.Warn($"{state.Path} sets '{key}', which {shader.Path} does not declare.");

            string stripped = ParamLayout.BlankDefaults(text, layout.Payload.StructSpan);

            // The loader goes right after the struct's closing brace, before main() needs it.
            int end = layout.Payload.StructSpan.End;
            int semicolon = stripped.IndexOf(';', end);
            int insertAt = semicolon >= 0 ? semicolon + 1 : end + 1;
            string loader = layout.Payload.EmitArrayLoader("LoadPassParams", "PassRaw");
            string line = $"#line {1 + stripped.AsSpan(0, insertAt).Count('\n')} \"{shader.Path}\"\n";
            text = stripped[..insertAt] + "\n" + loader + line + stripped[insertAt..];
        }
        else if (state.Pass.Params.Count > 0)
            Debugging.Log.Warn($"{state.Path} has params but {shader.Path} declares no PassParams.");

        StringBuilder composed = new();
        composed.Append("#define PASS_OUTPUT_FORMAT ").Append(ImageFormat(state.OutputFormat)).Append('\n');
        composed.Append("#include \"Include/Pass.hlsli\"\n");
        for (int i = 0; i < state.Pass.Inputs.Length; i++)
        {
            string name = state.Pass.Inputs[i];
            composed.Append("Texture2D ").Append(name).Append(" : READ(").Append(i).Append(");\n");
            composed.Append("SamplerState ").Append(name).Append("Sampler : SAMPLER(").Append(i).Append(");\n");
        }
        composed.Append("#line 1 \"").Append(shader.Path).Append("\"\n").Append(text);

        ctx.Passes.States[index] = state with { ShaderId = shader.Id, Params = parameters };
        string source = composed.ToString();
        string salt = Shaders.ClosureHash(ctx, shader) + string.Join(",", state.Pass.Inputs);
        ctx.Passes.Compiles.Start(state.Id, Task.Run(() => Shaders.Compile(ctx, source, $"{state.Path}+{shader.Path}", compute ? GpuStage.Compute : GpuStage.Fragment, salt)));
    }

    /// <summary>
    /// A compile finished: the pass gets its new pipeline, or is disabled with the error. Main thread: the render system
    /// polls the table's compiles with it once per frame.
    /// </summary>
    public static void FinishCompile(RenderContext ctx, ulong id, Result<CompiledShader> result)
    {
        int index = ctx.Passes.IndexOf(id);
        if (index < 0)
            return; // removed while compiling
        if (result.Failed)
        {
            Disable(ctx, index, result.Message);
            return;
        }

        PassState state = ctx.Passes.States[index];
        CompiledShader shader = result.Payload;
        if (shader.Samplers != state.Pass.Inputs.Length)
        {
            Disable(ctx, index, $"the shader uses {shader.Samplers} of its {state.Pass.Inputs.Length} inputs; every input must be read.");
            return;
        }

        try
        {
            ctx.Gpu.Release(state.Pipeline);
            GpuPipeline pipeline = shader.Stage == GpuStage.Compute
                ? ctx.Gpu.CreateComputePipeline(shader)
                : ctx.Gpu.CreatePipeline(new PipelineDesc(ctx.Passes.FullscreenVertex, shader, state.OutputFormat) { Cull = GpuCull.None });
            ctx.Passes.States[index] = state with { Pipeline = pipeline, Compiled = shader, Error = null };
            Debugging.Log.Verbose($"Pass ready: {state.Path}.");
        }
        catch (InvalidOperationException ex)
        {
            ctx.Passes.States[index] = state with { Pipeline = default, Compiled = null };
            Disable(ctx, index, ex.Message);
        }
    }

    /// <summary>
    /// The first input of <paramref name="pass"/> that is not a target yet: neither one of the engine's (Hdr, Ldr, Depth)
    /// nor written by a pass earlier in this frame's list. Null when it has them all.
    /// </summary>
    private static string? MissingInput(FrameTargets targets, PassState pass)
    {
        foreach (string input in pass.Pass.Inputs)
        {
            FrameTargets.Target? target = targets.Get(input);
            bool builtIn = target is not null && (target == targets.Hdr || target == targets.Ldr || target == targets.Depth);
            if (target is null || !(builtIn || target.InUse))
                return input;
        }

        return null;
    }

    private static string ImageFormat(GpuFormat format)
    {
        return format switch
        {
            GpuFormat.Rgba16Float => "rgba16f",
            GpuFormat.R16Float => "r16f",
            GpuFormat.R32Float => "r32f",
            GpuFormat.Rg16Float => "rg16f",
            GpuFormat.R11G11B10Float => "r11f_g11f_b10f",
            _ => "rgba8",
        };
    }

    /// <summary>Disables the pass with <paramref name="message"/>, logged when it is new.</summary>
    private static void Disable(RenderContext ctx, int index, string message)
    {
        PassState state = ctx.Passes.States[index];
        if (state.Error != message)
            Debugging.Log.Error($"Pass {state.Path} disabled: {message}");

        ctx.Passes.States[index] = state with { Error = message };
    }
}
