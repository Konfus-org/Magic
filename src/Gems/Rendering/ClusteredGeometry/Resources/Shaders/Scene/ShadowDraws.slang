// What the shadow cull leaves for the shadow draws: a list of the clusters and impostors that each shadow view draws,
// and the draws' regions. The pass header's x is the camera's frame and its y the heap id of the shadow constants.
module "Scene/ShadowDraws";

import Core.Heap;
import Core.Pass;

// What the shadow passes read beyond the frame, C#'s ShadowCullConstants, 28 bytes.
public struct ShadowCullConstants
{
    // The heap id of the shadow views and how many there are.
    public uint views;
    public uint viewCount;
    // The heap ids of the shadow list, the draws and the counts.
    public uint shadowClusters;
    public uint drawArguments;
    public uint counts;
    // The rows the shadow list holds, and the draws each region holds.
    public uint maxShadowClusters;
    public uint maxShadowDraws;
}

public ShadowCullConstants GetShadowCull() { return LoadConstants<ShadowCullConstants>(GetPassHeader().y); }

// One cluster that a shadow view draws, 12 bytes: its instance, its cluster, or ShadowImpostor for the instance's
// impostor, and the row of the shadow views it draws into.
public struct ShadowCluster
{
    public uint instance;
    public uint cluster;
    public uint view;
}

public static const uint ShadowImpostor = 0xFFFFFFFFu;

// The regions of the shadow draws, C#'s ShadowDrawRegion, a pipeline each: the clusters of single-sided and of
// double-sided materials, the same two of the instances in the impostor handoff, which draw with the dither, and the
// impostors. Each region is counted at word region * 4 of the counts buffer, and the shadow list's rows at
// CountShadowClusters.
public enum ShadowDrawRegion
{
    SingleSided,
    DoubleSided,
    Fading,
    FadingDoubleSided,
    Impostors,
}

public static const uint ShadowDrawRegions = 5u;

public static const uint CountShadowClusters = ShadowDrawRegions * 4u;

public ShadowCluster LoadShadowCluster(uint index)
{
    ArrayRef<ShadowCluster> list = { GetShadowCull().shadowClusters };
    return list[index];
}
