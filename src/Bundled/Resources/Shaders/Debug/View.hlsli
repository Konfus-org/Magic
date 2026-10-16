// What every debug view shares (Passes/Debug/, a view a pass, each named for what it shows, drawn into DebugView in
// place of the finished image): the view's pixel under a fullscreen fragment, a faint grey of the albedo an overlay is
// drawn on so what it marks can be placed and a value or colour is shaded by it so the scene's shape reads whatever the
// value, colour ramps, a curve that shows an HDR value as the finished image roughly would, and the flat teal a view
// shows where the frame has nothing for it, as an effect the world does not draw. A view draws black where nothing was
// drawn, and never magenta, which says that something failed.

#ifndef MAGIC_DEBUG_VIEW_HLSLI
#define MAGIC_DEBUG_VIEW_HLSLI

#include "Include/Frame.hlsli"
#include "Include/Fullscreen.hlsli"

static const float3 NothingColor = float3(0.0, 0.25, 0.3);

uint2 ViewPixelOf(float2 uv)
{
    return min((uint2)(uv * ViewSize), (uint2)ViewSize - 1u);
}

// The albedo's brightness at a pixel, dimmed, as the ground an overlay is drawn on; dark where nothing was drawn.
float3 OverlayBase(uint2 pixel)
{
    float3 albedo = Albedo.Load(int3(pixel, 0)).rgb;
    float grey = dot(albedo, float3(0.2126, 0.7152, 0.0722));
    return (0.15 + 0.35 * grey).xxx;
}

// A value, 0 to 1, as grey shaded by the scene at the pixel: the scene in dark grey at 0 and in light grey at 1, so
// its shape reads whatever the value.
float3 Shaded(float value, uint2 pixel)
{
    float3 base = OverlayBase(pixel);
    return lerp(0.3 * base, 0.35 + base, value);
}

// A colour lifted by a quarter of the scene at the pixel, so the scene's shape reads through black.
float3 Lifted(float3 color, uint2 pixel)
{
    return color + 0.25 * OverlayBase(pixel);
}

// A heat ramp, 0 to 1: black, blue, cyan, green, yellow, red, white.
float3 HeatColor(float t)
{
    static const float3 Stops[7] =
    {
        float3(0.0, 0.0, 0.0), float3(0.0, 0.0, 1.0), float3(0.0, 1.0, 1.0), float3(0.0, 1.0, 0.0),
        float3(1.0, 1.0, 0.0), float3(1.0, 0.0, 0.0), float3(1.0, 1.0, 1.0),
    };
    float at = saturate(t) * 6.0;
    uint stop = min((uint)at, 5u);
    return lerp(Stops[stop], Stops[stop + 1u], at - (float)stop);
}

// A hue, 0 to 1 round the wheel, lightened so every one reads: for telling things apart.
float3 HueColor(float hue)
{
    float3 color = saturate(abs(frac(hue + float3(0.0, 2.0 / 3.0, 1.0 / 3.0)) * 6.0 - 3.0) - 1.0);
    return lerp(float3(1.0, 1.0, 1.0), color, 0.7);
}

// A hue for an id, ids near each other far apart on the wheel.
float3 IdColor(uint id)
{
    return HueColor((float)((id * 2654435761u) >> 8) / 16777216.0);
}

// An HDR colour roughly as the finished image shows it.
float3 Tonemapped(float3 color)
{
    return color / (1.0 + color);
}

#endif
