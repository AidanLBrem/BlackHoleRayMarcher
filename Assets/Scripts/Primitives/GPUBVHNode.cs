using Unity.Mathematics;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)]
//BVH node that GPU can read
//TODO: pack all the AABBs into something more readable
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