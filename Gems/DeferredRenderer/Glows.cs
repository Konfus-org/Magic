using Magic.Contexts.Rendering;
using Magic.Extensions;

namespace DeferredRendererGem;

/// <summary>
/// The glows (<see cref="Magic.Contexts.Components.Glow"/>), as commands: every one in the world is a row of one buffer,
/// uploaded whole each frame, and one draw per view turns the rows into camera-facing disks in the gbuffer, six
/// vertices each from the vertex id alone. They are emission only, so the lighting passes them by.
/// </summary>
internal static class Glows
{
    private const uint VerticesPerGlow = 6; // Glow.vert.hlsl: two triangles

    /// <summary>
    /// Compiles the glow shaders into the pipeline that draws them into the gbuffer.
    /// </summary>
    public static GpuPipeline Create(RenderContext ctx)
    {
        CompiledShader vertex = Shaders.CompileBuiltIn(ctx, "Lighting/Glow.vert.hlsl", GpuStage.Vertex);
        CompiledShader fragment = Shaders.CompileBuiltIn(ctx, "Lighting/Glow.frag.hlsl", GpuStage.Fragment);
        return ctx.Gpu.CreatePipeline(new PipelineDesc(vertex, fragment, GBuffer.ColorFormats) { Depth = ctx.Gpu.DepthFormat, Cull = GpuCull.None });
    }

    /// <summary>
    /// The frame's glows into their buffer, all of them.
    /// </summary>
    public static void Upload(RenderContext ctx, ReadOnlySpan<GpuGlow> glows)
    {
        ctx.GlowCount = (uint)glows.Length;
        if (glows.IsEmpty)
            return;

        ctx.Glows.Ensure(ctx.Gpu, (uint)(glows.Length * GpuGlow.Size));
        ctx.Gpu.Upload(ctx.Glows.Handle, 0, glows);
    }

    /// <summary>
    /// Draws the glows into one view. Inside the gbuffer's render pass. Returns the draw calls recorded.
    /// </summary>
    public static int Record(RenderContext ctx, RenderCommands commands, in ViewPlan view)
    {
        if (ctx.GlowCount == 0)
            return 0;

        commands.SetViewport(view.Rect);
        commands.SetScissor(view.Rect);
        commands.Push(GpuStage.Vertex, view.Constants);
        commands.Push(GpuStage.Fragment, view.Constants);
        commands.BindPipeline(ctx.GlowPipeline);
        commands.BindStorageBuffers(GpuStage.Vertex, 0, [ctx.Glows.Handle]);
        commands.Draw(ctx.GlowCount * VerticesPerGlow);

        return 1;
    }
}
