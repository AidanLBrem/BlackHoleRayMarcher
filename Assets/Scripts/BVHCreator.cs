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

[DisallowMultipleComponent]
public class BVHCreator : MonoBehaviour
{
    public static int triangleCountPerLeaf = 4;
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
        PerfTimer.Time("BVH assembly", () => root = BuildBVHRec(0, triIndexArray.Length - 1, 0));
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

    BVHNode BuildBVHRec(int start, int end, int depth)
    {
        BVHNode node = new BVHNode();
        node.firstTriangleIndex = start;
        node.triangleCount = end - start + 1;

        // Always compute node bounds from triangles in this range (Unity Bounds)
        Bounds nodeBounds = ComputeBoundsForRange(start, end);

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
        

        float split = FindBestSplitBalanced_NoMutate(start, end, ref axis);
        if (float.IsNaN(split))
        {
            return node;
        }

        int mid = PartitionTriIndexArray(split, start, end, axis);
        if (mid <= start || mid > end)
        {
            return node;
        }
        node.left = BuildBVHRec(start, mid - 1, depth + 1);
        node.right = BuildBVHRec(mid, end, depth + 1);

        // Optional but good: ensure parent bounds encapsulate children exactly
        // (also keeps Bounds consistent if you later change ComputeBoundsForRange)

        return node;
    }

    float FindBestSplitBalanced_NoMutate(int start, int end, ref int axis)
    {
        // Use centroid range, not bounds range (usually better for splitting)

        int total = end - start + 1;
        int targetLeft = total / 2;

        float bestSplit = float.NaN;
        float bestCost = float.MaxValue;
        float bestRatio = 1f;
        for (int i = 0; i <= 2; i++)
        {
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

            for (float t = 0.125f; t <= 0.875f; t += 0.125f)
            {
                float split = Mathf.Lerp(cmin, cmax, t);
                Bounds leftBounds = default;
                Bounds rightBounds = default;
                bool leftInit = false;
                bool rightInit = false;
                int leftCount = 0;
                int rightCount = 0;

                for (int k = start; k <= end; k++)
                {
                    float c = meshObj.buildTriangles[triIndexArray[k]].centroid[i];
                    if (c < split)
                    {
                        leftCount++;
                        Bounds bounds = meshObj.buildTriangles[triIndexArray[k]].bounds;
                        if (!leftInit)
                        {
                            leftBounds = bounds;
                            leftInit = true;
                        }
                        else
                        {
                            leftBounds.Encapsulate(bounds);
                        }
                        
                    }

                    else
                    {
                        rightCount++;
                        Bounds bounds = meshObj.buildTriangles[triIndexArray[k]].bounds;
                        if (!rightInit)
                        {
                            rightBounds = bounds;
                            rightInit = true;
                        }

                        else
                        {
                            rightBounds.Encapsulate(bounds);
                        }
                    }
                }

                if (leftCount == 0 || rightCount == 0)
                {
                    continue;
                }

                /*float ratio = (float)leftCount / total; //Even split
                if (Math.Abs(ratio - 0.5f) < bestRatio)
                {
                    bestRatio = ratio;
                    bestSplit = split;
                }*/
                //SAH split
                float leftCost = leftCount * surfaceArea(leftBounds);
                float rightCost = rightCount * surfaceArea(rightBounds);
                float totalCost = leftCost + rightCost;
                if (totalCost < bestCost)
                {
                    bestCost = totalCost;
                    bestSplit = split;
                    axis = i;
                }
            }
        }

        return bestSplit;
    }
    public static float surfaceArea(Bounds b)
    {
        Vector3 size = b.size;
        return 2f * (size.x * size.y + size.y * size.z + size.z * size.x);
    }
}