using Magic.Contexts;
using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Utils;
using System.Runtime.InteropServices;
using System.Text;

namespace Magic.Systems.Rendering;

/// <summary>
/// The custom passes: every <c>.pass</c> file under a <c>Passes</c> folder of Resources or of the project's Assets,
/// validated, compiled (on a worker; the pass is skipped until ready) and run in (stage, order, path) order after the scene.
/// A pass is a fullscreen fragment shader or a compute shader with the pass contract of <c>Include/Pass.hlsli</c>: its
/// inputs bound in order, its parameters packed after the frame constants, its output a named target (a new one is created,
/// and made again when the pass changes its format; one it also reads is ping-ponged). Anything wrong disables the pass
/// with one logged line and the reason.
/// </summary>
internal static class Passes
{
    private const GpuTextureUsage OutputUsage = GpuTextureUsage.ColorTarget | GpuTextureUsage.Sampler | GpuTextureUsage.ComputeWrite;

    /// <summary>Lists every .pass file again; new ones are loaded, gone ones dropped, kept ones untouched.</summary>
    public static void Discover(RenderContext ctx)
    {
        List<PassState> passes = ctx.Passes.Passes;
        HashSet<ulong> seen = [];
        foreach (string root in (ReadOnlySpan<string>)[ctx.Project.Resources, ctx.Project.Assets])
        {
            string folder = Path.Combine(root, "Passes");
            if (!ctx.Files.DirectoryExists(folder) || ctx.Files.ReadDirectoryRecursive(folder) is not { Ok: true } listing)
                continue;

            foreach (string file in listing.Payload)
            {
                if (!file.EndsWith(".pass", StringComparison.OrdinalIgnoreCase))
                    continue;

                Handle<Pass> handle = ctx.Assets.Find<Pass>(Path.GetRelativePath(root, file).Replace('\\', '/'));
                if (handle.IsValid && seen.Add(handle.Id) && ctx.Passes.IndexOf(handle.Id) < 0)
                    Load(ctx, handle.Id);
            }
        }

        for (int i = passes.Count - 1; i >= 0; i--)
        {
            if (seen.Contains(passes[i].Id))
                continue;

            ctx.Gpu.Release(passes[i].Pipeline);
            Debugging.Log.Info($"Pass {passes[i].Path} removed.");
            passes.RemoveAt(i);
        }

        passes.Sort(static (a, b) => a.Pass.Stage != b.Pass.Stage ? a.Pass.Stage.CompareTo(b.Pass.Stage)
            : a.Pass.Order != b.Pass.Order ? a.Pass.Order.CompareTo(b.Pass.Order)
            : string.CompareOrdinal(a.Path, b.Path));

        StringBuilder listed = new();
        foreach (PassState pass in passes)
            listed.Append(listed.Length == 0 ? "" : ", ").Append(pass.Path).Append(" (").Append(pass.Pass.Stage).Append(pass.Pass.Enabled ? "" : ", disabled").Append(pass.Error is null ? "" : ", error").Append(')');
        Debugging.Log.Info($"Passes: {(passes.Count == 0 ? "none" : listed)}.");
    }

    /// <summary>Assets changed: a changed .pass reloads, a pass whose shader (or an include of it) changed recompiles.</summary>
    public static void OnAssetsChanged(RenderContext ctx, IReadOnlySet<ulong> changed, IReadOnlySet<ulong> shaders)
    {
        List<PassState> passes = ctx.Passes.Passes;
        for (int i = 0; i < passes.Count; i++)
        {
            PassState pass = passes[i];
            if (changed.Contains(pass.Id))
            {
                Load(ctx, pass.Id);
                Debugging.Log.Info($"Pass {pass.Path} reloaded{(passes[i].Error is null ? "" : " (disabled)")}.");
            }
            else if (shaders.Contains(pass.ShaderId))
                Compile(ctx, i);
        }
    }

    /// <summary>Main thread, once per frame: finished compiles become pipelines.</summary>
    public static void Update(RenderContext ctx)
    {
        ctx.Passes.Compiles.Poll(ctx, Finish);
    }

