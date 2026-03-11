using UnityEngine;

public static class BvhPacker
{
    public static GpuBvhNode[] PackNodes(BvhNode[] nodes)
    {
        if (nodes == null)
            return new GpuBvhNode[0];

        GpuBvhNode[] packed = new GpuBvhNode[nodes.Length];

        for (int i = 0; i < nodes.Length; i++)
        {
            Bounds b = nodes[i].bounds;

            packed[i] = new GpuBvhNode
            {
                boundsMin = b.min,
                boundsMax = b.max,
                leftChild = nodes[i].leftChild,
                rightChild = nodes[i].rightChild,
                start = nodes[i].start,
                count = nodes[i].count,
            };
        }

        return packed;
    }
}