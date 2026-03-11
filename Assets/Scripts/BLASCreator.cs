using UnityEngine;
using System.Collections.Generic;
using UnityEditor;
using Unity.Mathematics;
using System;
using System.Diagnostics;
using Debug = UnityEngine.Debug;
using Random = System.Random;

static class PerfTimer
{
    static Stopwatch sw = new Stopwatch();

    public static void Time(string label, System.Action action)
    {
        sw.Restart();
        action();
        sw.Stop();
        Debug.Log($"{label} took {sw.Elapsed.TotalMilliseconds:F3} ms");
    }
}

public struct BVHBin
{
    public int count;
    public Bounds bounds;
    public bool initialized;
}

public struct SplitResult
{
    public bool valid;
    public int axis;
    public float splitPos;
    public Bounds leftBounds;
    public Bounds rightBounds;
}

[DisallowMultipleComponent]
public class BLASCreator : MonoBehaviour
{
    public static int triangleCountPerLeaf = 4;
    public int numBins = 16;
    public BVHNode root;
    public RayTracedMesh meshObj;
    public bool drawCentroids = false;
    public bool drawBVH = false;
    public bool drawTriangleHeatMap = false;
    public bool drawNodeDepthHeatMap = false;
    [SerializeField] private int totalNodes = 0;
    [SerializeField] private int leafNodes = 0;
    [SerializeField] private int trianglesPerLeaf = 0;

    public int maxDepth = 8;
    [Range(1,32)] public int  visualizorMaxDepth = 8;
    public int maxTrianglesInNode = 0;
    public int minTrianglesInNode = 1000000;

    [SerializeField] private int recordDepth = 0;
    [SerializeField] private int highestLeafNode = 10000;
    [HideInInspector] public int[] triIndexArray;
    [SerializeField] private float averageTrianglesPerNode = 0;
    [SerializeField] private float numNodes = 0;
    [SerializeField] private int totalTrianglesCoveredByBVH = 0;

    [SerializeField][Range(1, 16)] private int trianglesInNodesCutoff = 1;
    [SerializeField] [Range(1, 32)] private int depthCutOff = 1;
    
    BVHBin[] bins;
    Bounds[] leftB, rightB;
    int[] leftC, rightC;
    bool[] leftInit, rightInit;


    public void ConstructBLAS()
    {
        totalNodes = 0;
        leafNodes = 0;
        trianglesPerLeaf = 0;

        meshObj = transform.GetComponent<RayTracedMesh>();
        if (meshObj == null || meshObj.buildTriangles == null || meshObj.buildTriangles.Length == 0)
        {
            return;
        }

        root = new BVHNode();
        BuildBLAS();
        GatherBLASStatistics();
    }

    void OnDrawGizmosSelected()
    {
        // Ensure we have data to visualize
        if (meshObj == null || meshObj.buildTriangles == null || meshObj.buildTriangles.Length == 0 || root == null)
        {
            meshObj = transform.GetComponent<RayTracedMesh>();
            if (meshObj != null && meshObj.buildTriangles != null && meshObj.buildTriangles.Length > 0)
            {
                ConstructBLAS();
            }
            else
            {
                return;
            }
        }

        Gizmos.color = Color.red;

        if (drawCentroids)
        {
            for (int i = 0; i < meshObj.buildTriangles.Length; i++)
            {
                // buildTriangles are in local space; convert to world for visualization
                var tri = meshObj.buildTriangles[i];
                Vector3 worldCentroid = transform.TransformPoint(tri.centroid);

                Bounds localBounds = tri.bounds;
                Vector3 localCenter = localBounds.center;
                Vector3 localSize = localBounds.size;

                // NOTE: This is not a correct world AABB under rotation,
                // but it's fine for a quick visualization like before.
                Vector3 worldCenter = transform.TransformPoint(localCenter);
                Vector3 worldSize = transform.TransformVector(localSize);

                Gizmos.DrawSphere(worldCentroid, 0.01f);
                Gizmos.DrawWireCube(worldCenter, worldSize);
            }
        }

        if (drawTriangleHeatMap)
        {
            DrawTriangleHeatMap(root);
        }
        
        else if (drawNodeDepthHeatMap)
        {
            DrawNodeDepthHeatMap(root, 0);
        }

        else if (drawBVH)
        {
            DrawBVH(root, 0);
        }

        Gizmos.color = Color.purple;
    }