    /// <summary>
    /// Before a frame is recorded: gives every ready pass its output among the render target's textures (made, or made
    /// again when the pass changed its format or scale, with a twin when the pass reads it too) and checks that its inputs
    /// are targets by then. A pass that fails here is disabled, so recording only has to skip it.
    /// </summary>
    public static void Prepare(RenderContext ctx, FrameTargets targets)
    {
        List<PassState> passes = ctx.Passes.Passes;
        for (int p = 0; p < passes.Count; p++)
        {
            PassState pass = passes[p];
            if (!pass.Ready)
                continue;

            if (Conflict(passes, p) is { } earlier)
            {
                Fail(ctx, p, $"output {pass.Pass.Output.Name} is written by {earlier.Path} in another format or scale; a target has one of each.");
                continue;
            }

            FrameTargets.Target? output = targets.Get(pass.Pass.Output.Name);
            if (output is null || output.Format != pass.OutputFormat || output.Scale != pass.Pass.Output.Scale)
            {
                output = targets.Define(ctx.Gpu, pass.Pass.Output.Name, pass.OutputFormat, OutputUsage, pass.Pass.Output.Scale);
                targets.Ensure(ctx.Gpu, targets.Width, targets.Height);
            }

            if (pass.PingPong)
                targets.TwinOf(ctx.Gpu, output);

            foreach (string input in pass.Pass.Inputs)
            {
                if (targets.Get(input) is { Texture.IsValid: true })
                    continue;

                Fail(ctx, p, $"input {input} is not a target of this frame (passes run in stage, order, path order).");
                break;
            }
        }
    }

