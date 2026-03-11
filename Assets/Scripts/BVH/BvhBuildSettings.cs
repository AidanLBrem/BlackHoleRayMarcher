using System;
using UnityEngine;

[Serializable]
public struct BvhBuildSettings
{
    [Min(1)] public int maxLeafSize;
    [Min(1)] public int maxDepth;
    [Min(2)] public int numBins;

    public static BvhBuildSettings Default => new BvhBuildSettings
    {
        maxLeafSize = 4,
        maxDepth = 64,
        numBins = 16
    };
}