    // --------- NEW helper: draw a local-space Bounds as a correct world-space AABB ---------
    void DrawBoundsAsWorldAABB(Bounds localBounds)
    {
        Vector3 c = localBounds.center;
        Vector3 e = localBounds.extents;

        Vector3[] corners =
        {
            c + new Vector3(-e.x, -e.y, -e.z),
            c + new Vector3(-e.x, -e.y,  e.z),
            c + new Vector3(-e.x,  e.y, -e.z),
            c + new Vector3(-e.x,  e.y,  e.z),
            c + new Vector3( e.x, -e.y, -e.z),
            c + new Vector3( e.x, -e.y,  e.z),
            c + new Vector3( e.x,  e.y, -e.z),
            c + new Vector3( e.x,  e.y,  e.z),
        };

        Vector3 worldMin = Vector3.positiveInfinity;
        Vector3 worldMax = Vector3.negativeInfinity;

        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 wp = transform.TransformPoint(corners[i]);
            worldMin = Vector3.Min(worldMin, wp);
            worldMax = Vector3.Max(worldMax, wp);
        }

        Vector3 worldCenter = (worldMin + worldMax) * 0.5f;
        Vector3 worldSize = (worldMax - worldMin);

        Gizmos.DrawCube(worldCenter, worldSize);
    }

    void DrawTriangleHeatMap(BVHNode node)
    {
        if (node == null) return;

        if (node.left == null && node.right == null && node.count > trianglesInNodesCutoff)
        {
            // Avoid NaN/Inf colors
            float denom = Mathf.Max(1, maxTrianglesInNode);
            Color c = new Color((float)node.count / denom, 0, 0);
            Gizmos.color = c;

            // node.bounds is local-space; draw correct world-space AABB
            DrawBoundsAsWorldAABB(node.bounds);
            return;
        }

        DrawTriangleHeatMap(node.left);
        DrawTriangleHeatMap(node.right);
    }

    void DrawNodeDepthHeatMap(BVHNode node, int depth)
    {
        if (node == null) return;

        if (node.left == null && node.right == null && depth < depthCutOff)
        {
            // Avoid NaN/Inf colors
            float denom = Mathf.Max(1, recordDepth);
            float shading = (float)(depth - highestLeafNode) / (recordDepth - highestLeafNode);
            Color c = new Color(shading, shading, shading);
            Gizmos.color = c;

            // node.bounds is local-space; draw correct world-space AABB
            DrawBoundsAsWorldAABB(node.bounds);
            return;
        }

        
        DrawNodeDepthHeatMap(node.left, depth + 1);
        DrawNodeDepthHeatMap(node.right, depth + 1);
    }

    void DrawBVH(BVHNode node, int depth)
    {
        if (node == null) return;
        if (depth > visualizorMaxDepth) return;
        if (depth == visualizorMaxDepth)
        {
            UnityEngine.Random.InitState(depth);
            Gizmos.color = new Color(UnityEngine.Random.Range(0f, 1f), UnityEngine.Random.Range(0f, 1f), UnityEngine.Random.Range(0f, 1f),
                (float)(depth + 1) / (visualizorMaxDepth + 1));

            DrawBoundsAsWorldAABB(node.bounds);
        }
        

        DrawBVH(node.left, depth + 1);
        DrawBVH(node.right, depth + 1);
    }
    void GatherBLASStatistics()
    {
        recordDepth = 0;
        maxTrianglesInNode = 0;
        minTrianglesInNode = 100000;
        averageTrianglesPerNode = 0;
        numNodes = 0;
        leafNodes = 0;
        highestLeafNode = 1000;
        totalTrianglesCoveredByBVH = 0;
        GatherBLASStatisticsRec(root, 0);

        if (leafNodes > 0)
            averageTrianglesPerNode /= leafNodes;
    }

    void GatherBLASStatisticsRec(BVHNode node, int depth)
    {
        recordDepth = Math.Max(depth, recordDepth);
        if (node == null) return;

        if (node.left == null && node.right == null)
        {
            uint nodeTriangles = node.count;
            maxTrianglesInNode = (int)Math.Max(nodeTriangles, maxTrianglesInNode);
            minTrianglesInNode = (int)Math.Min(nodeTriangles, minTrianglesInNode);
            highestLeafNode = Math.Min(highestLeafNode, depth);
            averageTrianglesPerNode += nodeTriangles;
            leafNodes++;
            totalTrianglesCoveredByBVH += (int)nodeTriangles;
        }

        numNodes++;

        // Keep your old world AABB computation block (maintained),
        // now using Bounds-derived min/max (still local space)
        Vector3 localMin = node.bounds.min;
        Vector3 localMax = node.bounds.max;

        Vector3 localCenter = (localMin + localMax) * 0.5f;
        Vector3 localExtents = (localMax - localMin) * 0.5f;

        Vector3[] corners =
        {
            localCenter + new Vector3(-localExtents.x, -localExtents.y, -localExtents.z),
            localCenter + new Vector3(-localExtents.x, -localExtents.y,  localExtents.z),
            localCenter + new Vector3(-localExtents.x,  localExtents.y, -localExtents.z),
            localCenter + new Vector3(-localExtents.x,  localExtents.y,  localExtents.z),
            localCenter + new Vector3( localExtents.x, -localExtents.y, -localExtents.z),
            localCenter + new Vector3( localExtents.x, -localExtents.y,  localExtents.z),
            localCenter + new Vector3( localExtents.x,  localExtents.y, -localExtents.z),
            localCenter + new Vector3( localExtents.x,  localExtents.y,  localExtents.z),
        };

        Vector3 worldMin = Vector3.positiveInfinity;
        Vector3 worldMax = Vector3.negativeInfinity;
        for (int i = 0; i < corners.Length; i++)
        {
            Vector3 wp = transform.TransformPoint(corners[i]);
            worldMin = Vector3.Min(worldMin, wp);
            worldMax = Vector3.Max(worldMax, wp);
        }

        Vector3 worldCenter = (worldMin + worldMax) * 0.5f;
        Vector3 worldSize = (worldMax - worldMin);

        GatherBLASStatisticsRec(node.left, depth + 1);
        GatherBLASStatisticsRec(node.right, depth + 1);
    }

    public List<GPUBVHNode> FlattenBLAS()
    {
        var nodes = new List<GPUBVHNode>();
        var stack = new Stack<(BVHNode n, int idx)>();

        int rootIdx = nodes.Count; nodes.Add(default);
        stack.Push((root, rootIdx));

        while (stack.Count > 0)
        {
            var (n, idx) = stack.Pop();

            int leftIdx = -1, rightIdx = -1;
            if (n.left != null) { leftIdx = nodes.Count; nodes.Add(default); stack.Push((n.left, leftIdx)); }
            if (n.right != null) { rightIdx = nodes.Count; nodes.Add(default); stack.Push((n.right, rightIdx)); }

            // Make sure boundsMin/boundsMax are synced from Bounds (they are, in BuildBVHRec below)
            nodes[idx] = new GPUBVHNode
            {
                left = leftIdx,
                right = rightIdx,
                firstIndex = n.firstIndex,
                count = n.count,

                AABBLeftX = n.boundsMin.x,
                AABBLeftY = n.boundsMin.y,
                AABBLeftZ = n.boundsMin.z,
                AABBRightX = n.boundsMax.x,
                AABBRightY = n.boundsMax.y,
                AABBRightZ = n.boundsMax.z,
            };
        }

        return nodes;
    }

    void CreateTriIndexArray()
    {
        triIndexArray = new int[meshObj.buildTriangles.Length];
        for (int i = 0; i < meshObj.buildTriangles.Length; i++)
        {
            triIndexArray[i] = i;
        }
    }

    void BuildBLAS()
    {
        PerfTimer.Time("TriIndexArray Assembly", () => CreateTriIndexArray());
        Bounds rootBounds = ComputeBoundsForRange(0, triIndexArray.Length - 1);
        PerfTimer.Time("BVH assembly of " + transform.name, () => root = BuildBVHRec(0, triIndexArray.Length - 1, 0, rootBounds));
        ValidateTriangleCoverage();
    }
    
    public void ValidateTriangleCoverage()
    {
        if (meshObj == null || meshObj.buildTriangles == null || root == null || triIndexArray == null)
        {
            Debug.LogWarning("BVH not ready for validation.");
            return;
        }

        int triCount = meshObj.buildTriangles.Length;
        int[] counts = new int[triCount];

        void Walk(BVHNode node)
        {
            if (node == null) return;

            bool isLeaf = (node.left == null && node.right == null);
            if (isLeaf)
            {
                int start = (int)node.firstIndex;
                int end = start + (int)node.count; // exclusive

                if (start < 0 || end > triIndexArray.Length || start > end)
                {
                    Debug.LogError($"Invalid leaf range: [{start}, {end}) / {triIndexArray.Length}");
                    return;
                }

                for (int i = start; i < end; i++)
                {
                    int triIdx = triIndexArray[i];
                    if (triIdx < 0 || triIdx >= triCount)
                    {
                        Debug.LogError($"Leaf references invalid triangle index {triIdx} at triIndexArray[{i}]");
                        continue;
                    }

                    counts[triIdx]++;
                }

                return;
            }

            Walk(node.left);
            Walk(node.right);
        }

        Walk(root);

        int missing = 0;
        int duplicated = 0;

        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] == 0)
            {
                Debug.LogError($"Triangle {i} is missing from BVH leaves.");
                missing++;
            }
            else if (counts[i] > 1)
            {
                Debug.LogError($"Triangle {i} appears {counts[i]} times in BVH leaves.");
                duplicated++;
            }
        }

        Debug.Log($"BVH coverage validation complete. Missing={missing}, Duplicated={duplicated}, Total={triCount}");
    }

    int PartitionArray<T>(ref T[] array, int start, int end, Func<T, bool> goesLeft)
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

        return i; // first index of right partition
    }

    Bounds ComputeBoundsForRange(int start, int end)
    {
        // Seed with first triangle bounds
        int tri0 = triIndexArray[start];
        Bounds b = meshObj.buildTriangles[tri0].bounds;

        for (int k = start + 1; k <= end; k++)
        {
            int triIdx = triIndexArray[k];
            b.Encapsulate(meshObj.buildTriangles[triIdx].bounds);
        }

        return b;
    }

    BVHNode BuildBVHRec(int start, int end, int depth, Bounds bounds)
    {
        BVHNode node = new BVHNode();
        node.firstIndex = (uint)start;
        node.count = (uint)end - (uint)start + 1;
        totalNodes++;
        // Always compute node bounds from triangles in this range (Unity Bounds)
        Bounds nodeBounds = bounds;

        // Store Bounds directly (NEW)
        node.bounds = nodeBounds;

        // Keep old fields in sync (so flattenBVH + GPU path stays unchanged)
        node.boundsMin = nodeBounds.min;
        node.boundsMax = nodeBounds.max;

        // Leaf condition
        if (depth >= maxDepth || node.count <= triangleCountPerLeaf)
            return node;

        // Choose axis by node bounds size
        Vector3 s = nodeBounds.size;
        int axis = (s.x > s.y && s.x > s.z) ? 0 : (s.y > s.z ? 1 : 2);
        

        SplitResult split = FindBestSplitBalanced_NoMutate(start, end, ref axis);
        if (!split.valid)
        {
            return node;
        }

        int mid = PartitionArray(ref triIndexArray, start, end, triIndex => meshObj.buildTriangles[triIndex].centroid[split.axis] < split.splitPos);

        // If partition failed to produce two non-empty sides, stop splitting.
        if (mid <= start || mid > end)
        {
            return node;
        }

        // Recompute bounds from the ACTUAL triangle ranges after partition.
        Bounds leftBounds = ComputeBoundsForRange(start, mid - 1);
        Bounds rightBounds = ComputeBoundsForRange(mid, end);

        node.left = BuildBVHRec(start, mid - 1, depth + 1, leftBounds);
        node.right = BuildBVHRec(mid, end, depth + 1, rightBounds);

        // Optional but good: ensure parent bounds encapsulate children exactly
        // (also keeps Bounds consistent if you later change ComputeBoundsForRange)

        return node;
    }

    (float min, float max) GetMinMax<T>(
        T[] array,
        int start,
        int end,
        Func<T, float> selector)
    {
        float min = selector(array[start]);
        float max = min;

        for (int k = start + 1; k <= end; k++)
        {
            float v = selector(array[k]);

            if (v < min) min = v;
            else if (v > max) max = v;
        }

        return (min, max);
    }
    SplitResult FindBestSplitBalanced_NoMutate(int start, int end, ref int axis)
    {
        // Use centroid range, not bounds range (usually better for splitting)
        SplitResult toReturn = new SplitResult
            { valid = false, axis = -1, splitPos = 0f, leftBounds = default, rightBounds = default };
        EnsureScratch();
        float bestScore = Mathf.Infinity;
        for (int i = 0; i < numBins; i++)
        {
            bins[i] = new BVHBin { count = 0, initialized = false };
        }
        for (int i = 0; i <= 2; i++)
        {
            Array.Clear(leftC, 0, leftC.Length);
            Array.Clear(rightC, 0, rightC.Length);
            Array.Clear(rightInit, 0, rightInit.Length);
            Array.Clear(leftInit, 0, leftInit.Length);

            var (cmin, cmax) = GetMinMax(triIndexArray, start, end, triIndex => meshObj.buildTriangles[triIndex].centroid[i]);

            // Degenerate: all centroids same on this axis -> no meaningful split
            if (cmax <= cmin + 1e-8f)
                continue;
            for (int k = 0; k < numBins; k++)
            {
                bins[k].count = 0;
                bins[k].bounds = default;
                bins[k].initialized = false;
            }
            float invRange = 1f / (cmax - cmin);
            for (int k = start; k <= end; k++) //note END IS INCLUSIVE
            {
                ref buildTri tri = ref meshObj.buildTriangles[triIndexArray[k]];
                float c = tri.centroid[i];
                float t = (c - cmin) * invRange;
                int binIndex = Mathf.Clamp((int)(t * numBins), 0, numBins - 1);
                ref BVHBin bin = ref bins[binIndex];
                bin.count++;
                if (!bin.initialized)
                {
                    bin.bounds = tri.bounds;
                    bin.initialized = true;
                }

                else
                {
                    bin.bounds.Encapsulate(tri.bounds);
                }
            }
            
// prefix
            for (int b = 0; b < numBins; b++)
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
                    if (!leftInit[b]) { leftB[b] = bins[b].bounds; leftInit[b] = true; }
                    else leftB[b].Encapsulate(bins[b].bounds);
                }
            }