    /// <summary>
    /// Records every ready, enabled pass over the render target's textures, as <see cref="Prepare"/> left them, in order,
    /// with the target's first view's constants. Outside any pass. True when one of them wrote Ldr;
    /// <paramref name="dispatches"/> counts the compute ones. The only thing it changes is which of a ping-ponged
    /// target's two textures is the current one.
    /// </summary>
    public static bool Record(RenderContext ctx, RenderCommands commands, FrameTargets targets, in FrameConstants frame, ref int draws, ref int dispatches)
    {
        bool wroteLdr = false;
        Span<GpuBinding> bindings = stackalloc GpuBinding[PassTable.MaxInputs];
        foreach (PassState pass in ctx.Passes.Passes)
        {
            if (!pass.Ready || targets.Get(pass.Pass.Output.Name) is not { } output)
                continue;

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

    /// <summary>Reads the .pass file into a new state (keeping the pipeline until the recompile lands) and compiles it.</summary>
    private static void Load(RenderContext ctx, ulong id)
    {
        List<PassState> passes = ctx.Passes.Passes;
        int index = ctx.Passes.IndexOf(id);
        if (index < 0)
        {
            passes.Add(new PassState(id, "", new Pass(), false, 0, [], FrameTargets.LdrFormat, default, null, null));
            index = passes.Count - 1;
        }

        Pass? pass = ctx.Assets.Load(new Handle<Pass>(id));
        if (pass is null)
        {
            Fail(ctx, index, "the file could not be read.");
            return;
        }

        bool pingPong = Array.Exists(pass.Inputs, input => string.Equals(input, pass.Output.Name, StringComparison.OrdinalIgnoreCase));
        passes[index] = passes[index] with { Path = pass.Path, Pass = pass, PingPong = pingPong, OutputFormat = FrameTargets.Format(pass.Output), Error = null };

        if (string.IsNullOrEmpty(pass.Output.Name))
            Fail(ctx, index, "no output target named.");
        else if (pass.Inputs.Length > PassTable.MaxInputs)
            Fail(ctx, index, $"{pass.Inputs.Length} inputs; a pass binds at most {PassTable.MaxInputs}.");
        else
            Compile(ctx, index);
    }

    /// <summary>Composes the pass's shader (contract, inputs, generated parameter loader) and starts its compile; packs its parameters.</summary>
    private static void Compile(RenderContext ctx, int index)
    {
        PassState state = ctx.Passes.Passes[index];
        Shader? shader = Shaders.Get(ctx, state.Pass.Shader);
        if (shader is null)
        {
            Fail(ctx, index, $"shader {state.Pass.Shader.Id} is not an asset.");
            return;
        }

        if (shader.Stage is not (ShaderStage.Fragment or ShaderStage.Compute))
        {
            Fail(ctx, index, $"{shader.Path} is a {shader.Stage} shader; a pass wants a .frag.hlsl or a .comp.hlsl.");
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
                Fail(ctx, index, $"{shader.Path}: {layout.Message}");
                return;
            }

            layout.Payload.Write(state.Pass.Params, _ => TextureTable.None, parameters);
            foreach (string key in layout.Payload.UnknownKeys(state.Pass.Params))
                Debugging.Log.Warn($"{state.Path} sets '{key}', which {shader.Path} does not declare.");

            string stripped = ParamLayout.StripDeclarations(text, layout.Payload.StructSpan);

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

        ctx.Passes.Passes[index] = state with { ShaderId = shader.Id, Params = parameters };
        string salt = Shaders.ClosureHash(ctx, shader) + string.Join(",", state.Pass.Inputs);
        ctx.Passes.Compiles.Start(state.Id, Shaders.CompileAsync(ctx, composed.ToString(), $"{state.Path}+{shader.Path}", compute ? GpuStage.Compute : GpuStage.Fragment, salt));
    }

    /// <summary>A compile finished: the pass gets its new pipeline, or is disabled with the error.</summary>
    private static void Finish(RenderContext ctx, ulong id, Result<CompiledShader> result)
    {
        int index = ctx.Passes.IndexOf(id);
        if (index < 0)
            return; // removed while compiling
        if (result.Failed)
        {
            Fail(ctx, index, result.Message);
            return;
        }

        PassState state = ctx.Passes.Passes[index];
        CompiledShader shader = result.Payload;
        if (shader.Samplers != state.Pass.Inputs.Length)
        {
            Fail(ctx, index, $"the shader uses {shader.Samplers} of its {state.Pass.Inputs.Length} inputs; every input must be read.");
            return;
        }

        try
        {
            ctx.Gpu.Release(state.Pipeline);
            GpuPipeline pipeline = shader.Stage == GpuStage.Compute
                ? ctx.Gpu.CreateComputePipeline(shader)
                : ctx.Gpu.CreatePipeline(new PipelineDesc(ctx.Passes.FullscreenVertex, shader, state.OutputFormat) { Cull = GpuCull.None });
            ctx.Passes.Passes[index] = state with { Pipeline = pipeline, Compiled = shader, Error = null };
            Debugging.Log.Debug($"Pass ready: {state.Path}.");
        }
        catch (InvalidOperationException ex)
        {
            ctx.Passes.Passes[index] = state with { Pipeline = default, Compiled = null };
            Fail(ctx, index, ex.Message);
        }
    }

    /// <summary>An earlier ready pass that writes the same output as pass <paramref name="index"/> in another format or scale.</summary>
    private static PassState? Conflict(List<PassState> passes, int index)
    {
        PassState pass = passes[index];
        for (int i = 0; i < index; i++)
        {
            PassState earlier = passes[i];
            if (earlier.Ready
                && string.Equals(earlier.Pass.Output.Name, pass.Pass.Output.Name, StringComparison.OrdinalIgnoreCase)
                && (earlier.OutputFormat != pass.OutputFormat || earlier.Pass.Output.Scale != pass.Pass.Output.Scale))
                return earlier;
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
    private static void Fail(RenderContext ctx, int index, string message)
    {
        PassState state = ctx.Passes.Passes[index];
        if (state.Error != message)
            Debugging.Log.Error($"Pass {state.Path} disabled: {message}");

        ctx.Passes.Passes[index] = state with { Error = message };
    }
}
