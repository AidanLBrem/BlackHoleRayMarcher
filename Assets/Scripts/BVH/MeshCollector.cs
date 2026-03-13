using UnityEngine;
using System.Collections.Generic;

public static class MeshCollector
{
    public static Dictionary<Mesh, List<Transform>> GetUniqueMeshes()
    {
        Dictionary<Mesh, List<Transform>> meshInstances = new();

        foreach (var mf in Object.FindObjectsOfType<MeshFilter>())
        {
            Mesh mesh = mf.sharedMesh;
            if (mesh == null) continue;

            if (!meshInstances.TryGetValue(mesh, out var list))
            {
                list = new List<Transform>();
                meshInstances[mesh] = list;
            }

            list.Add(mf.transform);
        }

        return meshInstances;
    }
}