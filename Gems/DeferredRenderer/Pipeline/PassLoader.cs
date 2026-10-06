using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Utils;
using System.Text;
using System.Text.Json;

namespace DeferredRendererGem;

/// <summary>
/// Turns a listed pass into something the executor can run: reads its file (a <c>.pass</c>, or a <c>.post</c> made
/// into one), validates it, composes and compiles its shaders off the render thread, checks the compiled bindings
/// against the file and builds its pipeline. A pass that is wrong is disabled with one logged line and the reason;
/// it stays disabled, shown as a failure, until its file or shader changes. Everything here runs on the render thread
/// but the compiles.
/// </summary>
internal static class PassLoader
{
    private const string PostExtension = ".post";

    // A depth draw's vertex layout: the mesh position from slot 0, the instance slot from the instance-rate buffer in
    // slot 1 (Shadows/Shadow.vert.hlsl). The vertex stage binds the transforms before the pass's own buffers.
    private static readonly VertexBufferLayout[] DepthVertexBuffers = [new(0, Vertex.Size), new(1, GpuVisible.Size, PerInstance: true)];
    private static readonly VertexAttribute[] DepthVertexAttributes = [new(0, 0, GpuVertexFormat.Float3, 0), new(1, 1, GpuVertexFormat.Uint, 0)];

    // An impostor's card says what it is in its normal and tangent (Include/Impostor.hlsli), so its depth draw takes those too.
    private static readonly VertexAttribute[] ImpostorVertexAttributes =
    [
        new(0, 0, GpuVertexFormat.Float3, 0),
        new(1, 0, GpuVertexFormat.Float3, 12),
        new(2, 0, GpuVertexFormat.Float4, 24),
        new(3, 1, GpuVertexFormat.Uint, 0),
    ];
    private const int DepthFixedVertexBuffers = 1;

