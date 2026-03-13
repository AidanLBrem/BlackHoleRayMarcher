using UnityEngine;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)]
struct MeshStruct {
    public Matrix4x4 localToWorldMatrix;
    public Matrix4x4 worldToLocalMatrix;
    public RayTracingMaterial material;

    public uint firstBVHNodeIndex;

    public float AABBLeftX;
    public float AABBLeftY;
    public float AABBLeftZ;
    public float AABBRightX;
    public float AABBRightY;
    public float AABBRightZ;

};
