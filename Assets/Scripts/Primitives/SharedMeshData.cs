using UnityEngine;
using System.Collections.Generic;
using System;
using UnityEngine.Serialization;
//Each instance creates a BLAS. All the GPU knows is the positions of each BLAS instance, it only contains one for each unqiue mesh
[ExecuteAlways]
public class SharedMeshData
{
    [Header("BVH Build Settings")]
    public int maxLeafSize = 4;
    public int bvhMaxDepth = 64;
    public int numBins = 16;
    public UnityEngine.Mesh mesh;
    [NonSerialized] public buildTri[] buildTriangles;
    [NonSerialized] public List<GPUBVHNode> GPUBVH;
    public BLASBuilder blas;
    
    public Bounds modelBounds;

    public SharedMeshData(Mesh mesh)
    {
        if (blas == null)
            blas = new BLASBuilder();

        // Local-space bounds directly from mesh
        modelBounds = mesh.bounds;
        this.mesh = mesh;

        if (blas.Nodes == null)
        {
            buildTriangles = new buildTri[mesh.GetIndexCount(0) / 3];
            PerfTimer.Time("Population of build triangles: in model " + mesh.name, () => PopulateBuildTriangles());

            BvhBuildSettings settings = new BvhBuildSettings
            {
                maxLeafSize = maxLeafSize,
                maxDepth = bvhMaxDepth,
                numBins = numBins
            };

            PerfTimer.Time("BLAS assembly of " + mesh.name, () => blas.Build(mesh, settings));
            PerfTimer.Time("Flattening of BVH for GPU in model " + mesh.name, () => GPUBVH = FlattenBLASForGPU(blas));

        }
    }
    
    void PopulateBuildTriangles()
    {
        int[] triangles = mesh.GetTriangles(0);
        Vector3[] vertices = mesh.vertices;
        Vector3[] normals = mesh.normals;
        bool hasNormals = normals != null && normals.Length == vertices.Length;

        for (int i = 0; i < triangles.Length; i += 3)
        {
            int v0Index = triangles[i];
            int v1Index = triangles[i + 1];
            int v2Index = triangles[i + 2];

            Vector3 v0 = vertices[v0Index];
            Vector3 v1 = vertices[v1Index];
            Vector3 v2 = vertices[v2Index];

            Vector3 centroid = (v0 + v1 + v2) / 3f;
            Bounds bounds = new Bounds(centroid, Vector3.zero);
            bounds.Encapsulate(v0);
            bounds.Encapsulate(v1);
            bounds.Encapsulate(v2);

            Vector3 eAB = v1 - v0;
            Vector3 eAC = v2 - v0;
            Vector3 g = Vector3.Cross(eAB, eAC);
            Vector3 fallbackNormal = g.sqrMagnitude > 1e-12f ? g.normalized : Vector3.up;

            buildTriangles[i / 3] = new buildTri
            {
                posA = v0,
                posB = v1,
                posC = v2,
                n1 = hasNormals ? normals[v0Index] : fallbackNormal,
                n2 = hasNormals ? normals[v1Index] : fallbackNormal,
                n3 = hasNormals ? normals[v2Index] : fallbackNormal,
                bounds = bounds,
                centroid = centroid,
                triangleIndex = i,
            };
            
        }
    }
    //Turn the recursive BLAS into something the GPU can read
    List<GPUBVHNode> FlattenBLASForGPU(BLASBuilder builder)
    {
        var result = new List<GPUBVHNode>();
        if (builder == null || builder.Nodes == null || builder.Nodes.Length == 0)
            return result;

        for (int i = 0; i < builder.Nodes.Length; i++)
        {
            BvhNode node = builder.Nodes[i];

            result.Add(new GPUBVHNode
            {
                left = node.leftChild,
                right = node.rightChild,
                firstIndex = (uint)node.start,
                count = (uint)node.count,

                AABBLeftX = node.bounds.min.x,
                AABBLeftY = node.bounds.min.y,
                AABBLeftZ = node.bounds.min.z,

                AABBRightX = node.bounds.max.x,
                AABBRightY = node.bounds.max.y,
                AABBRightZ = node.bounds.max.z,
            });
        }

        return result;
    }
}