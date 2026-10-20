# ClusterGenerator

Models' clusters and cards: provides `ILoadPostProcessor<Model>`, giving each model its `Clusters` and its `Cards` as it
loads, and keeps them in the cache (`Caches`), providing the `ICache<T>` that writes them. Its one setting, the card
height, is documented on `ClusterGeneratorSettings` and shown in the settings window; what it is to the cache is
under [Cache](#cache).

## Clusters

Core's `ClusterHierarchy`, after Karis, "Nanite: A Deep Dive" (SIGGRAPH 2021): the model's triangles in small
clusters, at every level from finest to coarsest at once, which a renderer cuts through per cluster as it draws.

- Positions are put on a grid first, the same for the whole model: its farthest vertex from the origin along an axis
  over a fixed number of steps, so a renderer packs a position into a few bits an axis without moving it, and a
  vertex two clusters share is the same in both.
- The finest clusters are the model's triangles, each cluster of one material slot, grown from the first triangle left
  along a Morton curve to the triangle beside it that brings the fewest new vertices, nearest its middle, up to the
  cluster's size in triangles and vertices.
- Then, level after level, the few clusters that share the most border are grouped, each group is simplified to
  half its triangles with every vertex it shares with another group kept where it is, and split into clusters again.
  Materials are kept by triangle, as the simplifier keeps each corner's vertex, so a group can mix them. A vertex
  that moved takes the normal of the triangles it is now a corner of, shared with the corners it is shaded smoothly
  with, so a smooth model stays smooth and a flat one flat at every level; the kept border keeps its normals, so the
  clusters either side carry the same ones along it.
- A group's error is the most of what its simplification moved the surface and what its clusters had, and its sphere
  encloses theirs: the clusters made from it take both as theirs, and the clusters it was made from as their parent's.
  Neither shrinks from a level to the next, so a renderer picks each cluster alone, and the clusters it picks still
  meet.
- A group that keeps most of its triangles has nothing left to give: its clusters are the coarsest of their part of
  the model, drawn however far away it is, until its cards take over. Its border with its neighbours is kept at every
  level after, so what is made beside it still meets it.

## Cards

Past its clusters, from where the model is the card height of the view tall, each mesh is a card: Core's `Card`, a
quad that turns about +Y to face the camera and shows the mesh as it was seen from the nearest of several sides,
shaded with the mesh's own material, its uv, its normal and how far behind the card the mesh is read from two atlases,
so each pixel is lit, shadowed and depth tested where the mesh would be. Its atlases are drawn on the CPU as the model
loads: each side looking back at the mesh straight on, several samples to a texel for soft edges, and the colours
spread a few texels past the mesh's edge, so filtering there reads the mesh. They are textures handed out with the
assets, under handles made from the model, the mesh and the atlas, so whatever draws loads them as it loads any
texture, for as long as the model, or anything holding its cards, holds them.

A model much less tall than its largest side lies flat, as a ground plane does, so a card, which stands up, would not
stand in for it: it has no cards, and keeps its coarsest clusters however far.

## Cache

A model's clusters and cards are kept under its id, its cards' atlases with them, made from its version, a hash of its
file and its `.meta`, and from the card height: a model is made once, and again when any of them changes, on disk or
while it runs, so a changed card height makes every model again as it loads. Changing how they are made raises the
cache's version, which makes every entry dirty.

Building the clusters and drawing the cards run on the worker the model loads on, so the first load of a large model
takes longer, and every load after it reads the cache.
