// One card written into the atlases: the card's visibility buffer, GBuffer and depth, drawn along its axis with the
// model at the origin into the card's size at the targets' top left, copied to the card's place, covered where a
// pixel holds a surface. The pass header's y is the card.
module "CaptureCard";

import Core.Heap;
import Core.Pass;
import Core.Frame;
import Scene.Cards;

[shader("compute")]
[numthreads(8, 8, 1)]
void CaptureCard(uint3 thread : SV_DispatchThreadID)
{
    FrameConstants frame = GetFrame();
    PassHeader header = GetPassHeader();
    GpuCard card = LoadCard(frame.cards, header.y);
    if (any(thread.xy >= GetCardSize(card)))
        return;

    uint2 texel = GetCardOrigin(card) + thread.xy;
    StorageRef albedoAtlas = { frame.cardAlbedo, 0u };
    StorageRef normalAtlas = { frame.cardNormal, 0u };
    StorageRef depthAtlas = { frame.cardDepth, 0u };

    UintTextureRef visibility = { frame.visibility };
    if (visibility.Load(thread.xy, 0u) == 0u)
    {
        albedoAtlas.Store(texel, float4(0.0));
        normalAtlas.Store(texel, float4(0.5, 0.5, 0.0, 0.0));
        depthAtlas.Store(texel, float4(0.0));
        return;
    }

    TextureRef baseTexture = { frame.gbufferBase };
    TextureRef normalTexture = { frame.gbufferNormal };
    TextureRef depthTexture = { frame.depth };
    albedoAtlas.Store(texel, float4(baseTexture.Load(thread.xy, 0u).rgb, 1.0));
    normalAtlas.Store(texel, float4(normalTexture.Load(thread.xy, 0u).xy, 0.0, 0.0));
    depthAtlas.Store(texel, float4(depthTexture.Load(thread.xy, 0u).x, 0.0, 0.0, 0.0));
}
