// What the renderer hands a sky shader (Include/Sky.hlsli) from the frame's constants: its sun and its weather, the
// same to every sky draw, so the sky seen, the sky captured and the sky's shadow on the world are one sky.

#ifndef MAGIC_SKY_FRAME_HLSLI
#define MAGIC_SKY_FRAME_HLSLI

#include "Include/Frame.hlsli"
#include "Include/Sky.hlsli"

// The sky's inputs along a direction; a capture is drawn coarser. dither is the pixel's (Include/Sky.hlsli).
SkyInputs SkyInputsOf(float3 direction, bool isCapture, float dither)
{
    SkyInputs inputs;
    inputs.direction = direction;
    inputs.cameraPosition = CameraPosition;
    inputs.time = Time;
    inputs.sunDirection = SunDirection;
    inputs.sunIlluminance = SunIlluminance;
    inputs.sunAngularRadius = SunAngularRadius;
    inputs.wind = Weather.Wind;
    inputs.coverage = Weather.Coverage;
    inputs.density = Weather.Density;
    inputs.cloudColor = Weather.CloudColor;
    inputs.cloudShadow = Weather.CloudShadow;
    inputs.isCapture = isCapture;
    inputs.dither = dither;
    return inputs;
}

#endif
