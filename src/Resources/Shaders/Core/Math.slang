// Numbers every module needs: constants, the small powers and extremes the intrinsics lack, guarded normalisation,
// and the reversed depth every pass reads. Depth is 1 at the near plane and 0 at infinity, from a projection with no
// far plane, so the near plane alone turns a depth back into metres.
module "Core/Math";

public static const float Pi = 3.14159265358979f;
public static const float TwoPi = 6.28318530717959f;
public static const float HalfPi = 1.57079632679490f;
public static const float InvPi = 0.318309886183791f;

// The least length a vector is normalised at, and the least value a division is guarded with.
public static const float Epsilon = 1e-6f;

public T Pow2<T : IFloat>(T x) { return x * x; }

public T Pow3<T : IFloat>(T x) { return x * x * x; }

public T Pow4<T : IFloat>(T x) { T x2 = x * x; return x2 * x2; }

public T Pow5<T : IFloat>(T x) { T x2 = x * x; return x2 * x2 * x; }

public float Max3(float a, float b, float c) { return max(a, max(b, c)); }

public float Max3(float3 v) { return max(v.x, max(v.y, v.z)); }

// v as a unit vector, or fallback when v is too short to have a direction.
public float3 NormalizeSafe(float3 v, float3 fallback)
{
    float length2 = dot(v, v);
    return length2 > Epsilon * Epsilon ? v * rsqrt(length2) : fallback;
}

// v as a unit vector, or zero when v is too short to have a direction.
public float3 NormalizeOrZero(float3 v) { return NormalizeSafe(v, float3(0.0)); }

// Whether a sphere touches the axis-aligned box from low to high.
public bool IsSphereTouchingBox(float3 center, float radius, float3 low, float3 high)
{
    float3 outside = max(max(low - center, center - high), 0.0);
    return dot(outside, outside) <= radius * radius;
}

// Metres from the camera plane to what a reversed depth is, given the near plane in metres. Infinity at depth 0.
public float ToViewDepth(float reversedDepth, float nearMetres) { return nearMetres / max(reversedDepth, 1e-20f); }

// The reversed depth of a point viewDepth metres from the camera plane, given the near plane in metres.
public float ToReversedDepth(float viewDepth, float nearMetres) { return nearMetres / max(viewDepth, Epsilon); }

// The centre of a texel as texture coordinates in [0, 1].
public float2 GetTexelCenterUv(uint2 texel, uint2 size) { return (float2(texel) + 0.5) / float2(size); }

// The texel of a texture size texels across and down that texture coordinates in [0, 1] fall in.
public uint2 GetTexelAt(float2 uv, uint2 size) { return min(uint2(uv * float2(size)), size - 1u); }

// Whether texture coordinates are in [0, 1], on the picture.
public bool IsOnScreen(float2 uv) { return all(saturate(uv) == uv); }

// Texture coordinates in [0, 1] as clip-space xy in [-1, 1], y up.
public float2 ToClip(float2 uv) { return float2(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0); }

// Clip-space xy in [-1, 1], y up, as texture coordinates in [0, 1].
public float2 ToUv(float2 clip) { return float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5); }
