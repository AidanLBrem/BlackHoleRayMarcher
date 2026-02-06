using UnityEngine;

public class BVHNode {
    public Vector3 boundsMin;
    public Vector3 boundsMax;

    public BVHNode left;
    public BVHNode right;

    public int firstTriangleIndex;
    public int triangleCount;

    public BVHNode() {
        boundsMin = Vector3.zero;
        boundsMax = Vector3.zero;
        left = null;
        right = null;
        firstTriangleIndex = 0;
        triangleCount = 0;
    }
}