using System.Collections.Generic;
using UnityEngine;
using Debug = UnityEngine.Debug;
using static RaytracerCPURay;

public partial class RayTracingManagerWavefront
{
    void GetValidMeshInstancesCached(List<RayTracedMesh> result)
    {
        result.Clear();
        List<RayTracedMesh> allMeshes = RayTracedMesh.All;
        for (int i = 0; i < allMeshes.Count; i++)
        {
            RayTracedMesh m = allMeshes[i];
            if (m == null) continue;
            if (m.sharedMesh == null) m.RebuildStaticData();
            if (m.sharedMesh == null) continue;
            if (m.sharedMesh.mesh == null) continue;
            if (m.sharedMesh.buildTriangles == null || m.sharedMesh.buildTriangles.Length == 0) continue;
            if (m.sharedMesh.blas == null) continue;
            if (m.sharedMesh.blas.Nodes == null || m.sharedMesh.blas.Nodes.Length == 0) continue;
            if (m.sharedMesh.blas.PrimitiveRefs == null || m.sharedMesh.blas.PrimitiveRefs.Length == 0) continue;
            if (m.sharedMesh.GPUBVH == null || m.sharedMesh.GPUBVH.Count == 0) continue;
            result.Add(m);
        }
    }

    void GetUniqueSharedMeshesCached(List<RayTracedMesh> instances, List<SharedMeshData> result)
    {
        result.Clear();
        uniqueMeshesSet.Clear();
        for (int i = 0; i < instances.Count; i++)
        {
            SharedMeshData sm = instances[i].sharedMesh;
            if (sm == null) continue;
            if (uniqueMeshesSet.Add(sm)) result.Add(sm);
        }
    }

    void ComputeMeshOffsetsCached(List<SharedMeshData> sharedMeshes, Dictionary<SharedMeshData, MeshOffsets> result)
    {
        result.Clear();
        int vertexOffset = 0, triangleOffset = 0, blasOffset = 0;
        for (int i = 0; i < sharedMeshes.Count; i++)
        {
            SharedMeshData mesh = sharedMeshes[i];
            result.Add(mesh, new MeshOffsets
            {
                vertexOffset = vertexOffset,
                triangleOffset = triangleOffset,
                blasNodeOffset = blasOffset,
                rootNodeIndex = blasOffset + mesh.blas.RootIndex
            });
            vertexOffset += mesh.mesh.vertexCount;
            triangleOffset += mesh.blas.PrimitiveRefs.Length;
            blasOffset += mesh.GPUBVH.Count;
        }
    }
    void BuildGlobalBLASGeometry(
        List<SharedMeshData> sharedMeshes,
        List<RayTracedMesh> validInstances,
        Dictionary<SharedMeshData, MeshOffsets> offsets)
    {
        int totalVertexCount = 0;
        int totalTris = 0;
        totalBVHNodes = 0;
        for (int i = 0; i < sharedMeshes.Count; i++)
        {
            totalVertexCount += sharedMeshes[i].mesh.vertexCount;
            totalTris += sharedMeshes[i].blas.PrimitiveRefs.Length;
            totalBVHNodes += sharedMeshes[i].GPUBVH.Count;
        }

        bool anyMeshMarkedUpdated = AnyInstanceNeedsUpdate(validInstances);
        bool needTriangles = !buffersHaveRealData || TriangleBuffer == null || TriangleBuffer.count != Mathf.Max(1, totalTris) || anyMeshMarkedUpdated;
        bool needBVH = !buffersHaveRealData || BVHBuffer == null || BVHBuffer.count != Mathf.Max(1, totalBVHNodes) || anyMeshMarkedUpdated;
        bool needVertices = !buffersHaveRealData || MeshVerticesBuffer == null || MeshNormalsBuffer == null || MeshIndicesBuffer == null || anyMeshMarkedUpdated;
        if (!(needTriangles || needBVH || needVertices)) return;

        tlasDirty = true;
        float startTime = Time.realtimeSinceStartup;

        int triCount = Mathf.Max(1, totalTris);
        int blasCount = Mathf.Max(1, totalBVHNodes);

        if (blasTriangles.Length != triCount) blasTriangles = new Triangle[triCount];
        if (blasTriangleIndices.Length != triCount * 3) blasTriangleIndices = new uint[triCount * 3];
        if (blasBVHNodes.Length != blasCount) blasBVHNodes = new GPUBVHNode[blasCount];
        blasVertices.Clear();
        blasNormals.Clear();

        int degenerateTriangles = 0;

        for (int i = 0; i < sharedMeshes.Count; i++)
        {
            SharedMeshData sharedMesh = sharedMeshes[i];
            MeshOffsets off = offsets[sharedMesh];

            sharedMesh.mesh.GetVertices(tV);
            sharedMesh.mesh.GetNormals(tN);

            for (int v = 0; v < tV.Count; v++) blasVertices.Add(tV[v]);
            if (tN.Count == tV.Count)
                for (int n = 0; n < tN.Count; n++) blasNormals.Add(tN[n]);
            else
                for (int n = 0; n < tV.Count; n++) blasNormals.Add(Vector3.up);

            int[] meshTriangles = sharedMesh.mesh.triangles;
            int[] order = sharedMesh.blas.PrimitiveRefs;

            for (int t = 0; t < order.Length; t++)
            {
                ref buildTri bt = ref sharedMesh.buildTriangles[order[t]];
                int globalTriIndex = off.triangleOffset + t;
                int triIndexBase = globalTriIndex * 3;

                blasTriangles[globalTriIndex] = new Triangle
                {
                    baseIndex = (uint)triIndexBase,
                    edgeAB = bt.posB - bt.posA,
                    edgeAC = bt.posC - bt.posA,
                };

                blasTriangleIndices[triIndexBase + 0] = (uint)(off.vertexOffset + meshTriangles[bt.triangleIndex + 0]);
                blasTriangleIndices[triIndexBase + 1] = (uint)(off.vertexOffset + meshTriangles[bt.triangleIndex + 1]);
                blasTriangleIndices[triIndexBase + 2] = (uint)(off.vertexOffset + meshTriangles[bt.triangleIndex + 2]);
            }

            for (int j = 0; j < sharedMesh.GPUBVH.Count; j++)
            {
                GPUBVHNode node = sharedMesh.GPUBVH[j];
                if (node.left != -1) node.left += off.blasNodeOffset;
                if (node.right != -1) node.right += off.blasNodeOffset;
                node.firstIndex += (uint)off.triangleOffset;
                blasBVHNodes[off.blasNodeOffset + j] = node;
            }

            tV.Clear();
            tN.Clear();
        }

        PerfTimer.Time("StructuredVertexBufferCreation", () =>
            ShaderHelper.CreateStructuredBuffer(ref MeshVerticesBuffer, blasVertices));

        ShaderHelper.CreateStructuredBuffer(ref MeshNormalsBuffer,  blasNormals);
        ShaderHelper.CreateStructuredBuffer(ref TriangleBuffer,     blasTriangles);
        ShaderHelper.CreateStructuredBuffer(ref BVHBuffer,          blasBVHNodes);
        ShaderHelper.CreateStructuredBuffer(ref MeshIndicesBuffer,  blasTriangleIndices);

        /*if (classifyCompute != null)
        {
            classifyCompute.SetBuffer(0, "Triangles",       TriangleBuffer);
            classifyCompute.SetBuffer(0, "BVHNodes",        BVHBuffer);
            classifyCompute.SetBuffer(0, "Vertices",        MeshVerticesBuffer);
            classifyCompute.SetBuffer(0, "Normals",         MeshNormalsBuffer);
            classifyCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
            classifyCompute.SetInt("numBLASNodes",          totalBVHNodes);
        }

        if (reflectionCompute != null)
        {
            reflectionCompute.SetBuffer(0, "Triangles",       TriangleBuffer);
            reflectionCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
            reflectionCompute.SetBuffer(0, "Normals",         MeshNormalsBuffer);
            reflectionCompute.SetBuffer(0, "BVHNodes",        BVHBuffer);
        }*/

        for (int i = 0; i < validInstances.Count; i++) validInstances[i].update = false;

        Debug.Log($"BLAS/global geometry upload took {(Time.realtimeSinceStartup - startTime) * 1000f:F3} ms, triangles: {TriangleBuffer.count}");
        Debug.Log($"Warning: {degenerateTriangles} degenerate triangles detected");
    }

