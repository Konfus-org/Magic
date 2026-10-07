using Magic.Contexts.Assets;
using Magic.Contexts.Rendering;
using Magic.Utils;

namespace DeferredRendererGem;

/// <summary>
/// What is wrong with a pass, said before it runs. <see cref="Validate"/> reads the file alone, when it is loaded:
/// its kind against its shaders, what it creates, how it dispatches or draws. <see cref="Check"/> holds the compiled
/// shader's bindings against what the file reads and writes: DXC strips a binding nothing uses, so every read must be
/// used, and the counts must agree class by class. <see cref="Fit"/> walks a whole listing in order each frame: every
/// name a pass uses is the engine's or made by a pass before it, within the scope the pass can reach, and nothing is
/// written that may not be. Pure: nothing here touches the GPU or logs.
/// </summary>
internal static class PassValidator
{
    public const int MaxIterations = 64;

    /// <summary>
    /// Bytes a pass's parameters may pack to: the last row of the words is the executor's.
    /// </summary>
    public const int MaxParamBytes = PassRawWords.IterationWord * 4;

    /// <summary>
    /// The file alone, for a pass listed in <paramref name="stage"/>; <paramref name="shaderStage"/> and
    /// <paramref name="fragmentStage"/> are what its shaders are, null for one that is not an asset.
    /// </summary>
    public static Result Validate(Pass pass, PipelineStage stage, ShaderStage? shaderStage, ShaderStage? fragmentStage)
    {
        Result shaders = ValidateShaders(pass, shaderStage, fragmentStage);
        if (shaders.Failed)
            return shaders;

        PassScope scope = PassState.ScopeOf(stage);
        HashSet<string> created = new(StringComparer.OrdinalIgnoreCase);
        foreach (PassCreate create in pass.Creates)
        {
            Result one = ValidateCreate(create, scope);
            if (one.Failed)
                return one;
            if (!created.Add(create.Name))
                return Result.Failure($"creates {create.Name} twice.");
        }

        if (pass.Each.Max is 0 or > MaxIterations)
            return Result.Failure($"each.max is {pass.Each.Max}; a pass runs 1 to {MaxIterations} times.");
        if (pass.Needs.Length > 0 && !FrameCounts.IsName(pass.Needs))
            return Result.Failure($"needs {pass.Needs}, which is not a count; one of {string.Join(", ", FrameCounts.Names)}.");

        foreach (PassRead read in pass.Reads)
        {
            if (read.Name.Length == 0)
                return Result.Failure("a read has no name.");
        }

        foreach (PassWrite write in pass.Writes)
        {
            if (write.Name.Length == 0)
                return Result.Failure("a write has no name.");
            if (PassNames.Reads(pass, write.Name) && PassNames.CreateOf(pass, write.Name) is { } own && !(own.Kind == ResourceKind.Texture && own.Scale > 0f))
                return Result.Failure($"reads and writes {write.Name}, which it creates fixed; a buffer or volume read back is a history (\"history\": true, read as {write.Name}Previous).");
        }

        return pass.Kind switch
        {
            PassKind.Compute => ValidateCompute(pass),
            PassKind.Fullscreen => ValidateFullscreen(pass),
            PassKind.Draw => ValidateDraw(pass, stage),
            PassKind.Quads => ValidateQuads(pass, stage),
            _ => Result.Failure($"kind {pass.Kind} is not one a pass can be."),
        };
    }

