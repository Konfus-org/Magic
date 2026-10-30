// What every pass knows of the frame it draws, whatever renderer runs it: the frame's clocks, as Core's Frame tells
// them, the pass header, which a renderer hands each pass ahead of its own constants, and the triangle a fullscreen
// draw covers its target with. What the world animates moves by the world's clock, which World.TimeScale speeds up or
// slows down and which stands still while the world is paused; what goes on while it is paused, a fade or a failure's
// pulse, by the real one.

module "Core/Pass";

__exported import Core.Heap;

public struct FrameConstants
{
    float worldSec;          // the world's time since the first frame
    float realSec;           // real time since the first frame
    float realDeltaSec;      // real time since the last frame, 0 on the first
    uint index;              // counts frames from 0
}

// The first of every pass's constants, a uniform parameter of its entry point ahead of the pass's own: the buffer of
// the frame's constants, which start with its FrameConstants and go on with what the renderer keeps for the frame, and
// the buffer of the view's, the renderer's own, for a pass drawn for a view.
public struct PassHeader
{
    BufferRef frame;
    BufferRef view;

    public property FrameConstants clocks
    {
        get { return frame.Load<FrameConstants>(0u); }
    }
}

// What a fullscreen draw's fragment stage takes: uv has the texture convention, (0, 0) top left.
public struct FullscreenVaryings
{
    float2 uv : TEXCOORD0;
    float4 position : SV_Position;
}

// One triangle covering the target, made from the vertex id alone, with no vertex buffer.
[shader("vertex")]
FullscreenVaryings DrawFullscreen(uint vertexId : SV_VulkanVertexID)
{
    // (0, 0), (2, 0), (0, 2): the corner the triangle covers the screen from, in uv.
    float2 uv = float2((float)((vertexId << 1u) & 2u), (float)(vertexId & 2u));

    FullscreenVaryings output;
    output.uv = uv;
    output.position = float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
    return output;
}
