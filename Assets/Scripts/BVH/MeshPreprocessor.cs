using System.Collections.Generic;
using UnityEngine;

public static class MeshPreprocessor
{
    public static BuildTriangle[] BuildTrianglesFromMesh(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        Vector3[] normals = mesh.normals;
        int[] triangles = mesh.triangles;

        bool hasNormals = normals != null && normals.Length == vertices.Length;
        List<BuildTriangle> result = new List<BuildTriangle>(triangles.Length / 3);

        for (int i = 0; i < triangles.Length; i += 3)
        {
            int i0 = triangles[i];
            int i1 = triangles[i + 1];
            int i2 = triangles[i + 2];

            Vector3 v0 = vertices[i0];
            Vector3 v1 = vertices[i1];
            Vector3 v2 = vertices[i2];

            Vector3 faceNormal = Vector3.Cross(v1 - v0, v2 - v0).normalized;

            Vector3 n0 = hasNormals ? normals[i0] : faceNormal;
            Vector3 n1 = hasNormals ? normals[i1] : faceNormal;
            Vector3 n2 = hasNormals ? normals[i2] : faceNormal;

            result.Add(BuildTriangle.Create(v0, v1, v2, n0, n1, n2));
        }

        return result.ToArray();
    }
}