    /// <summary>
    /// Whether the asset at <paramref name="path"/> is a <c>.post</c>.
    /// </summary>
    public static bool IsPost(string? path)
    {
        return path?.EndsWith(PostExtension, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// A post as the pass it is: a fullscreen or compute pass reading its inputs and writing its output, which it
    /// creates when it is not the engine's, dispatched over that output.
    /// </summary>
    public static Pass FromPost(Post post)
    {
        Pass pass = new()
        {
            Id = post.Id,
            Version = post.Version,
            Path = post.Path,
            Shader = post.Shader,
            Params = post.Params,
            Group = post.Group,
            Tuning = post.Tuning,
            Reads = [.. post.Inputs.Select(input => new PassRead { Name = input })],
            Writes = [new PassWrite { Name = post.Output.Name }],
            Dispatch = new PassDispatch { Per = "output:" + post.Output.Name },
        };

        if (post.Output.Name.Length > 0 && !ResourceRegistry.IsEngineName(post.Output.Name))
            pass.Creates = [new PassCreate { Name = post.Output.Name, Kind = ResourceKind.Texture, Format = post.Output.Format, Scale = post.Output.Scale }];

        return pass;
    }

    /// <summary>
    /// Reads the pass's file into its state (keeping the pipeline until the recompile lands) and compiles it. For a
    /// pass just listed, and again when its file changed.
    /// </summary>
    public static void Load(RenderContext ctx, ulong id)
    {
        if (!ctx.Pipeline.Passes.TryGet(id, out PassState state))
            return;

        bool isPost = IsPost(ctx.Assets.PathOf(id));
        Pass? pass = isPost ? Preloads.Get<Post>(ctx, id) is { } post ? FromPost(post) : null : Preloads.Get<Pass>(ctx, id);
        if (pass is null)
        {
            Disable(ctx, id, "the file could not be read.");
            return;
        }

        if (isPost && pass.Writes[0].Name.Length == 0)
        {
            state.Path = pass.Path;
            state.IsPost = true;
            state.Pass = pass;
            Disable(ctx, id, "no output target named.");
            return;
        }

        state.Path = pass.Path;
        state.IsPost = isPost;
        state.Pass = pass;
        state.PingPong = Array.Exists(pass.Writes, write => PassNames.Reads(pass, write.Name));
        state.Values = ValuesOf(pass, ctx.Settings.Values);
        state.Error = null;
        state.NeedsPipeline = false;
        ctx.Pipeline.Changed = true;

        if (isPost)
            pass.Kind = Shaders.Get(ctx, pass.Shader)?.Stage == ShaderStage.Compute ? PassKind.Compute : PassKind.Fullscreen;

        Result valid = PassValidator.Validate(pass, state.Stage, Shaders.Get(ctx, pass.Shader)?.Stage, Shaders.Get(ctx, pass.Fragment)?.Stage);
        if (valid.Failed)
        {
            Disable(ctx, id, valid.Message);
            return;
        }

        Compile(ctx, id);
    }

    /// <summary>
    /// Composes the pass's shader (its defines, the generated parameter loader, for a post the contract and its inputs)
    /// and starts its compile on a worker, with the fragment shader's beside it when it has one; packs its parameters.
    /// When the pass is loaded, and again when a shader or an include of it changed.
    /// </summary>
    public static void Compile(RenderContext ctx, ulong id)
    {
        if (!ctx.Pipeline.Passes.TryGet(id, out PassState state))
            return;

        Pass pass = state.Pass;
        if (state.UsesMaterialPipelines)
        {
            state.Compiled = null;
            state.CompiledFragment = null;
            state.NeedsPipeline = false;
            state.Error = null;
            ctx.Pipeline.Changed = true;
            Debugging.Log.Verbose($"Pass ready: {state.Path} (material pipelines).");
            return;
        }

        Shader? shader = Shaders.Get(ctx, pass.Shader);
        if (shader is null)
        {
            Disable(ctx, id, $"shader {pass.Shader.Id} is not an asset.");
            return;
        }

        byte[] parameters = new byte[ParamLayout.RecordBytes];
        ParamLayout? layout = null;
        string text = shader.Text;
        if (text.Contains("PassParams", StringComparison.Ordinal))
        {
            Result<ParamLayout> parsed = ParamLayout.Parse(text, "PassParams", allowTextures: false);
            if (parsed.Failed)
            {
                Disable(ctx, id, $"{shader.Path}: {parsed.Message}");
                return;
            }

            layout = parsed.Payload;
            if (layout.Size > PassValidator.MaxParamBytes)
            {
                Disable(ctx, id, $"{shader.Path}: PassParams packs to {layout.Size} bytes; a pass has {PassValidator.MaxParamBytes}, the last row is the executor's.");
                return;
            }

            layout.Write(state.Values, _ => TextureTable.None, parameters);
            foreach (string key in layout.UnknownKeys(state.Values))
                Debugging.Log.Warn($"{state.Path} sets '{key}', which {shader.Path} does not declare.");

            string stripped = ParamLayout.BlankDefaults(text, layout.StructSpan);

            // The loader goes right after the struct's closing brace, before main() needs it.
            int end = layout.StructSpan.End;
            int semicolon = stripped.IndexOf(';', end);
            int insertAt = semicolon >= 0 ? semicolon + 1 : end + 1;
            string loader = layout.EmitArrayLoader("LoadPassParams", "PassRaw");
            string line = $"#line {1 + stripped.AsSpan(0, insertAt).Count('\n')} \"{shader.Path}\"\n";
            text = stripped[..insertAt] + "\n" + loader + line + stripped[insertAt..];
        }
        else if (state.Values.Count > 0)
            Debugging.Log.Warn($"{state.Path} has params but {shader.Path} declares no PassParams.");

        StringBuilder composed = new();
        foreach (string define in pass.Defines)
            composed.Append(define).Append('\n');

        if (state.IsPost)
        {
            composed.Append("#define PASS_OUTPUT_FORMAT ").Append(ImageFormat(OutputFormat(ctx, state))).Append('\n');
            composed.Append("#include \"Include/Pass.hlsli\"\n");
            for (int i = 0; i < pass.Reads.Length; i++)
            {
                string name = pass.Reads[i].Name;
                composed.Append("Texture2D ").Append(name).Append(" : READ(").Append(i).Append(");\n");
                composed.Append("SamplerState ").Append(name).Append("Sampler : SAMPLER(").Append(i).Append(");\n");
            }
        }

        composed.Append("#line 1 \"").Append(shader.Path).Append("\"\n").Append(text);

        GpuStage stage = pass.Kind switch
        {
            PassKind.Compute => GpuStage.Compute,
            PassKind.Fullscreen => GpuStage.Fragment,
            _ => GpuStage.Vertex,
        };

        ulong fragmentId = 0;
        if (state.NeedsFragment)
        {
            Shader? fragment = Shaders.Get(ctx, pass.Fragment);
            if (fragment is null)
            {
                Disable(ctx, id, $"fragment shader {pass.Fragment.Id} is not an asset.");
                return;
            }

            fragmentId = fragment.Id;
            string fragmentSource = $"#line 1 \"{fragment.Path}\"\n{fragment.Text}";
            string fragmentSalt = Shaders.ClosureHash(ctx, fragment);
            ctx.Pipeline.Compiles.Start(id | PipelineState.FragmentKeyBit, cancel => Shaders.CompileAsync(ctx, fragmentSource, $"{state.Path}+{fragment.Path}", GpuStage.Fragment, fragmentSalt, cancel));
        }

        state.ShaderId = shader.Id;
        state.FragmentId = fragmentId;
        state.Params = parameters;
        state.Layout = layout;
        state.Compiled = null;
        if (state.NeedsFragment)
            state.CompiledFragment = null;
        state.NeedsPipeline = false;
        ctx.Pipeline.Changed = true;
        string source = composed.ToString();
        string salt = Shaders.ClosureHash(ctx, shader) + string.Join(",", pass.Reads.Select(read => read.Name)) + string.Join(";", pass.Defines);
        ctx.Pipeline.Compiles.Start(id, cancel => Shaders.CompileAsync(ctx, source, $"{state.Path}+{shader.Path}", stage, salt, cancel));
    }

    /// <summary>
    /// A pass's values: its file's params with the settings' <c>"PassName.param"</c> values (the preset's, then
    /// <c>--set</c>) laid over them.
    /// </summary>
    public static Dictionary<string, Param> ValuesOf(Pass pass, IReadOnlyDictionary<string, JsonElement> settings)
    {
        Dictionary<string, Param> values = new(pass.Params);
        string prefix = Path.GetFileNameWithoutExtension(pass.Path) + ".";
        foreach ((string key, JsonElement value) in settings)
        {
            if (!key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;

            if (Param.Of(value) is { } param)
                values[key[prefix.Length..]] = param;
            else
                Debugging.Log.Warn($"Setting {key} = {value} is not a pass parameter value (a number, true or false, or [x, y, z, w]).");
        }

        return values;
    }

    /// <summary>
    /// Every pass's values made again from its file and the settings, and packed: the settings' values changed (a preset
    /// applied, a value set from the settings window or the console).
    /// </summary>
    public static void Reapply(RenderContext ctx)
    {
        foreach (List<PassState> stage in ctx.Pipeline.Stages)
        {
            foreach (PassState state in stage)
            {
                state.Values = ValuesOf(state.Pass, ctx.Settings.Values);
                Repack(ctx, state.Id);
            }
        }
    }

    /// <summary>
    /// The pass's parameters packed again from its values.
    /// </summary>
    public static void Repack(RenderContext ctx, ulong id)
    {
        if (!ctx.Pipeline.Passes.TryGet(id, out PassState state) || state.Layout is not { } layout)
            return;

        byte[] parameters = new byte[ParamLayout.RecordBytes];
        layout.Write(state.Values, _ => TextureTable.None, parameters);
        state.Params = parameters;
    }

    /// <summary>
    /// A compile finished: the pass keeps the shader and, once every shader of it is in, gets its pipeline
    /// (<see cref="Finish"/>), or is disabled with the error. Main thread: the render system polls the compiles with it.
    /// </summary>
    public static void FinishCompile(RenderContext ctx, ulong key, Task<Result<CompiledShader>> job)
    {
        ulong id = key & ~PipelineState.FragmentKeyBit;
        bool isFragment = (key & PipelineState.FragmentKeyBit) != 0;
        Result<CompiledShader> result = Shaders.Outcome(job);
        if (!ctx.Pipeline.Passes.TryGet(id, out PassState state))
            return; // removed while compiling
        if (result.Failed)
        {
            Disable(ctx, id, result.Message);
            return;
        }

        if (isFragment)
            state.CompiledFragment = result.Payload;
        else
            state.Compiled = result.Payload;
        state.NeedsPipeline = true;
        Finish(ctx, id);
    }

    /// <summary>
    /// Builds the pipeline of a pass whose shaders have all compiled, once every name it writes stands for something
    /// (the format of a fullscreen pass's target is its creator's): the compiled bindings are held against the file
    /// first. Nothing while a name is still unknown; the listing's fit check says so meanwhile. Each frame for every
    /// pass that waits, and right after a compile lands.
    /// </summary>
    public static void Finish(RenderContext ctx, ulong id)
    {
        PipelineState pipeline = ctx.Pipeline;
        if (!pipeline.Passes.TryGet(id, out PassState state) || !state.NeedsPipeline || !state.HasCompiled || state.Error is not null)
            return;

        Pass pass = state.Pass;
        if (state.Compiled is not { } compiled)
            return;

        foreach (PassRead read in pass.Reads)
        {
            if (ResourceRegistry.Describe(ctx, pipeline, read.Name) is null)
                return;
        }

        foreach (PassWrite write in pass.Writes)
        {
            if (ResourceRegistry.Describe(ctx, pipeline, write.Name) is null)
                return;
        }

        foreach (string color in pass.Draw.Colors)
        {
            if (ResourceRegistry.Describe(ctx, pipeline, color) is null)
                return;
        }

        Result check = CheckBindings(ctx, state, compiled);
        if (check.Failed)
        {
            Disable(ctx, id, check.Message);
            return;
        }

        try
        {
            GpuPipeline built = Build(ctx, state, compiled);
            ctx.Gpu.Release(state.Pipeline);
            state.Pipeline = built;
            state.NeedsPipeline = false;
            state.Error = null;
            pipeline.Changed = true;
            Debugging.Log.Verbose($"Pass ready: {state.Path}.");
        }
        catch (InvalidOperationException ex)
        {
            Disable(ctx, id, ex.Message);
        }
    }

    /// <summary>
    /// The compiled bindings held against the file (<see cref="PassValidator.Check"/>); apart, so its lambda's closure
    /// is made only when a pass is checked, not each frame <see cref="Finish"/> is asked.
    /// </summary>
    private static Result CheckBindings(RenderContext ctx, PassState state, CompiledShader compiled)
    {
        PipelineState pipeline = ctx.Pipeline;
        int fixedBuffers = state.Pass.Kind == PassKind.Draw ? DepthFixedVertexBuffers : 0;
        return PassValidator.Check(state.Pass, compiled, state.CompiledFragment, fixedBuffers, name => ResourceRegistry.Describe(ctx, pipeline, name)?.Kind);
    }

    /// <summary>
    /// Disables the pass with <paramref name="message"/>, logged when it is new.
    /// </summary>
    public static void Disable(RenderContext ctx, ulong id, string message)
    {
        if (!ctx.Pipeline.Passes.TryGet(id, out PassState state))
            return;

        if (state.Error != message)
            Debugging.Log.Error($"Pass {(state.Path.Length > 0 ? state.Path : ctx.Assets.PathOf(id) ?? id.ToString())} disabled: {message}");

        state.Error = message;
        state.NeedsPipeline = false;
        ctx.Pipeline.Changed = true;
    }

    private static GpuPipeline Build(RenderContext ctx, PassState state, CompiledShader compiled)
    {
        Pass pass = state.Pass;
        PipelineState pipeline = ctx.Pipeline;
        switch (pass.Kind)
        {
            case PassKind.Compute:
                return ctx.Gpu.CreateComputePipeline(compiled);
            case PassKind.Fullscreen:
                return ctx.Gpu.CreatePipeline(new PipelineDesc(pipeline.FullscreenVertex, compiled, OutputFormat(ctx, state)) { Cull = GpuCull.None });
            case PassKind.Draw when state.CompiledFragment is { } depthFragment:
                return ctx.Gpu.CreatePipeline(new PipelineDesc(compiled, depthFragment)
                {
                    Buffers = DepthVertexBuffers,
                    Attributes = pass.Draw.Impostors ? ImpostorVertexAttributes : DepthVertexAttributes,
                    Depth = ctx.Gpu.DepthFormat,
                    Cull = GpuCull.None,
                    DepthCompare = pass.Draw.DepthCompare,
                    DepthWrite = pass.Draw.DepthWrite,
                    DepthBiasSlope = pass.Draw.DepthBiasSlope,
                    DepthBiasClamp = pass.Draw.DepthBiasClamp,
                    DepthClip = false,
                });
            case PassKind.Quads when state.CompiledFragment is { } quadFragment:
                GpuFormat[] colors = [.. pass.Draw.Colors.Select(color => ResourceRegistry.Describe(ctx, pipeline, color)?.Format ?? GpuFormat.Invalid)];
                return ctx.Gpu.CreatePipeline(new PipelineDesc(compiled, quadFragment, colors)
                {
                    Depth = pass.Draw.Depth.Length > 0 ? ctx.Gpu.DepthFormat : GpuFormat.Invalid,
                    Cull = GpuCull.None,
                    DepthCompare = pass.Draw.DepthCompare,
                    DepthWrite = pass.Draw.DepthWrite,
                });
            default:
                throw new InvalidOperationException("the pass has no fragment shader to build with.");
        }
    }

    /// <summary>
    /// The format of what a fullscreen or compute pass writes first.
    /// </summary>
    private static GpuFormat OutputFormat(RenderContext ctx, PassState state)
    {
        Pass pass = state.Pass;
        if (pass.Writes.Length == 0)
            return FrameTargets.LdrFormat;

        string name = pass.Writes[0].Name;
        foreach (PassCreate create in pass.Creates)
        {
            if (string.Equals(create.Name, name, StringComparison.OrdinalIgnoreCase))
                return ResourceRegistry.Format(create.Format, ctx.Gpu.DepthFormat);
        }

        return ResourceRegistry.Describe(ctx, ctx.Pipeline, name)?.Format ?? FrameTargets.LdrFormat;
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
            GpuFormat.R8Unorm => "r8",
            GpuFormat.R32Uint => "r32ui",
            GpuFormat.Rgba32Float => "rgba32f",
            _ => "rgba8",
        };
    }
}