    void BuildAndUploadTLAS(
        List<RayTracedMesh> meshObjects,
        Dictionary<SharedMeshData, MeshOffsets> offsets)
    {
        if (!tlasDirty && TLASBuffer != null && buffersHaveRealData) return;
        tlasDirty = false;
 

        // Software-only: build CPU-side TLAS BVH and upload TLASBuffer / TLASRefBuffer
        if (tlasBuilder == null) tlasBuilder = new TLASBuilder();
        tlasBuilder.Build(tlasInstances, new BvhBuildSettings
        {
            maxLeafSize = tlasMaxLeafSize,
            maxDepth    = tlasMaxDepth,
            numBins     = tlasNumBins
        });
 
        if (tlasNodesCache.Length != tlasBuilder.Nodes.Length)
            tlasNodesCache = new GPUBVHNode[tlasBuilder.Nodes.Length];
 
        for (int i = 0; i < tlasBuilder.Nodes.Length; i++)
        {
            BvhNode n = tlasBuilder.Nodes[i];
            tlasNodesCache[i] = new GPUBVHNode
            {
                left       = n.leftChild,
                right      = n.rightChild,
                firstIndex = (uint)n.start,
                count      = (uint)n.count,
                AABBLeftX  = n.bounds.min.x,
                AABBLeftY  = n.bounds.min.y,
                AABBLeftZ  = n.bounds.min.z,
                AABBRightX = n.bounds.max.x,
                AABBRightY = n.bounds.max.y,
                AABBRightZ = n.bounds.max.z,
            };
        }
 
        if (tlasRefsCache.Length != tlasBuilder.PrimitiveRefs.Length)
            tlasRefsCache = new uint[tlasBuilder.PrimitiveRefs.Length];
 
        for (int i = 0; i < tlasRefsCache.Length; i++)
            tlasRefsCache[i] = (uint)tlasBuilder.PrimitiveRefs[i];
 
        ShaderHelper.UploadStructuredBuffer(ref TLASBuffer,    tlasNodesCache);
        ShaderHelper.UploadStructuredBuffer(ref TLASRefBuffer, tlasRefsCache);
 
        buffersHaveRealData = true;
    }
    
