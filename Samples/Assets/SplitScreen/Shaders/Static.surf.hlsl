// TV static: the snow a set shows with no signal, new every frame of a 30 Hz picture, with faint scanlines
// creeping down and a brighter band rolling up the screen. It only emits: the picture is light, not paint. The
// glow param is the screen's average light, which is what the GI sees, so the flicker lights the room around it.

#include "Include/Surface.hlsli"

static const float PictureRate = 30.0;  // new snow this many times a second
static const float ScanlineCount = 220.0;
static const float ScanlineSpeed = 0.03; // screens a second the scanlines creep down
static const float BandSeconds = 3.5;   // one pass of the bright band up the screen
static const float BandWidth = 0.12;    // of the screen's height

struct MaterialParams
{
    Color glow : GiEmissive = Color(0.55, 0.6, 0.75, 1.0);
    float cells = 140.0;    // snow cells across the screen
    float contrast = 1.8;   // above 1 darkens the snow between the bright flecks
};

// A well mixed 32-bit value for one cell of one picture.
uint HashCell(uint2 cell, uint picture)
{
    uint hash = (cell.x * 73856093u) ^ (cell.y * 19349663u) ^ (picture * 83492791u);
    hash ^= hash >> 13u;
    hash *= 0x5bd1e995u;
    hash ^= hash >> 15u;
    return hash;
}

Surface EvaluateSurface(SurfaceInputs input, MaterialParams material)
{
    Surface surface = DefaultSurface(input);

    uint picture = (uint)(input.time * PictureRate);
    uint2 cell = (uint2)(saturate(input.uv) * material.cells);
    float snow = pow((float)(HashCell(cell, picture) & 0xFFFFu) / 65535.0, material.contrast);

    float scanline = 0.8 + (0.2 * sin((input.uv.y + (input.time * ScanlineSpeed)) * ScanlineCount * Pi));
    float bandCenter = 1.0 - frac(input.time / BandSeconds);
    float band = 1.0 + (0.7 * saturate(1.0 - (abs(input.uv.y - bandCenter) / BandWidth)));

    surface.baseColor = float3(0.0, 0.0, 0.0);
    surface.emissive = material.glow.rgb * (2.0 * snow * scanline * band);
    return surface;
}
