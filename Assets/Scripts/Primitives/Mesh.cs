using UnityEngine;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)]
struct MeshStruct {
    public int indexOffset;
    public int triangleCount;
    public RayTracingMaterial material;
    public float AABBLeftX;
    public float AABBLeftY;
    public float AABBLeftZ;
    public float AABBRightX;
    public float AABBRightY;
    public float AABBRightZ;
    public int   firstBVHNodeIndex;
    public int   largestAxis;
    public Matrix4x4 localToWorld;
    public Matrix4x4 worldToLocal;
};