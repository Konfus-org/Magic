# Curves

A curve is a value that changes along something, such as height, distance or time. A `.curve` file is a list of keys,
each a value at a place, in order. The `Curve` type documents the format, and the files here are examples.

- Between two keys, the value changes in a straight line. Before the first key and after the last, it holds that
  key's value.
- A material or an effect can take a curve. The weather's fog takes one for how it thins with height.
- Shaders read a curve through [Include/Curve.hlsli](../Shaders/Include/Curve.hlsli). Code reads one with Core's
  `CurveExtensions`.
- On the GPU, a curve is sampled at a fixed number of evenly spaced points between its first and last keys. Detail
  finer than that is smoothed out.

A curve param left out uses `Default.curve`, which is 1 everywhere.
