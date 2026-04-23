using UnityEngine;
using System;
using System.Collections.Generic;
using UnityEngine.Rendering;
public partial class RayTracingManagerWavefront
{
    void BuildAccelStructure(List<RayTracedMesh> meshes)
    {
        if (accelStructure != null) accelStructure.Release();

        accelStructure = new RayTracingAccelerationStructure(new RayTracingAccelerationStructure.Settings
        {
            rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything,
            managementMode     = RayTracingAccelerationStructure.ManagementMode.Manual,
            layerMask          = 255
        });

        lastBuiltMeshOrderList.Clear();
        lastBuiltMeshOrderList.AddRange(meshes);

        if (accelStructureInstanceIDs == null || accelStructureInstanceIDs.Length != meshes.Count)
            accelStructureInstanceIDs = new int[meshes.Count];

        if (tlasGpuInstances.Length != meshes.Count)
            tlasGpuInstances = new MeshStruct[meshes.Count];

        // uniqueMeshesCache and offsetsCache already populated by caller
        BuildHardwareGeometryBuffers(uniqueMeshesCache, offsetsCache);

        for (int i = 0; i < meshes.Count; i++)
        {
            RayTracedMesh m        = meshes[i];
            MeshRenderer  renderer = m.GetComponent<MeshRenderer>();
            MeshFilter    filter   = m.GetComponent<MeshFilter>();
            if (renderer == null || filter == null) continue;

            accelStructureInstanceIDs[i] = accelStructure.AddInstance(
                new RayTracingMeshInstanceConfig
                {
                    mesh                 = filter.sharedMesh,
                    subMeshIndex         = 0,
                    material             = renderer.sharedMaterial,
                    enableTriangleCulling = false,
                },
                m.transform.localToWorldMatrix, null, (uint)i);

            tlasGpuInstances[i].localToWorldMatrix = m.transform.localToWorldMatrix;
            tlasGpuInstances[i].worldToLocalMatrix = m.transform.worldToLocalMatrix;
            tlasGpuInstances[i].material           = m.material;
            tlasGpuInstances[i].firstBVHNodeIndex  = 0;
            tlasGpuInstances[i].triangleOffset     = (uint)offsetsCache[m.sharedMesh].triangleOffset;
        }

        ShaderHelper.UploadStructuredBuffer(ref InstanceBuffer, tlasGpuInstances);
        
        //if (neeCompute     != null) neeCompute.SetBuffer(0, "Instances", InstanceBuffer);
        //if (neeCompute     != null) neeCompute.SetBuffer(0, "Instances", InstanceBuffer);

        accelStructure.Build();
    }
    void BuildHardwareGeometryBuffers(List<SharedMeshData> uniqueMeshes, Dictionary<SharedMeshData, MeshOffsets> offsets)
    {
        int totalVerts = 0, totalTris = 0;
        for (int i = 0; i < uniqueMeshes.Count; i++)
        {
            totalVerts += uniqueMeshes[i].mesh.vertexCount;
            totalTris  += uniqueMeshes[i].mesh.triangles.Length / 3;
        }

        blasVertices.Clear();
        blasNormals.Clear();

        int triCount = Mathf.Max(1, totalTris);
        Triangle[] triangles       = new Triangle[triCount];
        uint[]     triangleIndices = new uint[triCount * 3];

        for (int i = 0; i < uniqueMeshes.Count; i++)
        {
            SharedMeshData sharedMesh = uniqueMeshes[i];
            MeshOffsets    off        = offsets[sharedMesh];
            Mesh           mesh       = sharedMesh.mesh;

            mesh.GetVertices(tV);
            mesh.GetNormals(tN);

            for (int v = 0; v < tV.Count; v++) blasVertices.Add(tV[v]);
            if (tN.Count == tV.Count)
                for (int n = 0; n < tN.Count; n++) blasNormals.Add(tN[n]);
            else
                for (int n = 0; n < tV.Count; n++) blasNormals.Add(Vector3.up);

            int[] meshTris    = mesh.triangles;
            int   rawTriCount = meshTris.Length / 3;

            for (int t = 0; t < rawTriCount; t++)
            {
                int globalTriIndex = off.triangleOffset + t;
                int triIndexBase   = globalTriIndex * 3;
                int v0 = meshTris[t * 3 + 0];
                int v1 = meshTris[t * 3 + 1];
                int v2 = meshTris[t * 3 + 2];

                triangles[globalTriIndex] = new Triangle
                {
                    baseIndex = (uint)triIndexBase,
                    edgeAB    = (Vector3)tV[v1] - (Vector3)tV[v0],
                    edgeAC    = (Vector3)tV[v2] - (Vector3)tV[v0],
                };

                triangleIndices[triIndexBase + 0] = (uint)(off.vertexOffset + v0);
                triangleIndices[triIndexBase + 1] = (uint)(off.vertexOffset + v1);
                triangleIndices[triIndexBase + 2] = (uint)(off.vertexOffset + v2);
            }

            tV.Clear();
            tN.Clear();
        }

        ShaderHelper.CreateStructuredBuffer(ref MeshVerticesBuffer, blasVertices);
        ShaderHelper.CreateStructuredBuffer(ref MeshNormalsBuffer,  blasNormals);
        ShaderHelper.CreateStructuredBuffer(ref TriangleBuffer,     triangles);
        ShaderHelper.CreateStructuredBuffer(ref MeshIndicesBuffer,  triangleIndices);
    }
    void ComputeHardwareMeshOffsetsCached(List<SharedMeshData> sharedMeshes, Dictionary<SharedMeshData, MeshOffsets> result)
    {
        result.Clear();
        int vertexOffset = 0, triangleOffset = 0;
        for (int i = 0; i < sharedMeshes.Count; i++)
        {
            SharedMeshData mesh = sharedMeshes[i];
            result.Add(mesh, new MeshOffsets
            {
                vertexOffset   = vertexOffset,
                triangleOffset = triangleOffset,
                blasNodeOffset = 0,
                rootNodeIndex  = 0
            });
            vertexOffset   += mesh.mesh.vertexCount;
            triangleOffset += mesh.mesh.triangles.Length / 3; // raw order, not BVH order
        }
    }
}
