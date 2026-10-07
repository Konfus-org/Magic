// The brick job of a brick pass (Gi/BrickClear.comp.hlsl, Gi/BrickBuild.comp.hlsl): the pass runs once per job
// the meshes queued this frame (each: bricks), and the job is the row of the jobs buffer at PassIteration().
// Needs `StructuredBuffer<GpuBrickJob> BrickJobs` declared by the shader.

#ifndef MAGIC_GI_BRICK_HLSLI
#define MAGIC_GI_BRICK_HLSLI

GpuBrickJob BrickJobOfPass()
{
    return BrickJobs[PassIteration()];
}

#endif
