# ClusterGenerator

Builds each model's clusters and cards as it loads. Provides `ILoadPostProcessor<Model>`.

Clusters are the model's triangles in small groups, at every level of detail from finest to coarsest. A renderer picks
a level per cluster. Cards are flat quads that turn to face the camera and stand in for each mesh far away. Clustering
and simplifying use [meshoptimizer](https://github.com/zeux/meshoptimizer), a native library
([Native libraries](../README.md#native-libraries)).

Clusters and cards are kept in the cache ([The cache](../../../Core/README.md#the-cache)). A model is built again only
when its file, its `.meta` or the card height changes. The card height is on `ClusterGeneratorSettings` and shows in
the settings window (F4). It sets how small a model gets on screen before its cards take over.

- The first load of a large model is slow. Later loads read the cache.
- A model that is very flat, such as a ground plane, gets no cards.