    void BuildInstancesAndLights(
        List<RayTracedMesh> meshObjects,
        Dictionary<SharedMeshData, MeshOffsets> offsets)
    {
        int count = meshObjects.Count;
 
        if (tlasGpuInstances.Length != count) tlasGpuInstances  = new MeshStruct[count];
        if (tlasInstances.Length    != count) tlasInstances      = new BvhInstance[count];
        if (tlasLightSources.Length != count) tlasLightSources   = new GPULightSource[count];
 
        lightTriIndicesCache.Clear();
        lightTriDataCache.Clear();
        numLightSources = 0;
 
        for (int i = 0; i < count; i++) meshObjects[i].transformDirty = false;
 
        for (int i = 0; i < count; i++)
        {
            RayTracedMesh  meshObj = meshObjects[i];
            MeshOffsets    off     = offsets[meshObj.sharedMesh];
            int            localRootIndex   = meshObj.sharedMesh.blas.RootIndex;
            Bounds         localRootBounds  = meshObj.sharedMesh.blas.Nodes[localRootIndex].bounds;
            Bounds         worldBounds      = TransformBoundsToWorld(localRootBounds, meshObj.transform);
            int            globalBlasRootIndex = off.rootNodeIndex;
 
            tlasInstances[i] = new BvhInstance
            {
                blasIndex      = i,
                blasRootIndex  = globalBlasRootIndex,
                localToWorld   = meshObj.transform.localToWorldMatrix,
                worldToLocal   = meshObj.transform.worldToLocalMatrix,
                localBounds    = localRootBounds,
                worldBounds    = worldBounds,
                materialIndex  = i
            };
 
            tlasGpuInstances[i] = new MeshStruct
            {
                localToWorldMatrix = meshObj.transform.localToWorldMatrix,
                worldToLocalMatrix = meshObj.transform.worldToLocalMatrix,
                material           = meshObj.material,
                firstBVHNodeIndex  = (uint)globalBlasRootIndex,
                triangleOffset     = (uint)off.triangleOffset,
                AABBLeftX  = worldBounds.min.x,
                AABBLeftY  = worldBounds.min.y,
                AABBLeftZ  = worldBounds.min.z,
                AABBRightX = worldBounds.max.x,
                AABBRightY = worldBounds.max.y,
                AABBRightZ = worldBounds.max.z,
            };
 
            if (meshObj.material.emissiveStrength > 0)
            {
                Matrix4x4      l2w      = meshObj.transform.localToWorldMatrix;
                SharedMeshData sm       = meshObj.sharedMesh;
                int            triStart = lightTriIndicesCache.Count;
                float          totalArea = 0f;
                int[]          order    = sm.blas.PrimitiveRefs;
 
                for (int t = 0; t < order.Length; t++)
                {
                    ref buildTri bt      = ref sm.buildTriangles[order[t]];
                    Vector3      worldAB = l2w.MultiplyVector(bt.posB - bt.posA);
                    Vector3      worldAC = l2w.MultiplyVector(bt.posC - bt.posA);
                    Vector3      worldCross = Vector3.Cross(worldAB, worldAC);
                    float        area    = worldCross.magnitude * 0.5f;
                    totalArea += area;
 
                    int globalTriIndex = off.triangleOffset + order[t];
                    lightTriIndicesCache.Add(globalTriIndex);
 
                    while (lightTriDataCache.Count <= globalTriIndex)
                        lightTriDataCache.Add(new GPULightTriangleData());
 
                    lightTriDataCache[globalTriIndex] = new GPULightTriangleData
                    {
                        worldSpaceArea = area,
                        worldNormal    = worldCross.magnitude > 1e-10f
                            ? worldCross.normalized
                            : Vector3.up
                    };
                }
 
                tlasLightSources[numLightSources++] = new GPULightSource
                {
                    instanceIndex = i,
                    totalArea     = totalArea,
                    triStart      = triStart,
                    triCount      = order.Length
                };
            }
        }
 
        // Upload the shared buffers — both paths need these
        if (lightTriDataCache.Count == 0) lightTriDataCache.Add(new GPULightTriangleData());
 
        ShaderHelper.CreateStructuredBuffer<GPULightSource>(ref LightSourceBuffer, Mathf.Max(1, numLightSources));
        if (numLightSources > 0) LightSourceBuffer.SetData(tlasLightSources, 0, 0, numLightSources);
 
        ShaderHelper.UploadStructuredBuffer(ref InstanceBuffer,              tlasGpuInstances);
        ShaderHelper.UploadStructuredBuffer(ref LightTriangleIndicesBuffer,  lightTriIndicesCache);
        ShaderHelper.UploadStructuredBuffer(ref LightTrianglesDataBuffer,    lightTriDataCache);
    }
}
