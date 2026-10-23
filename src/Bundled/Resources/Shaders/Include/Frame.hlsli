// The frame's clocks, as Core's Frame tells them, which a renderer hands every shader that needs a time: surfaces and
// skies in their inputs, its own passes in its constants. What the world animates moves by the world's clock, which
// World.TimeScale speeds up or slows down and which stands still while the world is paused; what goes on while it is
// paused, a fade or a failure's pulse, by the real one. 16 bytes, which packs the same in a storage buffer and in a
// pass's constants.

#ifndef MAGIC_FRAME_HLSLI
#define MAGIC_FRAME_HLSLI

struct FrameConstants
{
    float worldSec;          // the world's time since the first frame
    float realSec;           // real time since the first frame
    float realDeltaSec;      // real time since the last frame, 0 on the first
    uint index;              // counts frames from 0
};

#endif
