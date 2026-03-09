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
    public int triCount;
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
public class BVHCreator : MonoBehaviour
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

    [SerializeField][Range(1, 16)] private int trianglesInNodesCutoff = 1;
    [SerializeField] [Range(1, 32)] private int depthCutOff = 1;
    
    BVHBin[] bins;
    Bounds[] leftB, rightB;
    int[] leftC, rightC;
    bool[] leftInit, rightInit;


    public void StartBVHConstruction()
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
        BuildBVH();
        gatherBVHStatistics();
    }

    void OnDrawGizmosSelected()
    {
        // Ensure we have data to visualize
        if (meshObj == null || meshObj.buildTriangles == null || meshObj.buildTriangles.Length == 0 || root == null)
        {
            meshObj = transform.GetComponent<RayTracedMesh>();
            if (meshObj != null && meshObj.buildTriangles != null && meshObj.buildTriangles.Length > 0)
            {
                StartBVHConstruction();
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

        if (node.left == null && node.right == null && node.triangleCount > trianglesInNodesCutoff)
        {
            // Avoid NaN/Inf colors
            float denom = Mathf.Max(1, maxTrianglesInNode);
            Color c = new Color((float)node.triangleCount / denom, 0, 0);
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
    void gatherBVHStatistics()
    {
        recordDepth = 0;
        maxTrianglesInNode = 0;
        minTrianglesInNode = 100000;
        averageTrianglesPerNode = 0;
        numNodes = 0;
        leafNodes = 0;
        highestLeafNode = 1000;
        gatherBVHStatisticsRec(root, 0);

        if (leafNodes > 0)
            averageTrianglesPerNode /= leafNodes;
    }

    void gatherBVHStatisticsRec(BVHNode node, int depth)
    {
        recordDepth = Math.Max(depth, recordDepth);
        if (node == null) return;

        if (node.left == null && node.right == null)
        {
            int nodeTriangles = node.triangleCount;
            maxTrianglesInNode = Math.Max(nodeTriangles, maxTrianglesInNode);
            minTrianglesInNode = Math.Min(nodeTriangles, minTrianglesInNode);
            highestLeafNode = Math.Min(highestLeafNode, depth);
            averageTrianglesPerNode += nodeTriangles;
            leafNodes++;
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

        gatherBVHStatisticsRec(node.left, depth + 1);
        gatherBVHStatisticsRec(node.right, depth + 1);
    }

    public List<GPUBVHNode> flattenBVH()
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
                firstTriangleIndex = n.firstTriangleIndex,
                triangleCount = n.triangleCount,

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

    void BuildBVH()
    {
        PerfTimer.Time("TriIndexArray Assembly", () => CreateTriIndexArray());
        Bounds rootBounds = ComputeBoundsForRange(0, triIndexArray.Length - 1);
        PerfTimer.Time("BVH assembly of " + transform.name, () => root = BuildBVHRec(0, triIndexArray.Length - 1, 0, rootBounds));
    }

    int PartitionTriIndexArray(float split, int start, int end, int axis)
    {
        int i = start;
        int j = end;

        while (i <= j)
        {
            int triI = triIndexArray[i];
            float cI = meshObj.buildTriangles[triI].centroid[axis];

            if (cI < split) { i++; continue; }

            int triJ = triIndexArray[j];
            float cJ = meshObj.buildTriangles[triJ].centroid[axis];

            if (cJ >= split) { j--; continue; }

            (triIndexArray[i], triIndexArray[j]) = (triIndexArray[j], triIndexArray[i]);
            i++;
            j--;
        }

        return i; // i == first index of RIGHT partition (can be end+1)
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
        node.firstTriangleIndex = start;
        node.triangleCount = end - start + 1;
        totalNodes++;
        // Always compute node bounds from triangles in this range (Unity Bounds)
        Bounds nodeBounds = bounds;

        // Store Bounds directly (NEW)
        node.bounds = nodeBounds;

        // Keep old fields in sync (so flattenBVH + GPU path stays unchanged)
        node.boundsMin = nodeBounds.min;
        node.boundsMax = nodeBounds.max;

        // Leaf condition
        if (depth >= maxDepth || node.triangleCount <= triangleCountPerLeaf)
            return node;

        // Choose axis by node bounds size
        Vector3 s = nodeBounds.size;
        int axis = (s.x > s.y && s.x > s.z) ? 0 : (s.y > s.z ? 1 : 2);
        

        SplitResult split = FindBestSplitBalanced_NoMutate(start, end, ref axis);
        if (!split.valid)
        {
            return node;
        }

        int mid = PartitionTriIndexArray(split.splitPos, start, end, split.axis);
        if (mid <= start) mid = start + 1;
        else if (mid > end) mid = end;
        node.left = BuildBVHRec(start, mid - 1, depth + 1, split.leftBounds);
        node.right = BuildBVHRec(mid, end, depth + 1, split.rightBounds);

        // Optional but good: ensure parent bounds encapsulate children exactly
        // (also keeps Bounds consistent if you later change ComputeBoundsForRange)

        return node;
    }

    SplitResult FindBestSplitBalanced_NoMutate(int start, int end, ref int axis)
    {
        // Use centroid range, not bounds range (usually better for splitting)
        SplitResult toReturn = new SplitResult
            { valid = false, axis = -1, splitPos = 0f, leftBounds = default, rightBounds = default };
        int total = end - start + 1;
        EnsureScratch();
        float bestScore = Mathf.Infinity;
        for (int i = 0; i < numBins; i++)
        {
            bins[i] = new BVHBin { triCount = 0, initialized = false };
        }
        for (int i = 0; i <= 2; i++)
        {
            Array.Clear(leftC, 0, leftC.Length);
            Array.Clear(rightC, 0, rightC.Length);
            Array.Clear(rightInit, 0, rightInit.Length);
            Array.Clear(leftInit, 0, leftInit.Length);
            float cmin = float.PositiveInfinity;
            float cmax = float.NegativeInfinity;

            for (int k = start; k <= end; k++)
            {
                float c = meshObj.buildTriangles[triIndexArray[k]].centroid[i];
                cmin = Mathf.Min(cmin, c);
                cmax = Mathf.Max(cmax, c);
            }

            // Degenerate: all centroids same on this axis -> no meaningful split
            if (cmax <= cmin + 1e-8f)
                continue;
            for (int k = 0; k < numBins; k++)
            {
                bins[k].triCount = 0;
                bins[k].bounds = default;
                bins[k].initialized = false;
            }
            float invRange = 1f / (cmax - cmin);
            for (int k = start; k <= end; k++)
            {
                ref buildTri tri = ref meshObj.buildTriangles[triIndexArray[k]];
                float c = tri.centroid[i];
                float t = (c - cmin) * invRange;
                int binIndex = Mathf.Clamp((int)(t * numBins), 0, numBins - 1);
                ref BVHBin bin = ref bins[binIndex];
                bin.triCount++;
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

                leftC[b] += bins[b].triCount;

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

                rightC[b] += bins[b].triCount;

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