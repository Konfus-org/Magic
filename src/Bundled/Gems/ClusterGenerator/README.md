# ClusterGenerator

Models' clusters and cards: provides `ILoadPostProcessor<Model>`, giving each model its `Clusters` and its `Cards` as it
loads, and keeps them in the cache (`Caches`), providing the `ICache<T>` that writes them. Its one setting, the card
height, is documented on `ClusterGeneratorSettings` and shown in the settings window; what it is to the cache is
under [Cache](#cache).

## Clusters

Core's `ClusterHierarchy`, after Karis, "Nanite: A Deep Dive" (SIGGRAPH 2021): the model's triangles in small
clusters, at every level from finest to coarsest at once, which a renderer cuts through per cluster as it draws. The
clustering, the grouping and the simplifying are meshoptimizer's, a native library the gem declares; what the levels
are, their errors and spheres and what is kept between them, is the gem's own, as the renderer depends on it.

- Positions are put on a grid first, the same for the whole model: its farthest vertex from the origin along an axis
  over a fixed number of steps, so a renderer packs a position into a few bits an axis without moving it, and a
  vertex two clusters share is the same in both. Every level indexes these vertices: the simplifier keeps a subset of
  them as they were, so nothing leaves the grid, and every vertex keeps its normal and uv at every level.
- The finest clusters are compact pieces of each mesh, each of one material slot, up to the cluster's size in triangles
  and vertices. A large mesh is cut into chunks along a spatial sort first and its chunks are clustered at once, so a
  chunk's edge is one more border between clusters at the finest level; the levels above group across it.
- Then, level after level, neighbouring clusters are grouped, by the places they share and by where they are, each
  group is simplified to half its triangles with every place it shares with another group locked, and split into
  clusters again, every group at once. Places, not vertices, decide what meets, so clusters that meet only at a seam,
  a material's edge or a chunk's, are grouped and locked as neighbours. A kept triangle keeps its corners' material,
  and a group's triangles are split into clusters slot by slot.
- The mesh's own open border is locked as a group's is. A leaf is a quad open all round: collapsing it moves the
  surface by no more than the leaf is wide, so its error says nothing of all it covered, and a tree whose leaves went
  that way would stand bare from a few metres off. Its leaves stay as they are until its cards take over.
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
loads, beside its clusters: each side looking back at the mesh straight on, every side at once into a cell of its
own, several samples to a texel for soft edges, and the colours spread a few texels past the mesh's edge within the
cell, so filtering there reads the mesh. They are textures handed out with the
assets, under handles made from the model, the mesh and the atlas, so whatever draws loads them as it loads any
texture, for as long as the model, or anything holding its cards, holds them.

A model much less tall than its largest side lies flat, as a ground plane does, so a card, which stands up, would not
stand in for it: it has no cards, and keeps its coarsest clusters however far.

## Cache

A model's clusters and cards are kept under its id, its cards' atlases with them, made from its version, a hash of its
file and its `.meta`, and from the card height: a model is made once, and again when any of them changes, on disk or
while it runs, so a changed card height makes every model again as it loads. Changing how they are made raises the
cache's version, which makes every entry dirty.

Building the clusters and drawing the cards run on workers as the model loads, its chunks, groups and card sides
spread over every core, so the first load of a large model takes longer, and every load after it, from any checkout
on the machine, reads the cache.
