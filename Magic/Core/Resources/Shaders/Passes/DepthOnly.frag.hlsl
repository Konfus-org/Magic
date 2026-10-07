// An empty fragment stage, for a pipeline that only writes depth: there is no colour target, the rasteriser writes
// the depth, and a graphics pipeline still needs a fragment stage. No bindings, no constants.

void main(float4 position : SV_Position)
{
}
