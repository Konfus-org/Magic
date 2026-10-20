# Curves

A curve is a value that changes along something, a height, a distance or a time: a `.curve` file of keys, each a value
where it is along the curve, in order.

```json
{ "keys": [ { "at": 0, "value": 1 }, { "at": 30, "value": 0.4 }, { "at": 150, "value": 0 } ] }
```

Between two keys the value goes in a straight line from one to the other; before the first key it is the first's, and
after the last the last's, so a curve says what it is everywhere. A curve with one key is that value everywhere, as
`Default.curve` is 1, which a curve param left out takes.

A pass takes a curve as a param, `{ "curve": { "id": 3363 } }`, for a `Curve` member of its `PassParams`, and reads it
with `CurveAt` and `CurveIntegral` ([Shaders/Include/Curve.hlsli](../Shaders/Include/Curve.hlsli)). The renderer packs
it as where its first and last keys are and its value at 14 places spread evenly from the first key to the last, both
included, so a key between those places is rounded off a little, and a curve keeps its shape best with its keys spread
over its length. Code reads one with `At` (Core's `CurveExtensions`).
