using System;
using UnityEngine;

[Serializable]
public struct BvhInstance
{
    public int blasIndex;
    public int blasRootIndex;

    public Matrix4x4 localToWorld;
    public Matrix4x4 worldToLocal;

    public Bounds localBounds;
    public Bounds worldBounds;

    public int materialIndex;
}