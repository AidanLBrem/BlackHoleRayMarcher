using UnityEngine;
using System.Collections.Generic; // Required for List<T>
[ExecuteAlways]
public class RayTracedMesh : MonoBehaviour
{
    public List<int> triangles;
    public List<Vector3> vertices;
    public List<Vector3> normals;
    [SerializeField] public List<buildTri> buildTriangles;
    public int numVertices;
    public RayTracingMaterial material;
    public bool update = true;
    public BVHCreator BVH;
    public Bounds ModelBounds;
    [SerializeField] public List<GPUBVHNode> GPUBVH;
    public int largestAxis;
    public bool drawBVH = false;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if (transform.hasChanged) {
            updateParameters();
        }
        transform.hasChanged = false;
    }

    void OnValidate() {
        updateParameters();
        transform.hasChanged = false;
    }

    void OnDrawGizmosSelected() {
        if (GPUBVH.Count > 0 && drawBVH) {
            Stack<int> nodesStack = new Stack<int>();
            nodesStack.Push(0);
            while (nodesStack.Count > 0) {
                int nodeIndex = nodesStack.Pop();
                GPUBVHNode node = GPUBVH[nodeIndex];
                if (node.left != -1) {
                    nodesStack.Push(node.left);
                }
                if (node.right != -1) {
                    nodesStack.Push(node.right);
                }

                else {
                    // Assign a deterministic color based on the nodeIndex, so it's constant across frames for each node
                    long seed = nodeIndex * 2654435761; // Knuth's multiplicative hash, simple but decent
                    float r = ((seed >> 16) & 0xFF) / 255f;
                    float g = ((seed >> 8) & 0xFF) / 255f;
                    float b = (seed & 0xFF) / 255f;
                    Gizmos.color = new Color(r, g, b);
                    for (int i = node.firstTriangleIndex; i < node.firstTriangleIndex + node.triangleCount; i++) {
                        Gizmos.DrawLine(buildTriangles[i].posA, buildTriangles[i].posB);
                        Gizmos.DrawLine(buildTriangles[i].posB, buildTriangles[i].posC);
                        Gizmos.DrawLine(buildTriangles[i].posC, buildTriangles[i].posA);
                    }
                }
            
            }
        }
    }

    void updateParameters() {
        ModelBounds = new Bounds(transform.position, Vector3.zero);
        buildTriangles = new List<buildTri>();
        if (!TryGetComponent(out BVH)) {
            BVH = gameObject.AddComponent<BVHCreator>();
        }
        ModelBounds = new Bounds(transform.position, Vector3.zero);
        Debug.Log("updateParameters" + transform.name);
        UnityEngine.Mesh mesh = GetComponent<MeshFilter>().sharedMesh;
        vertices = new List<Vector3>();
        for (int i = 0; i < mesh.vertices.Length; i++) {
            vertices.Add(transform.TransformPoint(mesh.vertices[i]));
            ModelBounds.Encapsulate(vertices[i]);
        }
        triangles = new List<int>(mesh.triangles);
        normals = new List<Vector3>();
        for (int i = 0; i < mesh.normals.Length; i++) {
            normals.Add(transform.TransformDirection(mesh.normals[i]).normalized);
        }

        populateBuldTriangles();

        numVertices = vertices.Count;
        update = true;
    }

    void populateBuldTriangles() {
        for (int i = 0; i < triangles.Count; i+=3) {
            int vertexIndex1 = triangles[i];
            int vertexIndex2 = triangles[i+1];
            int vertexIndex3 = triangles[i+2];
            Vector3 centroid = (vertices[vertexIndex1] + vertices[vertexIndex2] + vertices[vertexIndex3]) / 3;
            Bounds bounds = new Bounds(centroid, Vector3.zero);
            bounds.Encapsulate(vertices[vertexIndex1]);
            bounds.Encapsulate(vertices[vertexIndex2]);
            bounds.Encapsulate(vertices[vertexIndex3]);
            buildTriangles.Add(new buildTri() {
                posA = vertices[vertexIndex1],
                posB = vertices[vertexIndex2],
                posC = vertices[vertexIndex3],
                bounds = bounds,
                centroid = centroid,
                triangleIndex = i,
            });
        }

        sortBasedOnCentroids();
        BVH.StartBVHConstruction();
        GPUBVH = BVH.flattenBVH();
    }

    void sortBasedOnCentroids() {
        var s = ModelBounds.size;
        if (s.y > s[largestAxis]) {
            largestAxis = 1;
        }
        if (s.z > s[largestAxis]) {
            largestAxis = 2;
        }
        buildTriangles.Sort((a, b) => a.centroid[largestAxis].CompareTo(b.centroid[largestAxis]));
    }

}
