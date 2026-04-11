using UnityEngine;
using System.Collections.Generic;
using System;
using UnityEngine.Serialization;

//Apply this to objects to make the raytracer see them
[ExecuteAlways]
public class RayTracedMesh : MonoBehaviour
{
    public Matrix4x4 localToWorld;
    public Matrix4x4 worldToLocal;

    public SharedMeshData sharedMesh;

    public RayTracingMaterial material;
    [HideInInspector] public bool update = true;
    [HideInInspector] public bool transformDirty = true;
    public bool rebuildBVH = false;
    
    public bool drawBVH = false;
    public static int maxDepth = 1;
    public int numVertices;
    public int numNormals;
    public int numTriangles;
    public static readonly List<RayTracedMesh> All = new();

    void OnEnable()  { All.Add(this);    }
    void OnDisable() { All.Remove(this); }
    void Start()
    {
        RebuildStaticData();
        transformDirty = true;
    }

    void OnValidate()
    {
        if (rebuildBVH)
        {
            SharedMeshRegistry.DeleteKey(sharedMesh.mesh);
        }

        RebuildStaticData();
        rebuildBVH = false;
        transformDirty = true;
        update = true;

        if (material.emissiveStrength > 0)
        {
            transform.tag = "Light Source";
        }
    }

    void Update()
    {
        if (transform.hasChanged)
        {
            transformDirty = true;
            transform.hasChanged = false;
        }
    }

    public void RebuildStaticData()
    {
        //this is possibly the most hacky thing I have ever written
        //If we haven't been initalized yet, force SharedMeshRegistry to give us a sharedMesh
        //No I will NOT VIBE CODE IT
        Mesh mesh = GetComponent<MeshFilter>().sharedMesh;
        if (sharedMesh == null)
        {
            sharedMesh = SharedMeshRegistry.GetOrCreate(mesh);
        }
    }
}