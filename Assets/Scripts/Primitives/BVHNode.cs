using UnityEngine;
//Used for BVH construction, needs to be flattened for GPU
public class BVHNode {
    public Vector3 boundsMin;
    public Vector3 boundsMax;

    public Bounds bounds;

    public BVHNode left;
    public BVHNode right;

    public uint firstIndex;
    public uint count;

    public BVHNode() {
        boundsMin = Vector3.zero;
        boundsMax = Vector3.zero;
        left = null;
        right = null;
        firstIndex = 0;
        count = 0;
    }
}