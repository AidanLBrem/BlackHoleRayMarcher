using System;
using System.Collections.Generic;
using UnityEngine;

public struct BvhBin
{
    public int count;
    public Bounds bounds;
    public bool initialized;
}

public struct BvhSplitResult
{
    public bool valid;
    public int axis;
    public float splitPos;
    public float cost;
    public Bounds leftBounds;
    public Bounds rightBounds;
}

public class BvhBuilder<TRef>
{
    private readonly IBvhAdapter<TRef> adapter;
    private readonly BvhBuildSettings settings;

    private TRef[] primitiveRefs;
    private readonly List<BvhNode> nodes = new();

    private BvhBin[] bins;
    private Bounds[] leftB;
    private Bounds[] rightB;
    private int[] leftC;
    private int[] rightC;
    private bool[] leftInit;
    private bool[] rightInit;

    public IReadOnlyList<BvhNode> Nodes => nodes;
    public TRef[] PrimitiveRefs => primitiveRefs;

    public BvhBuilder(
        TRef[] primitiveRefs,
        IBvhAdapter<TRef> adapter,
        BvhBuildSettings settings)
    {
        this.primitiveRefs = primitiveRefs ?? throw new ArgumentNullException(nameof(primitiveRefs));
        this.adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));

        this.settings = settings;
        if (this.settings.maxLeafSize < 1) this.settings.maxLeafSize = 4;
        if (this.settings.maxDepth < 1) this.settings.maxDepth = 64;
        if (this.settings.numBins < 2) this.settings.numBins = 16;

        EnsureScratch();
    }

    public int Build()
    {
        nodes.Clear();

        if (primitiveRefs.Length == 0)
            return -1;

        Bounds rootBounds = ComputeBoundsForRange(0, primitiveRefs.Length - 1);
        return BuildRecursive(0, primitiveRefs.Length - 1, 0, rootBounds);
    }

    private int BuildRecursive(int start, int end, int depth, Bounds bounds)
    {
        int count = end - start + 1;

        int nodeIndex = nodes.Count;
        nodes.Add(new BvhNode
        {
            bounds = bounds,
            leftChild = -1,
            rightChild = -1,
            start = start,
            count = count
        });

        if (depth >= settings.maxDepth || count <= settings.maxLeafSize)
            return nodeIndex;

        BvhSplitResult split = FindBestSplitBinnedSAH_NoMutate(start, end);
        float leafCost = count * SurfaceArea(bounds);
        if (!split.valid || split.cost >= leafCost)
            return nodeIndex;
        
        int mid = PartitionArray(
            primitiveRefs,
            start,
            end,
            item => adapter.GetCentroid(item)[split.axis] < split.splitPos
        );

        if (mid <= start || mid > end)
            return nodeIndex;

        Bounds leftBounds = ComputeBoundsForRange(start, mid - 1);
        Bounds rightBounds = ComputeBoundsForRange(mid, end);

        int leftChild = BuildRecursive(start, mid - 1, depth + 1, leftBounds);
        int rightChild = BuildRecursive(mid, end, depth + 1, rightBounds);

        nodes[nodeIndex] = new BvhNode
        {
            bounds = bounds,
            leftChild = leftChild,
            rightChild = rightChild,
            start = start,
            count = count
        };

        return nodeIndex;
    }

    private int PartitionArray(
        TRef[] array,
        int start,
        int end,
        Func<TRef, bool> goesLeft)
    {
        int i = start;
        int j = end;

        while (i <= j)
        {
            if (goesLeft(array[i]))
            {
                i++;
                continue;
            }

            if (!goesLeft(array[j]))
            {
                j--;
                continue;
            }

            (array[i], array[j]) = (array[j], array[i]);
            i++;
            j--;
        }

        return i;
    }

    private Bounds ComputeBoundsForRange(int start, int end)
    {
        Bounds bounds = adapter.GetBounds(primitiveRefs[start]);

        for (int i = start + 1; i <= end; i++)
            bounds.Encapsulate(adapter.GetBounds(primitiveRefs[i]));

        return bounds;
    }

    private (float min, float max) GetMinMax(
        TRef[] array,
        int start,
        int end,
        Func<TRef, float> selector)
    {
        float min = selector(array[start]);
        float max = min;

        for (int i = start + 1; i <= end; i++)
        {
            float v = selector(array[i]);

            if (v < min) min = v;
            else if (v > max) max = v;
        }

        return (min, max);
    }

    private BvhSplitResult FindBestSplitBinnedSAH_NoMutate(int start, int end)
    {
        EnsureScratch();

        BvhSplitResult best = new BvhSplitResult
        {
            valid = false,
            axis = -1,
            splitPos = 0f,
            leftBounds = default,
            rightBounds = default
        };

        float bestScore = Mathf.Infinity;

        for (int axis = 0; axis < 3; axis++)
        {
            Array.Clear(leftC, 0, leftC.Length);
            Array.Clear(rightC, 0, rightC.Length);
            Array.Clear(leftInit, 0, leftInit.Length);
            Array.Clear(rightInit, 0, rightInit.Length);

            var (cmin, cmax) = GetMinMax(
                primitiveRefs,
                start,
                end,
                item => adapter.GetCentroid(item)[axis]
            );

            if (cmax <= cmin + 1e-8f)
                continue;

            for (int b = 0; b < settings.numBins; b++)
            {
                bins[b].count = 0;
                bins[b].bounds = default;
                bins[b].initialized = false;
            }

            float invRange = 1f / (cmax - cmin);

            for (int i = start; i <= end; i++)
            {
                TRef item = primitiveRefs[i];
                float c = adapter.GetCentroid(item)[axis];
                float t = (c - cmin) * invRange;
                int binIndex = Mathf.Clamp((int)(t * settings.numBins), 0, settings.numBins - 1);

                ref BvhBin bin = ref bins[binIndex];
                bin.count++;

                Bounds itemBounds = adapter.GetBounds(item);
                if (!bin.initialized)
                {
                    bin.bounds = itemBounds;
                    bin.initialized = true;
                }
                else
                {
                    bin.bounds.Encapsulate(itemBounds);
                }
            }

            for (int b = 0; b < settings.numBins; b++)
            {
                if (b > 0)
                {
                    leftC[b] = leftC[b - 1];
                    leftB[b] = leftB[b - 1];
                    leftInit[b] = leftInit[b - 1];
                }

                leftC[b] += bins[b].count;

                if (bins[b].initialized)
                {
                    if (!leftInit[b])
                    {
                        leftB[b] = bins[b].bounds;
                        leftInit[b] = true;
                    }
                    else
                    {
                        leftB[b].Encapsulate(bins[b].bounds);
                    }
                }
            }

            for (int b = settings.numBins - 1; b >= 0; b--)
            {
                if (b < settings.numBins - 1)
                {
                    rightC[b] = rightC[b + 1];
                    rightB[b] = rightB[b + 1];
                    rightInit[b] = rightInit[b + 1];
                }

                rightC[b] += bins[b].count;

                if (bins[b].initialized)
                {
                    if (!rightInit[b])
                    {
                        rightB[b] = bins[b].bounds;
                        rightInit[b] = true;
                    }
                    else
                    {
                        rightB[b].Encapsulate(bins[b].bounds);
                    }
                }
            }

            for (int k = 1; k < settings.numBins; k++)
            {
                int lc = leftC[k - 1];
                int rc = rightC[k];

                if (!leftInit[k - 1] || !rightInit[k])
                    continue;

                float cost =
                    lc * SurfaceArea(leftB[k - 1]) +
                    rc * SurfaceArea(rightB[k]);

                if (cost < bestScore)
                {
                    bestScore = cost;

                    float t = k / (float)settings.numBins;
                    float splitPos = cmin + t * (cmax - cmin);

                    best.valid = true;
                    best.axis = axis;
                    best.splitPos = splitPos;
                    best.cost = cost;
                    best.leftBounds = leftB[k - 1];
                    best.rightBounds = rightB[k];
                }
            }
        }

        return best;
    }

    private static float SurfaceArea(Bounds b)
    {
        Vector3 size = b.size;
        return 2f * (size.x * size.y + size.y * size.z + size.z * size.x);
    }

    private void EnsureScratch()
    {
        if (bins != null && bins.Length == settings.numBins)
            return;

        bins = new BvhBin[settings.numBins];
        leftB = new Bounds[settings.numBins];
        rightB = new Bounds[settings.numBins];
        leftC = new int[settings.numBins];
        rightC = new int[settings.numBins];
        leftInit = new bool[settings.numBins];
        rightInit = new bool[settings.numBins];
    }
}