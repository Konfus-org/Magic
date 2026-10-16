// A curve, as a pass's Curve param packs it (Core's Curve, packed by the renderer's ParamLayout): where its first and
// last keys are, then its value at CurveSamples places evenly from the one to the other, joined by straight lines and
// held past either end, as the curve itself is.

#ifndef MAGIC_CURVE_HLSLI
#define MAGIC_CURVE_HLSLI

static const uint CurveSamples = 14u;

struct Curve
{
    float4 rows[4];
};

// The curve's value at its i-th packed place.
float CurveSample(Curve curve, uint i)
{
    uint at = 2u + i;
    float4 row = curve.rows[at / 4u];
    uint lane = at % 4u;
    return lane == 0u ? row.x : lane == 1u ? row.y : lane == 2u ? row.z : row.w;
}

// Where a place is among the packed places, 0 to CurveSamples - 1, held at either end.
float CurveIndex(Curve curve, float place)
{
    float start = curve.rows[0].x;
    float end = curve.rows[0].y;
    return end > start ? saturate((place - start) / (end - start)) * (float)(CurveSamples - 1u) : 0.0;
}

// The curve's value at a place.
float CurveAt(Curve curve, float place)
{
    float index = CurveIndex(curve, place);
    uint low = min((uint)index, CurveSamples - 2u);
    return lerp(CurveSample(curve, low), CurveSample(curve, low + 1u), index - (float)low);
}

// The area under the curve from its first key to a place, below 0 before it, the ends' values held past them: so the
// area between two places, CurveIntegral, is the difference of theirs.
float CurveArea(Curve curve, float place)
{
    float start = curve.rows[0].x;
    float end = curve.rows[0].y;
    float first = CurveSample(curve, 0u);
    [branch] if (end <= start || place <= start)
    {
        return first * (place - start);
    }

    float step = (end - start) / (float)(CurveSamples - 1u);
    float index = CurveIndex(curve, place);
    uint whole = min((uint)index, CurveSamples - 1u);
    float area = 0.0;
    float before = first;
    [loop] for (uint i = 1u; i <= whole; i++)
    {
        float after = CurveSample(curve, i);
        area += 0.5 * (before + after) * step;
        before = after;
    }

    [branch] if (whole < CurveSamples - 1u)
    {
        float part = index - (float)whole;
        float after = CurveSample(curve, whole + 1u);
        area += (before + 0.5 * (after - before) * part) * part * step;
    }

    return area + CurveSample(curve, CurveSamples - 1u) * max(place - end, 0.0);
}

// The area under the curve from one place to another, negative when to is before from.
float CurveIntegral(Curve curve, float from, float to)
{
    return CurveArea(curve, to) - CurveArea(curve, from);
}

#endif
