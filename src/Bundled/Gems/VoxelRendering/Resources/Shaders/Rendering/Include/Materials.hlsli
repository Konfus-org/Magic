// The constants of a draw of the world's materials (Rendering/Materials/), the script's MaterialConstants, the same to
// every surface's pipeline and both its stages.

#ifndef MAGIC_RENDERING_MATERIALS_HLSLI
#define MAGIC_RENDERING_MATERIALS_HLSLI

#include "Rendering/Include/View.hlsli"

struct MaterialConstants
{
    uint view;
    uint pieces;             // the view's pieces (Pieces.hlsli), an instance of the draw each
    uint tiles;              // the resolve: the tile list, an instance of the draw each
    uint visibility;         // the resolve: the visibility buffer
};

CONSTANTS(MaterialConstants, Draw);

#endif