    /// <summary>
    /// The compiled shaders against the file: every binding class agrees in count. For a draw or the glows the vertex
    /// stage binds the engine's buffers first (<paramref name="fixedVertexBuffers"/>), then the file's buffer reads.
    /// <paramref name="kindOf"/> says what a name another pass made is (null for one nothing made yet).
    /// </summary>
    public static Result Check(Pass pass, CompiledShader shader, CompiledShader? fragment, int fixedVertexBuffers, Func<string, ResourceKind?> kindOf)
    {
        CountReads(pass, kindOf, out int sampled, out int buffers);
        CountWrites(pass, kindOf, out int textureWrites, out int bufferWrites);
        switch (pass.Kind)
        {
            case PassKind.Compute:
                if (shader.Samplers != sampled)
                    return Mismatch("sampled textures", shader.Samplers, sampled);
                if (shader.StorageTextures != 0)
                    return Mismatch("storage textures", shader.StorageTextures, 0);
                if (shader.StorageBuffers != buffers)
                    return Mismatch("storage buffers", shader.StorageBuffers, buffers);
                if (shader.ReadWriteStorageTextures != textureWrites)
                    return Mismatch("written textures", shader.ReadWriteStorageTextures, textureWrites);
                if (shader.ReadWriteStorageBuffers != bufferWrites)
                    return Mismatch("written buffers", shader.ReadWriteStorageBuffers, bufferWrites);
                if (shader.ThreadCountX * shader.ThreadCountY * shader.ThreadCountZ == 0)
                    return Result.Failure("the shader declares no numthreads.");
                return Result.Success();
            case PassKind.Fullscreen:
                if (shader.Samplers != sampled)
                    return Mismatch("sampled textures", shader.Samplers, sampled);
                if (shader.StorageTextures != 0)
                    return Mismatch("storage textures", shader.StorageTextures, 0);
                if (shader.StorageBuffers != buffers)
                    return Mismatch("storage buffers", shader.StorageBuffers, buffers);
                return Result.Success();
            default:
                if (fragment is null)
                    return Result.Failure("the fragment shader has not compiled.");
                if (shader.StorageBuffers != fixedVertexBuffers + buffers)
                    return Mismatch("vertex storage buffers", shader.StorageBuffers, fixedVertexBuffers + buffers);
                if (shader.Samplers != sampled)
                    return Mismatch("vertex sampled textures", shader.Samplers, sampled);
                return Result.Success();
        }
    }

    /// <summary>
    /// Holds the listing together, stage by stage in order: a pass fits when every name it uses stands for something
    /// by then (the engine's, or made by a pass before it) within the scope it runs in, when it creates nothing an
    /// earlier pass already made, and when it writes only what may be written. Returns each pass that does not fit and
    /// what is wrong with it; the ones that fit go on to run.
    /// </summary>
    public static List<(PassState Pass, string Problem)> Fit(RenderContext ctx, PipelineState pipeline)
    {
        List<(PassState Pass, string Problem)> unfit = [];
        Dictionary<string, ResourceInfo> made = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in ResourceRegistry.EngineNames)
        {
            if (ResourceRegistry.Describe(ctx, pipeline, name) is { } engine)
                made[name] = engine;
        }

        foreach (List<PassState> stage in pipeline.Stages)
        {
            foreach (PassState state in stage)
            {
                if (state.Error is not null)
                    continue;

                string? problem = FitOne(ctx, pipeline, state, made);
                if (problem is not null)
                {
                    unfit.Add((state, problem));
                    continue;
                }

                foreach (PassCreate create in state.Pass.Creates)
                {
                    if (ResourceRegistry.Describe(ctx, pipeline, create.Name) is { } info)
                    {
                        made[create.Name] = info;
                        if (create.History)
                            made[create.Name + PassNames.Previous] = info;
                    }
                }
            }
        }

