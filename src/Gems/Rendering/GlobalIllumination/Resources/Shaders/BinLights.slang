// The light grid that GetDirectLight reads: each frame its words are cleared, then each light sets its bit in every cell
// that its reach touches. The pass header's x is the frame's buffer, which holds the lights and the probe grid.
module "BinLights";

import Core.Heap;
import Core.Frame;
import Core.Math;
import Scene.Lights;
import Light.Probes;
import Light.Indirect;
import Traced;

static const uint Threads = 64u;

// A thread a word of the light grid: it holds no light.
[shader("compute")]
[numthreads(Threads, 1, 1)]
void ClearLightCells(uint3 thread : SV_DispatchThreadID)
{
    ProbeGridConstants grid = GetProbeGrid();
    uint3 size = GetLightGridSize(grid);
    if (thread.x >= size.x * size.y * size.z * LightBuckets)
        return;

    ArrayRef<uint> cells = { grid.probes };
    cells[GetLightCellOffset(grid, int3(0)) + thread.x] = 0u;
}

// A group a light: its threads share the cells of the box around its reach and set its bit in each that the reach
// touches. A cell at a side of the grid reaches on without end, so its box does too.
[shader("compute")]
[numthreads(Threads, 1, 1)]
void BinLights(uint3 group : SV_GroupID, uint index : SV_GroupIndex)
{
    Lights lights = GetLights();
    if (group.x >= min(lights.count, MaxLights))
        return;

    ProbeGridConstants grid = GetProbeGrid();
    GpuLight light = LoadLight(lights.table, group.x);
    // A little more than the reach, so a point that the lookup finds in reach is in a cell with the bit.
    float reach = GetLightReach(light) + 0.01;
    int3 low = GetLightCell(grid, light.position - reach);
    int3 high = GetLightCell(grid, light.position + reach);
    int3 first = GetFirstLightCell(grid);
    int3 last = int3(GetLightGridSize(grid)) - 1;
    float3 size = GetLightCellSize(grid);
    uint3 span = uint3(high - low + 1);
    BufferRef cells = { grid.probes };
    for (uint i = index; i < span.x * span.y * span.z; i += Threads)
    {
        int3 cell = low + int3(i % span.x, (i / span.x) % span.y, i / (span.x * span.y));
        float3 cellLow = select(cell == 0, float3(-1e30), float3(cell + first) * size);
        float3 cellHigh = select(cell == last, float3(1e30), float3(cell + first + 1) * size);
        if (!IsSphereTouchingBox(light.position, reach, cellLow, cellHigh))
            continue;

        // Each light visits each cell once, so the add sets its bit as an or would.
        uint word = GetLightCellOffset(grid, cell) + group.x / 32u;
        cells.InterlockedAdd(word * 4u, 1u << (group.x % 32u));
    }
}
