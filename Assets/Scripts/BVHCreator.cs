using UnityEngine;
using System.Collections.Generic;
using UnityEditor;
using Unity.Mathematics;
[DisallowMultipleComponent]
public class BVHCreator : MonoBehaviour
{
    [SerializeField] public static int triangleCountPerLeaf = 16;
    BVHNode root;
    public RayTracedMesh meshObj;
    public bool drawCentroids = false;
    public bool drawBVH = false;
    [SerializeField] private int totalNodes = 0;
    [SerializeField] private int leafNodes = 0;
    [SerializeField] private int trianglesPerLeaf = 0;
    public void StartBVHConstruction() {
        totalNodes = 0;
        leafNodes = 0;
        trianglesPerLeaf = 0;
        meshObj = transform.GetComponent<RayTracedMesh>();
        root = new BVHNode();
        BuildBVH();
    }
    
    void OnDrawGizmosSelected() {
        Gizmos.color = Color.red;
        if (drawCentroids) {
            for (int i = 0; i < meshObj.buildTriangles.Count; i++) {
                Gizmos.DrawSphere(meshObj.buildTriangles[i].centroid, 0.01f);
                Gizmos.DrawWireCube(meshObj.buildTriangles[i].bounds.center, meshObj.buildTriangles[i].bounds.size);
            }
        }
        Gizmos.color = Color.purple;
        if (drawBVH) {
            Stack<BVHNode> nodeStack = new Stack<BVHNode>();
            nodeStack.Push(root);
            while (nodeStack.Count > 0) {
                BVHNode node = nodeStack.Pop();
                Gizmos.color = Color.blue;
                if (node.left != null) {
                    nodeStack.Push(node.left);
                }
                else {
                    Gizmos.color = Color.red;
                }
                if (node.right != null) {
                    nodeStack.Push(node.right);
                }
                else {
                    Gizmos.color = Color.red;
                }
                Gizmos.DrawWireCube((node.boundsMin + node.boundsMax) / 2, node.boundsMax - node.boundsMin);
            }
        }

    }

    void BuildBVH() {
        root = new BVHNode(); 
        totalNodes++;
        root.triangleCount = meshObj.buildTriangles.Count;
        root.firstTriangleIndex = 0;
        root.left = null;
        root.right = null;
        int midIndex = (meshObj.buildTriangles.Count - 1) / 2;
        root.left = buildBVHLeaf(0, midIndex);
        root.right = buildBVHLeaf(midIndex + 1, meshObj.buildTriangles.Count - 1);
        root.boundsMin = Vector3.Min(root.left.boundsMin, root.right.boundsMin);
        root.boundsMax = Vector3.Max(root.left.boundsMax, root.right.boundsMax);
        trianglesPerLeaf /= leafNodes;
    }

    BVHNode buildBVHLeaf(int startIndex, int endIndex) {
        BVHNode node = new BVHNode();
        totalNodes++;
        node.triangleCount = endIndex - startIndex + 1;
        node.firstTriangleIndex = startIndex;
        if (node.triangleCount <= triangleCountPerLeaf) {
            leafNodes++;
            trianglesPerLeaf += node.triangleCount;
            node.left = null;
            node.right = null;
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            for (int i = startIndex; i <= endIndex; i++) {
                min = Vector3.Min(min, meshObj.buildTriangles[i].bounds.min);
                max = Vector3.Max(max, meshObj.buildTriangles[i].bounds.max);
            }
            node.boundsMin = min;
            node.boundsMax = max;
            return node;
        }
        int midIndex = (startIndex + endIndex) / 2;
        node.left = buildBVHLeaf(startIndex, midIndex);
        node.right = buildBVHLeaf(midIndex + 1, endIndex);
        node.boundsMin = Vector3.Min(node.left.boundsMin, node.right.boundsMin);
        node.boundsMax = Vector3.Max(node.left.boundsMax, node.right.boundsMax);
        return node;
    }

    public List<GPUBVHNode> flattenBVH() {
        var nodes = new List<GPUBVHNode>();
        var stack = new Stack<(BVHNode n, int idx)>();

        int rootIdx = nodes.Count; nodes.Add(default);
        stack.Push((root, rootIdx));

        while (stack.Count > 0) {
            var (n, idx) = stack.Pop();

            int leftIdx = -1, rightIdx = -1;
            if (n.left != null) { leftIdx = nodes.Count; nodes.Add(default); stack.Push((n.left, leftIdx)); }
            if (n.right != null){ rightIdx = nodes.Count; nodes.Add(default); stack.Push((n.right, rightIdx)); }

            nodes[idx] = new GPUBVHNode {
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
}
