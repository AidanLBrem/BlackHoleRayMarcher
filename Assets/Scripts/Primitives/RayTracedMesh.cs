using UnityEngine;
using System.Collections.Generic;
using System;
[ExecuteAlways]
public class RayTracedMesh : MonoBehaviour
{
    public UnityEngine.Mesh mesh; 
    [NonSerialized] public buildTri[] buildTriangles;
    public int numVertices;
    public RayTracingMaterial material;
    public bool update = true;
    public BVHCreator BVH;
    public Bounds ModelBounds;
    [NonSerialized] public List<GPUBVHNode> GPUBVH;
    public int largestAxis;
    public bool drawBVH = false;
    public static int maxDepth = 1;

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
        if (!TryGetComponent(out BVH))
        {
            BVH = gameObject.AddComponent<BVHCreator>();
        }

        mesh = GetComponent<MeshFilter>()?.sharedMesh;
        if (mesh == null)
            return;
        

        // Local-space bounds directly from mesh
        ModelBounds = mesh.bounds;

        // Build local-space triangle data
        buildTriangles = new buildTri[mesh.GetIndexCount(0) / 3];
        PerfTimer.Time("Population of build triangles: in model " + transform.name, () => PopulateBuildTriangles());

        numVertices = (int)mesh.GetIndexCount(0);

        // Build BVH once in local space
        BVH.StartBVHConstruction();
        PerfTimer.Time("Flattening of BVH for GPU in model " + transform.name, () => GPUBVH = BVH.flattenBVH());
        // Signal to your ray tracer that buffers need re-upload
        update = true;
    }

    void PopulateBuildTriangles()
    {
        int[] triangles = mesh.GetTriangles(0);
        Vector3[] vertices = mesh.vertices;
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

            buildTriangles[i / 3] = (new buildTri
            {
                posA = v0,
                posB = v1,
                posC = v2,
                bounds = bounds,
                centroid = centroid,
                triangleIndex = i,
            });
        }
    }
}