using Magic.Contexts.Rendering;

namespace DeferredRendererGem;

/// <summary>
/// Shows a broken pass on the finished image: one band per pass that is disabled or does not fit, stacked from the top
/// of every render target, the failure magenta breathing with the pass's name across it in the engine's own bitmap
/// font (<c>Passes/FailureOverlay.frag.hlsl</c>). The glow says a pass is broken and which; the log has the rest.
/// Drawn over Ldr after the posts, so a tonemap cannot dim it, and read by a screenshot like the rest.
/// </summary>
internal static class FailureOverlay
{
    private const int MaxGlyphs = PassRawWords.IterationWord;
    private const uint GlyphDigit0 = 0;
    private const uint GlyphLetterA = 10;
    private const uint GlyphSpace = 36;

    /// <summary>
    /// One fullscreen draw per failed pass of the listing, with the target's first view's constants. Returns the draws recorded.
    /// </summary>
    public static int Record(RenderContext ctx, RenderCommands commands, FrameTargets targets, in FrameConstants frame)
    {
        PipelineState pipeline = ctx.Pipeline;
        if (!pipeline.FailureOverlay.IsValid)
            return 0;

        int band = 0;
        foreach (List<PassState> stage in pipeline.Stages)
        {
            foreach (PassState pass in stage)
            {
                if (pass.Error is null && !pipeline.Unfit.Contains(pass.Id))
                    continue;

                PassConstants constants = PassConstants.Of(frame);
                string name = System.IO.Path.GetFileNameWithoutExtension(pass.Path).ToUpperInvariant();
                int count = Math.Min(name.Length, MaxGlyphs);
                for (int i = 0; i < count; i++)
                    constants.Raw[i] = Glyph(name[i]);
                constants.SetIteration((uint)band, (uint)count);
                constants.Raw[PassRawWords.IterationWord + 2] = targets.Width;
                constants.Raw[PassRawWords.IterationWord + 3] = targets.Height;

                commands.Push(GpuStage.Fragment, constants);
                commands.BeginRenderPass(targets.Ldr.Texture, GpuLoad.Load);
                commands.BindPipeline(pipeline.FailureOverlay);
                commands.Draw(3);
                commands.EndRenderPass();
                band++;
            }
        }

        return band;
    }

    private static uint Glyph(char letter)
    {
        if (letter is >= 'A' and <= 'Z')
            return GlyphLetterA + (uint)(letter - 'A');
        if (letter is >= '0' and <= '9')
            return GlyphDigit0 + (uint)(letter - '0');

        return GlyphSpace;
    }
}
