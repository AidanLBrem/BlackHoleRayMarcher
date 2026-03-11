using UnityEngine;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)]
public struct Triangle
{
    /*public int vertexIndex1;
    public int vertexIndex2;
    public int vertexIndex3;*/
    public uint baseIndex;
    
    public Vector3 edgeAB;
    public Vector3 edgeAC;

    public Vector3 normal;

}
