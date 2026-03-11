using System;
using UnityEngine;

[Serializable]
public struct BvhNode
{
    public Bounds bounds;

    public int leftChild;
    public int rightChild;

    public int start;
    public int count;

    public bool IsLeaf => count > 0;
}