using Unity.Mathematics;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)]
public struct GPUBVHNode {


    public int left;
    public int right;

    public uint firstIndex;
    public uint count;

    public float AABBLeftX;
    public float AABBLeftY;
    public float AABBLeftZ;
    public float AABBRightX;
    public float AABBRightY;
    public float AABBRightZ;
}