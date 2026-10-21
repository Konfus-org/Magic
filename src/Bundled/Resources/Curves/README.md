# Curves

A curve is a value that changes along something, a height, a distance or a time: a `.curve` file of keys, each a value
where it is along the curve, in order. The keys are the `Curve` type's properties, documented on it, and the files
beside this README are the examples.

Between two keys the value goes in a straight line from one to the other; before the first key it is the first's, and
after the last the last's, so a curve says what it is everywhere. A curve with one key is that value everywhere, as
`Default.curve` is 1, which a curve param left out takes.

A pass takes a curve as a param, for a curve member of its params block, and the weather takes one for its fog's falloff
by height; a shader reads the curve's value at a place and its integral between two through the curve header
([Shaders/Include/Curve.hlsli](../Shaders/Include/Curve.hlsli)). The renderer packs it as where its first and last keys
are and its value at a fixed number of places spread evenly from the first key to the last, both included, so a key
between those places is rounded off a little, and a curve keeps its shape best with its keys spread over its length.
Code reads one through Core's extension on the type.