// suffix
            for (int b = numBins - 1; b >= 0; b--)
            {
                if (b < numBins - 1)
                {
                    rightC[b] = rightC[b + 1];
                    rightB[b] = rightB[b + 1];
                    rightInit[b] = rightInit[b + 1];
                }

                rightC[b] += bins[b].count;

                if (bins[b].initialized)
                {
                    if (!rightInit[b]) { rightB[b] = bins[b].bounds; rightInit[b] = true; }
                    else rightB[b].Encapsulate(bins[b].bounds);
                }
            }

// evaluate all boundaries k = 1..B-1
            for (int k = 1; k < numBins; k++)
            {
                int lc = leftC[k - 1];
                int rc = rightC[k];

                if (!leftInit[k - 1] || !rightInit[k]) continue; // empty side

                float cost = lc * surfaceArea(leftB[k - 1]) + rc * surfaceArea(rightB[k]);

                if (cost < bestScore)
                {
                    bestScore = cost;

                    float t = k / (float)numBins;
                    float splitPos = cmin + t * (cmax - cmin);

                    toReturn.valid = true;
                    toReturn.axis = i;
                    toReturn.splitPos = splitPos;
                    toReturn.leftBounds = leftB[k - 1];
                    toReturn.rightBounds = rightB[k];
                    
                }
            }


        }
        return toReturn;
    }
    public static float surfaceArea(Bounds b)
    {
        Vector3 size = b.size;
        return 2f * (size.x * size.y + size.y * size.z + size.z * size.x);
    }
    
    void EnsureScratch()
    {
        if (bins != null && bins.Length == numBins) return;
        bins = new BVHBin[numBins];
        leftB = new Bounds[numBins];
        rightB = new Bounds[numBins];
        leftC = new int[numBins];
        rightC = new int[numBins];
        leftInit = new bool[numBins];
        rightInit = new bool[numBins];
    }
}