        return unfit;
    }

    private static string? FitOne(RenderContext ctx, PipelineState pipeline, PassState state, Dictionary<string, ResourceInfo> made)
    {
        Pass pass = state.Pass;
        PassScope scope = PassState.ScopeOf(state.Stage);
        foreach (PassCreate create in pass.Creates)
        {
            if (made.ContainsKey(create.Name))
                return $"creates {create.Name}, which an earlier pass already made.";
        }

        // Its own creates are in reach from here on.
        foreach (PassCreate create in pass.Creates)
        {
            if (ResourceRegistry.Describe(ctx, pipeline, create.Name) is { } info)
                made.TryAdd(create.Name, info);
        }

        foreach (PassRead read in pass.Reads)
        {
            // Last frame's history of something a later pass makes is there to read: order is no matter for it.
            if (!made.ContainsKey(read.Name) && read.Name.EndsWith(PassNames.Previous, StringComparison.OrdinalIgnoreCase) && ResourceRegistry.Describe(ctx, pipeline, read.Name) is { } history)
                made[read.Name] = history;
            if (Reach(read.Name, scope, made) is { } problem)
                return problem;
        }

        foreach (PassWrite write in pass.Writes)
        {
            if (Reach(write.Name, scope, made) is { } problem)
                return problem;
            if (!made.TryGetValue(write.Name, out ResourceInfo info) || !info.IsEngine)
                continue;
            if (info.Kind == ResourceKind.Buffer && !ResourceRegistry.IsWritableEngineBuffer(write.Name))
                return $"writes {write.Name}, an engine buffer only the engine writes.";
            if (info.Kind != ResourceKind.Buffer && pass.Kind is PassKind.Compute or PassKind.Fullscreen && !ResourceRegistry.IsComputeWritable(write.Name))
                return $"writes {write.Name}; a compute or fullscreen pass writes Hdr, Ldr or what a pass created.";
        }

        if (pass.Kind is PassKind.Draw or PassKind.Quads)
        {
            foreach (string color in pass.Draw.Colors)
            {
                if (Reach(color, scope, made) is { } problem)
                    return problem;
                if (made.TryGetValue(color, out ResourceInfo info) && info.IsEngine && !ResourceRegistry.IsDrawTarget(color))
                    return $"draws into {color}, which is not drawn into.";
            }

            foreach (string name in (ReadOnlySpan<string>)[pass.Draw.Depth, pass.Draw.Args, pass.Draw.Instances])
            {
                if (name.Length > 0 && Reach(name, scope, made) is { } problem)
                    return problem;
            }
        }

        if (pass.Dispatch.Indirect.Length > 0 && Reach(pass.Dispatch.Indirect, scope, made) is { } indirect)
            return indirect;

        return null;
    }

    /// <summary>
    /// Why <paramref name="name"/> cannot be used from a pass in <paramref name="scope"/>: nothing stands for it yet, or
    /// it lives in a narrower scope (a frame-wide pass cannot read a view's texture). Null when it can.
    /// </summary>
    private static string? Reach(string name, PassScope scope, Dictionary<string, ResourceInfo> made)
    {
        if (!made.TryGetValue(name, out ResourceInfo info))
            return $"uses {name}, which is not the engine's and no pass before it makes.";
        if (info.Scope > scope)
            return $"uses {name}, a {Describe(info.Scope)} resource, from a pass that runs {Describe(scope)}-wide.";

        return null;
    }

    private static string Describe(PassScope scope)
    {
        return scope switch { PassScope.Frame => "frame", PassScope.Target => "target", _ => "view" };
    }

    private static Result ValidateShaders(Pass pass, ShaderStage? shaderStage, ShaderStage? fragmentStage)
    {
        bool materialDraw = pass.Kind == PassKind.Draw && pass.Draw.Pipeline != DrawPipeline.Depth;
        if (materialDraw)
            return pass.Shader.IsValid || pass.Fragment.IsValid ? Result.Failure("a material draw names no shaders: the engine composes them.") : Result.Success();

        if (shaderStage is null)
            return Result.Failure($"shader {pass.Shader.Id} is not an asset.");

        switch (pass.Kind)
        {
            case PassKind.Compute when shaderStage != ShaderStage.Compute:
                return Result.Failure("a compute pass wants a .comp.hlsl.");
            case PassKind.Fullscreen when shaderStage != ShaderStage.Fragment:
                return Result.Failure("a fullscreen pass wants a .frag.hlsl.");
            case PassKind.Draw or PassKind.Quads when shaderStage != ShaderStage.Vertex:
                return Result.Failure("a depth draw or a quads pass wants a .vert.hlsl as the shader.");
            case PassKind.Draw or PassKind.Quads when fragmentStage != ShaderStage.Fragment:
                return Result.Failure("a depth draw or a quads pass wants a .frag.hlsl as the fragment.");
        }

        return Result.Success();
    }

    private static Result ValidateCreate(PassCreate create, PassScope scope)
    {
        if (create.Name.Length == 0)
            return Result.Failure("a create has no name.");
        if (ResourceRegistry.IsEngineName(create.Name))
            return Result.Failure($"creates {create.Name}, which is the engine's.");
        if (create.Name.EndsWith(PassNames.Previous, StringComparison.OrdinalIgnoreCase))
            return Result.Failure($"creates {create.Name}; a name ending in Previous is a history's.");

        switch (create.Kind)
        {
            case ResourceKind.Texture when create.Scale > 0f && create.Size.Length > 0:
                return Result.Failure($"{create.Name} has a scale and a size; a texture follows the target (scale) or is fixed (size).");
            case ResourceKind.Texture when create.Scale <= 0f && create.Size.Length != 2:
                return Result.Failure($"{create.Name} needs a scale, or a size of two numbers.");
            case ResourceKind.Texture when create.Scale > 0f && scope == PassScope.Frame:
                return Result.Failure($"{create.Name} follows a render target's size, which a frame-wide pass has not got.");
            case not ResourceKind.Buffer when create.History:
                return Result.Failure($"{create.Name}: only a buffer keeps a history.");
            case ResourceKind.Volume when create.Size.Length != 3:
                return Result.Failure($"{create.Name} is a volume and needs a size of three numbers.");
            case ResourceKind.Volume when create.Levels == 0:
                return Result.Failure($"{create.Name} needs at least one level.");
            case ResourceKind.Buffer when (create.Bytes > 0) == (create.Per.Length > 0):
                return Result.Failure($"{create.Name} is sized by bytes or by a $count (per), one of the two.");
            case ResourceKind.Buffer when create.Per.Length > 0 && !FrameCounts.IsName(create.Per):
                return Result.Failure($"{create.Name} is sized per {create.Per}, which is not a count; one of {string.Join(", ", FrameCounts.Names)}.");
            case ResourceKind.Buffer when create.Stride == 0:
                return Result.Failure($"{create.Name} has a stride of 0.");
            case ResourceKind.Buffer when create.Seed.Length > 0 && !ResourceRegistry.IsEngineBuffer(create.Seed):
                return Result.Failure($"{create.Name} is seeded from {create.Seed}, which is not an engine buffer.");
        }

        if (create.Kind != ResourceKind.Buffer && (create.Seed.Length > 0 || create.Reset.Length > 0 || create.Zero))
            return Result.Failure($"{create.Name}: seed, reset and zero are a buffer's.");

        foreach (string word in create.Reset)
        {
            if (!FrameCounts.IsName(word) && !uint.TryParse(word, out _))
                return Result.Failure($"{create.Name} resets to '{word}', which is neither a number nor a $count.");
        }

        return Result.Success();
    }

    private static Result ValidateCompute(Pass pass)
    {
        if (pass.Writes.Length == 0)
            return Result.Failure("a compute pass writes something.");

        PassDispatch dispatch = pass.Dispatch;
        int ways = (dispatch.Per.Length > 0 ? 1 : 0) + (dispatch.Groups.Length > 0 ? 1 : 0) + (dispatch.Indirect.Length > 0 ? 1 : 0);
        if (ways != 1)
            return Result.Failure("dispatch says one of per, groups or indirect.");
        if (dispatch.Groups.Length is > 0 and > 3)
            return Result.Failure("dispatch.groups has up to three numbers.");
        if (dispatch.Per.Length > 0 && dispatch.Per != "view" && !dispatch.Per.StartsWith("output:", StringComparison.Ordinal) && !dispatch.Per.StartsWith("level:", StringComparison.Ordinal) && !FrameCounts.IsName(dispatch.Per))
            return Result.Failure($"dispatch per '{dispatch.Per}' is not view, output:Name, level:Name or a $count.");
        if (dispatch.Per.StartsWith("output:", StringComparison.Ordinal) || dispatch.Per.StartsWith("level:", StringComparison.Ordinal))
        {
            string name = dispatch.Per[(dispatch.Per.IndexOf(':') + 1)..];
            if (!PassNames.Writes(pass, name))
                return Result.Failure($"dispatches per {name}, which it does not write.");
        }

        return Result.Success();
    }

    private static Result ValidateFullscreen(Pass pass)
    {
        if (pass.Writes.Length != 1)
            return Result.Failure("a fullscreen pass writes exactly one texture.");

        return Result.Success();
    }

    private static Result ValidateDraw(Pass pass, PipelineStage stage)
    {
        PassDraw draw = pass.Draw;
        if (draw.Args.Length == 0 || draw.Instances.Length == 0)
            return Result.Failure("a draw names its args (indirect) and instances (vertex) buffers.");
        if (draw.Colors.Length > 4)
            return Result.Failure("a draw has at most four colour targets.");
        if (draw.Pipeline == DrawPipeline.Depth)
        {
            if (draw.Colors.Length > 0 || draw.Depth.Length == 0)
                return Result.Failure("a depth draw names a depth and no colours.");
        }
        else if (draw.Colors.Length == 0 || draw.Depth.Length == 0)
            return Result.Failure("a material draw names its colours and its depth.");

        if (stage is not (PipelineStage.Shadows or PipelineStage.Scene or PipelineStage.Transparency))
            return Result.Failure("a draw runs in the shadows, scene or transparency stage.");
        if (stage == PipelineStage.Shadows && draw.Pipeline != DrawPipeline.Depth)
            return Result.Failure("a draw in the shadows stage is a depth draw.");

        return Result.Success();
    }

    private static Result ValidateQuads(Pass pass, PipelineStage stage)
    {
        if (stage is not (PipelineStage.Scene or PipelineStage.Shadows))
            return Result.Failure("a quads pass runs in the scene or shadows stage.");
        if (pass.Reads.Length == 0)
            return Result.Failure("a quads pass reads the buffer its rows come from first.");
        if (pass.Draw.Colors.Length == 0 && pass.Draw.Depth.Length == 0)
            return Result.Failure("a quads pass names colours, a depth, or both.");
        if (pass.Draw.Colors.Length > 4)
            return Result.Failure("a quads pass has at most four colour targets.");
        if (pass.Draw.Rows.Length == 0 || (!FrameCounts.IsName(pass.Draw.Rows) && !uint.TryParse(pass.Draw.Rows, out _)))
            return Result.Failure("a quads pass says its rows: a $count or a number.");

        return Result.Success();
    }

    private static void CountReads(Pass pass, Func<string, ResourceKind?> kindOf, out int sampled, out int buffers)
    {
        (sampled, buffers) = (0, 0);
        foreach (PassRead read in pass.Reads)
        {
            if (ResourceRegistry.IsEngineBuffer(read.Name) || PassNames.CreateOf(pass, read.Name) is { Kind: ResourceKind.Buffer } || kindOf(read.Name) == ResourceKind.Buffer)
                buffers++;
            else
                sampled++;
        }
    }

    private static void CountWrites(Pass pass, Func<string, ResourceKind?> kindOf, out int textures, out int buffers)
    {
        (textures, buffers) = (0, 0);
        foreach (PassWrite write in pass.Writes)
        {
            if (ResourceRegistry.IsEngineBuffer(write.Name) || PassNames.CreateOf(pass, write.Name) is { Kind: ResourceKind.Buffer } || kindOf(write.Name) == ResourceKind.Buffer)
                buffers++;
            else
                textures++;
        }
    }

    private static Result Mismatch(string what, uint shader, int file)
    {
        return Result.Failure($"the shader uses {shader} {what}; the pass lists {file}. Every read must be used, and declared in the order read.");
    }
}
