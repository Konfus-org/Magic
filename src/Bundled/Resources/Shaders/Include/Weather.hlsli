// The world's weather, as its Weather effects say, which a renderer hands every shader that needs it: surfaces and
// skies in their inputs, its own passes in its constants. Still, clear and with no fog without any. Every row is 16
// bytes, which packs the same in a storage buffer and in a pass's constants.

#ifndef MAGIC_WEATHER_HLSLI
#define MAGIC_WEATHER_HLSLI

#include "Include/Curve.hlsli"

struct WeatherConstants
{
    float3 wind;             // metres per second: its direction and strength
    float coverage;          // how much of the sky clouds cover, 0 clear to 1 overcast
    float3 cloudColor;       // the clouds where the sun lights them, linear
    float density;           // how thick they are, 0 wisps to 1 thick
    float3 cloudShadow;      // the clouds in their own shade and underneath, linear
    float fog;               // the share of the light the fog puts out a metre where its falloff is 1; 0, none
    float3 fogColor;         // the fog's tint, times the light that reaches it, linear
    float fogStart;          // metres from the camera before the fog begins
    Curve fogFalloff;        // the fog's share by metres above the world's origin
};

#endif
