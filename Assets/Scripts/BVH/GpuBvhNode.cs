using System.Runtime.InteropServices;
using UnityEngine;

[StructLayout(LayoutKind.Sequential)]
public struct GpuBvhNode
{
    public Vector3 boundsMin;
    public int leftChild;

    public Vector3 boundsMax;
    public int rightChild;

    public int start;
    public int count;
}