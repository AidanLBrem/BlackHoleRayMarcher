using UnityEngine;
using System.Collections.Generic;
using System;
using UnityEngine.Serialization;

[ExecuteAlways]
public class RayTracedMesh : MonoBehaviour
{
    public UnityEngine.Mesh mesh;
    [NonSerialized] public buildTri[] buildTriangles;

    public RayTracingMaterial material;
    [HideInInspector] public bool update = true;
    public bool rebuildBVH = false;

    // Old:
    // [FormerlySerializedAs("BVH")] public BLASCreator blas;

    // New:
    [FormerlySerializedAs("BVH")] public BLASBuilder blas;

    public Bounds ModelBounds;
    [NonSerialized] public List<GPUBVHNode> GPUBVH;
    public int largestAxis;
    public bool drawBVH = false;
    public static int maxDepth = 1;
    public int numVertices;
    public int numNormals;
    public int numTriangles;

    [Header("BVH Build Settings")]
    public int maxLeafSize = 4;
    public int bvhMaxDepth = 64;
    public int numBins = 16;

    void Start()
    {
        RebuildStaticData();
    }

    void OnValidate()
    {
        RebuildStaticData();
    }

    void Update()
    {
        // Transform changes are now handled on the GPU;
        // no per-frame rebuild of vertices/BVH here.
    }

    void RebuildStaticData()
    {
        mesh = GetComponent<MeshFilter>()?.sharedMesh;
        if (mesh == null)
            return;

        // BLASBuilder is a plain class, not a component.
        if (blas == null)
            blas = new BLASBuilder();

        // Local-space bounds directly from mesh
        ModelBounds = mesh.bounds;

        if (blas.Nodes == null || rebuildBVH)
        {
            buildTriangles = new buildTri[mesh.GetIndexCount(0) / 3];
            PerfTimer.Time("Population of build triangles: in model " + transform.name, () => PopulateBuildTriangles());

            numVertices = mesh.vertexCount;
            numNormals = mesh.normals != null ? mesh.normals.Length : 0;
            numTriangles = mesh.triangles != null ? mesh.triangles.Length : 0;

            BvhBuildSettings settings = new BvhBuildSettings
            {
                maxLeafSize = maxLeafSize,
                maxDepth = bvhMaxDepth,
                numBins = numBins
            };

            PerfTimer.Time("BLAS assembly of " + transform.name, () => blas.Build(mesh, settings));
            PerfTimer.Time("Flattening of BVH for GPU in model " + transform.name, () => GPUBVH = FlattenBLASForGPU(blas));

            update = true;
            rebuildBVH = false;
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

            if (!float.IsFinite(g.x) || !float.IsFinite(g.y) || !float.IsFinite(g.z) || g.sqrMagnitude < 1e-12f)
            {
                Debug.LogWarning(
                    $"Degenerate tri in {name}: triOffset={i}, indices=({v0Index},{v1Index},{v2Index})\n" +
                    $"v0=({v0.x:R}, {v0.y:R}, {v0.z:R}) bits=({BitConverter.SingleToInt32Bits(v0.x)}, {BitConverter.SingleToInt32Bits(v0.y)}, {BitConverter.SingleToInt32Bits(v0.z)})\n" +
                    $"v1=({v1.x:R}, {v1.y:R}, {v1.z:R}) bits=({BitConverter.SingleToInt32Bits(v1.x)}, {BitConverter.SingleToInt32Bits(v1.y)}, {BitConverter.SingleToInt32Bits(v1.z)})\n" +
                    $"v2=({v2.x:R}, {v2.y:R}, {v2.z:R}) bits=({BitConverter.SingleToInt32Bits(v2.x)}, {BitConverter.SingleToInt32Bits(v2.y)}, {BitConverter.SingleToInt32Bits(v2.z)})\n" +
                    $"area=({g.sqrMagnitude})\n" +
                    $"cross=({g.x:R}, {g.y:R}, {g.z:R})"
                );
            }
        }
    }

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