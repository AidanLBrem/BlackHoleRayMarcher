using UnityEngine;
using System.Runtime.InteropServices;
[StructLayout(LayoutKind.Sequential)]
//Triangle struct fed into GPU
//TODO: Octal encoding?
public struct Triangle
{
    /*public int vertexIndex1;
    public int vertexIndex2;
    public int vertexIndex3;*/
    public uint baseIndex;
    
    public Vector3 edgeAB;
    public Vector3 edgeAC;

}
