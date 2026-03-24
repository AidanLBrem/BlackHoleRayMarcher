using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Unity.Mathematics;
using Debug = UnityEngine.Debug;
using UnityEngine.Profiling;
using static RaytracerCPURay;

public static class SharedMeshRegistry
{
    private static Dictionary<Mesh, SharedMeshData> cache = new();

    public static SharedMeshData GetOrCreate(Mesh mesh)
    {
        if (mesh == null) return null;

        if (cache.TryGetValue(mesh, out var data))
            return data;

        data = new SharedMeshData(mesh);
        cache.Add(mesh, data);
        return data;
    }

    public static void DeleteKey(Mesh mesh)
    {
        cache.Remove(mesh);
    }
}

[ExecuteAlways, ImageEffectAllowedInSceneView]
public class RayTracingManager : MonoBehaviour
{
    [SerializeField] bool useShaderInSceneView = true;
    [SerializeField] public bool useTlas = true;
    public bool useRedshifting = true;
    [SerializeField] Shader rayTracingShader;
    [SerializeField] Shader accumulatorShader;

    [Header("TLAS Settings")]
    public int tlasMaxLeafSize = 2;
    public int tlasMaxDepth = 32;
    public int tlasNumBins = 8;

    Material rayTracingMaterial;
    Material accumulatorMaterial;

    public int marchStepsCount;
    public int renderDistance;
    public int raysPerPixel;
    public int maxBounces;
    public bool accumlateInSceneView = true;
    public bool accumulateInGameView = true;
    public float blackHoleSOIStepSize = 0.01f;

    [NonSerialized] ComputeBuffer sphereBuffer;
    [NonSerialized] ComputeBuffer blackHoleBuffer;

    [NonSerialized] ComputeBuffer MeshVerticesBuffer;
    [NonSerialized] ComputeBuffer MeshNormalsBuffer;
    [NonSerialized] ComputeBuffer MeshIndicesBuffer;
    [NonSerialized] ComputeBuffer TriangleBuffer;
    [NonSerialized] ComputeBuffer BVHBuffer;

    [NonSerialized] ComputeBuffer TLASBuffer;
    [NonSerialized] ComputeBuffer TLASRefBuffer;
    [NonSerialized] ComputeBuffer InstanceBuffer;

    [Header("Atmosphere")]
    public bool applyScattering = true;
    public bool applyRayleigh = true;
    public bool applyMie = true;
    public bool applySundisk = true;
    public bool applySunLighting = true;
    public float planetRadius = 6378137.0f;
    public float atmosphereRadius = 6538137.0f;
    public int framesPerScatter = 10;
    public int numOpticalDepthPoints = 8;
    public int inScatteringPoints = 8;

    [Header("Rayleigh Scattering")]
    public float densityFalloffRayleigh = 4f;
    public Vector3 rayleighScatteringCoefficients = new Vector3(0.0058f, 0.0135f, 0.0331f);

    [Header("Mie Scattering")]
    public float densityFalloffMie = 4f;
    public float mieForwardScatter = 0.76f;
    public float mieBackwardScatter = -0.5f;
    public Vector3 mieScatteringCoefficients = new Vector3(21e-6f, 21e-6f, 21e-6f);

    public Color sunLightColor = Color.white;
    public float sunLightIntensity = 1;
    public Transform sun;

    RenderTexture resultTexture;
    int numRenderedFrames = 0;

    public int emergencyBreakMaxSteps = 1000;
    public float numFrames = 10000;
    bool stopRendering = false;
    public float accumWeight = 1.0f;

    public bool renderSphere = true;
    public bool renderTriangles = true;

    public float strongFieldRadPerMeterCuttoff = 0.01f;

    static readonly List<Vector3> tV = new();
    static readonly List<Vector3> tN = new();

    [Header("Black Hole Lensing Debug Options")]
    public bool enable_lensing = true;
    public float bendStrength = 1.0f;
    public bool impactParameterDebug = false;
    public bool useOrbitalPlaneCullingIfAble = true;

    public bool displayTriTests = false;
    public int triTestFullSaturationValue = 1000;

    public bool displayBVHNodeTests = false;
    public int BVHNodeTestSaturationValue = 10;

    public bool displayTLASNodeVisits = false;
    public int TLASNodeVisitsSaturationValue = 1000;

    public bool displayBLASNodeVisits = false;
    public int BLASNodeVisitsSaturationValue = 1000;

    public bool displayInstanceBLASTraversals = false;
    public int InstanceBLASTraversalsSaturationValue = 1000;

    public bool displayTLASLeafRefs = false;
    public int TLASLeafRefsSaturationValue = 1000;
    // --- Accumulation tracking ---
    Vector3 lastCameraPosition;
    Quaternion lastCameraRotation;
    float lastCameraFov;
    int lastScreenWidth;
    int lastScreenHeight;
    bool historyInitialized = false;
    struct MeshOffsets
    {
        public int vertexOffset;
        public int triangleOffset;
        public int blasNodeOffset;
        public int rootNodeIndex;
    }
    bool ShouldResetAccumulation(Camera cam)
    {
        if (!historyInitialized)
        {
            lastCameraPosition = cam.transform.position;
            lastCameraRotation = cam.transform.rotation;
            lastCameraFov = cam.fieldOfView;
            lastScreenWidth = Screen.width;
            lastScreenHeight = Screen.height;
            historyInitialized = true;
            return true;
        }

        float posDelta = Vector3.Distance(cam.transform.position, lastCameraPosition);
        float rotDelta = Quaternion.Angle(cam.transform.rotation, lastCameraRotation);
        float fovDelta = Mathf.Abs(cam.fieldOfView - lastCameraFov);

        bool changed =
            posDelta > 0.0005f ||
            rotDelta > 0.05f ||
            fovDelta > 0.01f ||
            Screen.width != lastScreenWidth ||
            Screen.height != lastScreenHeight;

        if (changed)
        {
            lastCameraPosition = cam.transform.position;
            lastCameraRotation = cam.transform.rotation;
            lastCameraFov = cam.fieldOfView;
            lastScreenWidth = Screen.width;
            lastScreenHeight = Screen.height;
        }

        return changed;
    }
    void UpdateAtmosphereParams()
    {
        rayTracingMaterial.SetFloat("atmosphereRadius", atmosphereRadius);
        rayTracingMaterial.SetFloat("planetRadius", planetRadius);
        rayTracingMaterial.SetFloat("densityFalloffRayleigh", densityFalloffRayleigh);
        rayTracingMaterial.SetFloat("densityFalloffMie", densityFalloffMie);
        rayTracingMaterial.SetFloat("mieForwardScatter", mieForwardScatter);
        rayTracingMaterial.SetFloat("mieBackwardScatter", mieBackwardScatter);
        rayTracingMaterial.SetInt("numOpticalDepthPoints", numOpticalDepthPoints);
        rayTracingMaterial.SetInt("inScatteringPoints", inScatteringPoints);
        rayTracingMaterial.SetVector("rayleighScatteringCoefficients", rayleighScatteringCoefficients);
        rayTracingMaterial.SetVector("mieScatteringCoefficients", new Vector3(
            mieScatteringCoefficients.x,
            mieScatteringCoefficients.y,
            mieScatteringCoefficients.z));
        rayTracingMaterial.SetVector("sunLightColor", new Vector3(
            sunLightColor.r * sunLightIntensity,
            sunLightColor.g * sunLightIntensity,
            sunLightColor.b * sunLightIntensity));
        rayTracingMaterial.SetVector("sunDirection", sun != null ? Vector3.Normalize(-sun.forward) : Vector3.up);
        rayTracingMaterial.SetInt("framesPerScatter", framesPerScatter);

        if (!accumulateInGameView)
        {
            rayTracingMaterial.SetInt("framesPerScatter", 1);
        }

        DirectionalGeodesic2DLutSolver.StepSize = blackHoleSOIStepSize;
    }

    List<RayTracedMesh> GetValidMeshInstances()
    {
        RayTracedMesh[] allMeshes = FindObjectsOfType<RayTracedMesh>();
        List<RayTracedMesh> validMeshes = new(allMeshes.Length);

        for (int i = 0; i < allMeshes.Length; i++)
        {
            RayTracedMesh m = allMeshes[i];
            if (m == null)
                continue;

            if (m.sharedMesh == null)
                m.RebuildStaticData();

            if (m.sharedMesh == null)
                continue;
            if (m.sharedMesh.mesh == null)
                continue;
            if (m.sharedMesh.buildTriangles == null || m.sharedMesh.buildTriangles.Length == 0)
                continue;
            if (m.sharedMesh.blas == null)
                continue;
            if (m.sharedMesh.blas.Nodes == null || m.sharedMesh.blas.Nodes.Length == 0)
                continue;
            if (m.sharedMesh.blas.PrimitiveRefs == null || m.sharedMesh.blas.PrimitiveRefs.Length == 0)
                continue;
            if (m.sharedMesh.GPUBVH == null || m.sharedMesh.GPUBVH.Count == 0)
                continue;

            validMeshes.Add(m);
        }

        return validMeshes;
    }

    List<SharedMeshData> GetUniqueSharedMeshes(List<RayTracedMesh> validInstances)
    {
        HashSet<SharedMeshData> seen = new();
        List<SharedMeshData> unique = new();

        for (int i = 0; i < validInstances.Count; i++)
        {
            SharedMeshData sm = validInstances[i].sharedMesh;
            if (sm == null)
                continue;

            if (seen.Add(sm))
                unique.Add(sm);
        }

        return unique;
    }

    bool AnyInstanceNeedsUpdate(List<RayTracedMesh> validInstances)
    {
        for (int i = 0; i < validInstances.Count; i++)
        {
            if (validInstances[i].update)
                return true;
        }
        return false;
    }

    void BindDummyAccelerationBuffers()
    {
        ShaderHelper.CreateStructuredBuffer(ref TriangleBuffer, new Triangle[1]);
        ShaderHelper.CreateStructuredBuffer(ref BVHBuffer, new GPUBVHNode[1]);
        ShaderHelper.CreateStructuredBuffer(ref TLASBuffer, new GPUBVHNode[1]);
        ShaderHelper.CreateStructuredBuffer(ref TLASRefBuffer, new uint[1]);
        ShaderHelper.CreateStructuredBuffer(ref InstanceBuffer, new MeshStruct[1]);
        ShaderHelper.CreateStructuredBuffer(ref MeshVerticesBuffer, new Vector3[1]);
        ShaderHelper.CreateStructuredBuffer(ref MeshNormalsBuffer, new Vector3[1]);
        ShaderHelper.CreateStructuredBuffer(ref MeshIndicesBuffer, new uint[1]);

        rayTracingMaterial.SetBuffer("Triangles", TriangleBuffer);
        rayTracingMaterial.SetBuffer("BVHNodes", BVHBuffer);
        rayTracingMaterial.SetBuffer("TLASNodes", TLASBuffer);
        rayTracingMaterial.SetBuffer("TLASRefs", TLASRefBuffer);
        rayTracingMaterial.SetBuffer("Instances", InstanceBuffer);
        rayTracingMaterial.SetBuffer("Vertices", MeshVerticesBuffer);
        rayTracingMaterial.SetBuffer("Normals", MeshNormalsBuffer);
        rayTracingMaterial.SetBuffer("TriangleIndices", MeshIndicesBuffer);

        rayTracingMaterial.SetInt("numMeshes", 0);
        rayTracingMaterial.SetInt("numBLASNodes", 0);
        rayTracingMaterial.SetInt("numTLASNodes", 0);
        rayTracingMaterial.SetInt("numInstances", 0);
        rayTracingMaterial.SetInt("TLASRootIndex", 0);
    }

    void AllocateAccelerationBuffers()
    {
        List<RayTracedMesh> validInstances = GetValidMeshInstances();

        if (validInstances.Count == 0)
        {
            BindDummyAccelerationBuffers();
            return;
        }

        List<SharedMeshData> uniqueSharedMeshes = GetUniqueSharedMeshes(validInstances);
        Dictionary<SharedMeshData, MeshOffsets> offsets = ComputeMeshOffsets(uniqueSharedMeshes);

        BuildGlobalBLASGeometry(uniqueSharedMeshes, validInstances, offsets);
        BuildAndUploadTLAS(validInstances, offsets);
    }

    Dictionary<SharedMeshData, MeshOffsets> ComputeMeshOffsets(List<SharedMeshData> sharedMeshes)
    {
        Dictionary<SharedMeshData, MeshOffsets> offsets = new(sharedMeshes.Count);

        int vertexOffset = 0;
        int triangleOffset = 0;
        int blasOffset = 0;

        for (int i = 0; i < sharedMeshes.Count; i++)
        {
            SharedMeshData mesh = sharedMeshes[i];
            int localRootIndex = mesh.blas.RootIndex;

            MeshOffsets entry = new MeshOffsets
            {
                vertexOffset = vertexOffset,
                triangleOffset = triangleOffset,
                blasNodeOffset = blasOffset,
                rootNodeIndex = blasOffset + localRootIndex
            };

            offsets.Add(mesh, entry);

            vertexOffset += mesh.mesh.vertexCount;
            triangleOffset += mesh.blas.PrimitiveRefs.Length;
            blasOffset += mesh.GPUBVH.Count;
        }

        return offsets;
    }

    void BuildGlobalBLASGeometry(
        List<SharedMeshData> sharedMeshes,
        List<RayTracedMesh> validInstances,
        Dictionary<SharedMeshData, MeshOffsets> offsets)
    {
        int totalVertexCount = 0;
        int totalTris = 0;
        int totalBVHNodes = 0;

        for (int i = 0; i < sharedMeshes.Count; i++)
        {
            SharedMeshData sm = sharedMeshes[i];
            totalVertexCount += sm.mesh.vertexCount;
            totalTris += sm.blas.PrimitiveRefs.Length;
            totalBVHNodes += sm.GPUBVH.Count;
        }

        bool anyMeshMarkedUpdated = AnyInstanceNeedsUpdate(validInstances);

        bool needTriangles = TriangleBuffer == null || TriangleBuffer.count != Mathf.Max(1, totalTris) || anyMeshMarkedUpdated;
        bool needBVH = BVHBuffer == null || BVHBuffer.count != Mathf.Max(1, totalBVHNodes) || anyMeshMarkedUpdated;
        bool needVertices = MeshVerticesBuffer == null || MeshNormalsBuffer == null || MeshIndicesBuffer == null || anyMeshMarkedUpdated;

        if (!(needTriangles || needBVH || needVertices))
            return;

        float startTime = Time.realtimeSinceStartup;

        Triangle[] triangles = new Triangle[Mathf.Max(1, totalTris)];
        uint[] triangleIndices = new uint[Mathf.Max(1, totalTris * 3)];
        GPUBVHNode[] blasNodes = new GPUBVHNode[Mathf.Max(1, totalBVHNodes)];

        List<float3> vertices = new(totalVertexCount);
        List<float3> normals = new(totalVertexCount);

        int degenerateTriangles = 0;

        for (int i = 0; i < sharedMeshes.Count; i++)
        {
            SharedMeshData sharedMesh = sharedMeshes[i];
            MeshOffsets off = offsets[sharedMesh];

            sharedMesh.mesh.GetVertices(tV);
            sharedMesh.mesh.GetNormals(tN);

            for (int v = 0; v < tV.Count; v++)
                vertices.Add(tV[v]);

            if (tN.Count == tV.Count)
            {
                for (int n = 0; n < tN.Count; n++)
                    normals.Add(tN[n]);
            }
            else
            {
                for (int n = 0; n < tV.Count; n++)
                    normals.Add(Vector3.up);
            }

            int[] meshTriangles = sharedMesh.mesh.triangles;

            for (int j = 0; j < sharedMesh.GPUBVH.Count; j++)
            {
                GPUBVHNode node = sharedMesh.GPUBVH[j];

                if (node.left != -1) node.left += off.blasNodeOffset;
                if (node.right != -1) node.right += off.blasNodeOffset;

                node.firstIndex += (uint)off.triangleOffset;
                blasNodes[off.blasNodeOffset + j] = node;
            }

            int[] order = sharedMesh.blas.PrimitiveRefs;
            for (int t = 0; t < order.Length; t++)
            {
                int triId = order[t];
                ref buildTri bt = ref sharedMesh.buildTriangles[triId];

                int baseIndex = bt.triangleIndex;
                int v1 = meshTriangles[baseIndex + 0];
                int v2 = meshTriangles[baseIndex + 1];
                int v3 = meshTriangles[baseIndex + 2];

                Vector3 edgeAB = bt.posB - bt.posA;
                Vector3 edgeAC = bt.posC - bt.posA;
                //Vector3 geometricNormal = Vector3.Cross(edgeAB, edgeAC);

                /*if (geometricNormal.sqrMagnitude <= 1e-16f)
                    degenerateTriangles++;*/

                int globalTriIndex = off.triangleOffset + t;
                int triIndexBase = globalTriIndex * 3;

                triangles[globalTriIndex] = new Triangle
                {
                    baseIndex = (uint)triIndexBase,
                    edgeAB = edgeAB,
                    edgeAC = edgeAC,
                    //normal = geometricNormal
                };

                triangleIndices[triIndexBase + 0] = (uint)(off.vertexOffset + v1);
                triangleIndices[triIndexBase + 1] = (uint)(off.vertexOffset + v2);
                triangleIndices[triIndexBase + 2] = (uint)(off.vertexOffset + v3);
            }

            tV.Clear();
            tN.Clear();
        }

        PerfTimer.Time("StructuredVertexBufferCreation", () =>
            ShaderHelper.CreateStructuredBuffer(ref MeshVerticesBuffer, vertices));

        ShaderHelper.CreateStructuredBuffer(ref MeshNormalsBuffer, normals);
        ShaderHelper.CreateStructuredBuffer(ref TriangleBuffer, triangles);
        ShaderHelper.CreateStructuredBuffer(ref BVHBuffer, blasNodes);
        ShaderHelper.CreateStructuredBuffer(ref MeshIndicesBuffer, triangleIndices);

        rayTracingMaterial.SetBuffer("Triangles", TriangleBuffer);
        rayTracingMaterial.SetBuffer("BVHNodes", BVHBuffer);
        rayTracingMaterial.SetBuffer("Vertices", MeshVerticesBuffer);
        rayTracingMaterial.SetBuffer("Normals", MeshNormalsBuffer);
        rayTracingMaterial.SetBuffer("TriangleIndices", MeshIndicesBuffer);

        rayTracingMaterial.SetInt("numBLASNodes", totalBVHNodes);

        for (int i = 0; i < validInstances.Count; i++)
            validInstances[i].update = false;

        float endTime = Time.realtimeSinceStartup;
        Debug.Log($"BLAS/global geometry upload took {(endTime - startTime) * 1000f:F3} ms");
        Debug.Log("Warning: " + degenerateTriangles + " degenerate triangles detected");
    }

    void BuildAndUploadTLAS(
        List<RayTracedMesh> meshObjects,
        Dictionary<SharedMeshData, MeshOffsets> offsets)
    {
        BvhInstance[] instances = new BvhInstance[meshObjects.Count];
        MeshStruct[] gpuInstances = new MeshStruct[meshObjects.Count];

        for (int i = 0; i < meshObjects.Count; i++)
        {
            RayTracedMesh meshObj = meshObjects[i];
            MeshOffsets off = offsets[meshObj.sharedMesh];

            int localRootIndex = meshObj.sharedMesh.blas.RootIndex;
            Bounds localRootBounds = meshObj.sharedMesh.blas.Nodes[localRootIndex].bounds;
            Bounds worldBounds = TransformBoundsToWorld(localRootBounds, meshObj.transform);

            int globalBlasRootIndex = off.rootNodeIndex;

            instances[i] = new BvhInstance
            {
                blasIndex = i,
                blasRootIndex = globalBlasRootIndex,
                localToWorld = meshObj.transform.localToWorldMatrix,
                worldToLocal = meshObj.transform.worldToLocalMatrix,
                localBounds = localRootBounds,
                worldBounds = worldBounds,
                materialIndex = i
            };

            gpuInstances[i] = new MeshStruct
            {
                localToWorldMatrix = meshObj.transform.localToWorldMatrix,
                worldToLocalMatrix = meshObj.transform.worldToLocalMatrix,
                material = meshObj.material,
                firstBVHNodeIndex = (uint)globalBlasRootIndex,

                AABBLeftX = worldBounds.min.x,
                AABBLeftY = worldBounds.min.y,
                AABBLeftZ = worldBounds.min.z,
                AABBRightX = worldBounds.max.x,
                AABBRightY = worldBounds.max.y,
                AABBRightZ = worldBounds.max.z,
            };
        }

        TLASBuilder tlasBuilder = new TLASBuilder();
        BvhBuildSettings tlasSettings = new BvhBuildSettings
        {
            maxLeafSize = tlasMaxLeafSize,
            maxDepth = tlasMaxDepth,
            numBins = tlasNumBins
        };

        tlasBuilder.Build(instances, tlasSettings);

        GPUBVHNode[] tlasNodes = PackNodes(tlasBuilder.Nodes);
        uint[] tlasRefs = new uint[tlasBuilder.PrimitiveRefs.Length];
        for (int i = 0; i < tlasRefs.Length; i++)
            tlasRefs[i] = (uint)tlasBuilder.PrimitiveRefs[i];

        ShaderHelper.CreateStructuredBuffer(ref TLASBuffer, tlasNodes);
        ShaderHelper.CreateStructuredBuffer(ref TLASRefBuffer, tlasRefs);
        ShaderHelper.CreateStructuredBuffer(ref InstanceBuffer, gpuInstances);

        rayTracingMaterial.SetBuffer("TLASNodes", TLASBuffer);
        rayTracingMaterial.SetBuffer("TLASRefs", TLASRefBuffer);
        rayTracingMaterial.SetBuffer("Instances", InstanceBuffer);
        rayTracingMaterial.SetInt("numMeshes", gpuInstances.Length);
        rayTracingMaterial.SetInt("numTLASNodes", tlasNodes.Length);
        rayTracingMaterial.SetInt("numInstances", gpuInstances.Length);
        rayTracingMaterial.SetInt("TLASRootIndex", tlasBuilder.RootIndex);

        rayTracingMaterial.SetInt("BVHTestsSaturation", BVHNodeTestSaturationValue);
        rayTracingMaterial.SetInt("triTestsSaturation", triTestFullSaturationValue);
        rayTracingMaterial.SetInt("TLASNodeVisitsSaturation", TLASNodeVisitsSaturationValue);
        rayTracingMaterial.SetInt("BLASNodeVisitsSaturation", BLASNodeVisitsSaturationValue);
        rayTracingMaterial.SetInt("InstanceBLASTraversalsSaturation", InstanceBLASTraversalsSaturationValue);
        rayTracingMaterial.SetInt("TLASLeafRefsVisitedSaturation", TLASLeafRefsSaturationValue);
    }

    static GPUBVHNode[] PackNodes(BvhNode[] nodes)
    {
        GPUBVHNode[] packed = new GPUBVHNode[nodes.Length];

        for (int i = 0; i < nodes.Length; i++)
        {
            BvhNode n = nodes[i];
            packed[i] = new GPUBVHNode
            {
                left = n.leftChild,
                right = n.rightChild,
                firstIndex = (uint)n.start,
                count = (uint)n.count,

                AABBLeftX = n.bounds.min.x,
                AABBLeftY = n.bounds.min.y,
                AABBLeftZ = n.bounds.min.z,
                AABBRightX = n.bounds.max.x,
                AABBRightY = n.bounds.max.y,
                AABBRightZ = n.bounds.max.z,
            };
        }

        return packed;
    }

    void ApplyBlackHoleLUT(Material rayTracingMaterial, RayTracedBlackHole blackHole)
    {
        rayTracingMaterial.SetFloat("bendStrength", bendStrength);
        rayTracingMaterial.SetFloat("strongFieldCurvatureRadPetMeterCutoff", strongFieldRadPerMeterCuttoff);
    }

    void OnValidate()
    {
        if (rayTracingShader != null)
            ShaderHelper.InitMaterial(rayTracingShader, ref rayTracingMaterial);

        if (accumulatorShader != null)
            ShaderHelper.InitMaterial(accumulatorShader, ref accumulatorMaterial);

        numRenderedFrames = 0;
    }

    void OnRenderImage(RenderTexture source, RenderTexture target)
    {
        if (Camera.current.name != "SceneCamera" || useShaderInSceneView)
        {
            InitFrame();

            // --- Reset accumulation if camera changed ---
            Camera cam = GetComponent<Camera>();
            if (ShouldResetAccumulation(cam))
            {
                Debug.Log("Moving!");
                numRenderedFrames = 0;
                stopRendering = false;
            }
            if (stopRendering)
            {
                Graphics.Blit(resultTexture, target);
                return;
            }

            RenderTexture prevFrame =
                RenderTexture.GetTemporary(source.width, source.height, 0, ShaderHelper.RGBA_SFloat);
            Graphics.Blit(resultTexture, prevFrame);

            if (!accumulateInGameView)
            {
                numRenderedFrames = 0;
            }

            rayTracingMaterial.SetInt("numRenderedFrames", numRenderedFrames);

            RenderTexture currentFrame =
                RenderTexture.GetTemporary(source.width, source.height, 0, ShaderHelper.RGBA_SFloat);

            Graphics.Blit(null, currentFrame, rayTracingMaterial);

            accumulatorMaterial.SetInt("numRenderedFrames", numRenderedFrames);
            accumulatorMaterial.SetTexture("_MainTexOld", prevFrame);
            Graphics.Blit(currentFrame, resultTexture, accumulatorMaterial);
            Graphics.Blit(resultTexture, target);

            if (Camera.current.name == "SceneCamera" && !accumlateInSceneView)
                Graphics.Blit(currentFrame, target);

            if (Camera.current.name != "SceneCamera" && !accumulateInGameView)
                Graphics.Blit(currentFrame, target);

            if (Camera.current.name != "SceneCamera")
            {
                numRenderedFrames++;
                if (numRenderedFrames % 100 == 0)
                    Debug.Log("Num Rendered Frames: " + numRenderedFrames);

                if (numRenderedFrames > numFrames)
                {
                    // stopRendering = true;
                }
            }

            RenderTexture.ReleaseTemporary(prevFrame);
            RenderTexture.ReleaseTemporary(currentFrame);
        }
        else
        {
            Graphics.Blit(source, target);
        }
    }

    void InitFrame()
    {
        Camera.current.cullingMask = 0;

        ShaderHelper.InitMaterial(accumulatorShader, ref accumulatorMaterial);
        ShaderHelper.InitMaterial(rayTracingShader, ref rayTracingMaterial);

        ShaderHelper.CreateRenderTexture(
            ref resultTexture,
            Screen.width,
            Screen.height,
            FilterMode.Bilinear,
            ShaderHelper.RGBA_SFloat,
            "Result"
        );

        UpdateShaderValues();
        AllocateAccelerationBuffers();
        allocateSphereBuffer();
        allocateBlackHoleBuffer();
        UpdateAtmosphereParams();
        UpdateCameraParams(Camera.current);
    }

    void UpdateCameraParams(Camera camera)
    {
        float planeHeight = camera.nearClipPlane * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 2f;
        float planeWidth = planeHeight * camera.aspect;

        rayTracingMaterial.SetVector("ViewParams", new Vector3(planeWidth, planeHeight, camera.nearClipPlane));
        rayTracingMaterial.SetMatrix("CameraLocalToWorldMatrix", camera.transform.localToWorldMatrix);
        rayTracingMaterial.SetFloat("CameraNear", camera.nearClipPlane);
        rayTracingMaterial.SetFloat("CameraFar", renderDistance);
        rayTracingMaterial.SetVector("CameraWorldPos", camera.transform.position);
        rayTracingMaterial.SetInt("RaysPerPixel", raysPerPixel);
        rayTracingMaterial.SetInt("maxBounces", maxBounces);

        if (blackHoleSOIStepSize == 0)
            blackHoleSOIStepSize = 1.0f;

        rayTracingMaterial.SetFloat("stepSize", blackHoleSOIStepSize);
        rayTracingMaterial.SetInt("emergencyBreakMaxSteps", emergencyBreakMaxSteps);
        accumulatorMaterial.SetFloat("accumWeight", accumWeight);

        rayTracingMaterial.SetInt("triTestsSaturation", triTestFullSaturationValue);
        rayTracingMaterial.SetInt("BVHTestsSaturation", BVHNodeTestSaturationValue);
        rayTracingMaterial.SetInt("TLASNodeVisitsSaturation", TLASNodeVisitsSaturationValue);
        rayTracingMaterial.SetInt("BLASNodeVisitsSaturation", BLASNodeVisitsSaturationValue);
        rayTracingMaterial.SetInt("InstanceBLASTraversalsSaturation", InstanceBLASTraversalsSaturationValue);
        rayTracingMaterial.SetInt("TLASLeafRefsVisitedSaturation", TLASLeafRefsSaturationValue);

        if (renderSphere) rayTracingMaterial.EnableKeyword("TEST_SPHERE");
        else rayTracingMaterial.DisableKeyword("TEST_SPHERE");

        if (renderTriangles) rayTracingMaterial.EnableKeyword("TEST_TRIANGLE");
        else rayTracingMaterial.DisableKeyword("TEST_TRIANGLE");

        if (enable_lensing) rayTracingMaterial.EnableKeyword("ENABLE_LENSING");
        else rayTracingMaterial.DisableKeyword("ENABLE_LENSING");

        if (useTlas) rayTracingMaterial.EnableKeyword("USE_TLAS");
        else rayTracingMaterial.DisableKeyword("USE_TLAS");

        if (useRedshifting) rayTracingMaterial.EnableKeyword("USE_REDSHIFTING");
        else rayTracingMaterial.DisableKeyword("USE_REDSHIFTING");

        if (applyScattering) rayTracingMaterial.EnableKeyword("APPLY_SCATTERING");
        else rayTracingMaterial.DisableKeyword("APPLY_SCATTERING");

        if (applyRayleigh) rayTracingMaterial.EnableKeyword("APPLY_RAYLEIGH");
        else rayTracingMaterial.DisableKeyword("APPLY_RAYLEIGH");

        if (applyMie) rayTracingMaterial.EnableKeyword("APPLY_MIE");
        else rayTracingMaterial.DisableKeyword("APPLY_MIE");

        if (applySundisk) rayTracingMaterial.EnableKeyword("APPLY_SUNDISK");
        else rayTracingMaterial.DisableKeyword("APPLY_SUNDISK");

        if (applySunLighting) rayTracingMaterial.EnableKeyword("APPLY_SUN_LIGHTING");
        else rayTracingMaterial.DisableKeyword("APPLY_SUN_LIGHTING");

        if (impactParameterDebug) rayTracingMaterial.EnableKeyword("IMPACT_PARAMETER_DEBUG");
        else rayTracingMaterial.DisableKeyword("IMPACT_PARAMETER_DEBUG");

        if (useOrbitalPlaneCullingIfAble && blackHoleBuffer.count == 1)
            rayTracingMaterial.EnableKeyword("ORBITAL_PLANE_TEST_POSSIBLE");
        else
            rayTracingMaterial.DisableKeyword("ORBITAL_PLANE_TEST_POSSIBLE");

        if (displayTriTests) rayTracingMaterial.EnableKeyword("DEBUG_DISPLAY_TRIANGLE_TESTS");
        else rayTracingMaterial.DisableKeyword("DEBUG_DISPLAY_TRIANGLE_TESTS");

        if (displayBVHNodeTests) rayTracingMaterial.EnableKeyword("DEBUG_DISPLAY_BVH_NODES_VISITED");
        else rayTracingMaterial.DisableKeyword("DEBUG_DISPLAY_BVH_NODES_VISITED");

        if (displayTLASNodeVisits) rayTracingMaterial.EnableKeyword("DEBUG_DISPLAY_TLAS_NODE_VISITS");
        else rayTracingMaterial.DisableKeyword("DEBUG_DISPLAY_TLAS_NODE_VISITS");

        if (displayBLASNodeVisits) rayTracingMaterial.EnableKeyword("DEBUG_DISPLAY_BLAS_NODE_VISITS");
        else rayTracingMaterial.DisableKeyword("DEBUG_DISPLAY_BLAS_NODE_VISITS");

        if (displayInstanceBLASTraversals) rayTracingMaterial.EnableKeyword("DEBUG_DISPLAY_INSTANCE_BLAS_TRAVERSALS");
        else rayTracingMaterial.DisableKeyword("DEBUG_DISPLAY_INSTANCE_BLAS_TRAVERSALS");

        if (displayTLASLeafRefs) rayTracingMaterial.EnableKeyword("DEBUG_DISPLAY_TLAS_LEAF_REFS");
        else rayTracingMaterial.DisableKeyword("DEBUG_DISPLAY_TLAS_LEAF_REFS");
    }

    void UpdateShaderValues()
    {
        rayTracingMaterial.SetInt("MarchStepsCount", marchStepsCount);
    }

    void allocateSphereBuffer()
    {
        RayTracedSphere[] sphereObjects = FindObjectsOfType<RayTracedSphere>();
        Sphere[] spheres = new Sphere[sphereObjects.Length];

        for (int i = 0; i < sphereObjects.Length; i++)
        {
            spheres[i] = new Sphere()
            {
                position = sphereObjects[i].transform.position,
                radius = sphereObjects[i].transform.localScale.x * 0.5f,
                material = sphereObjects[i].material
            };
        }

        ShaderHelper.CreateStructuredBuffer(ref sphereBuffer, spheres);
        rayTracingMaterial.SetBuffer("Spheres", sphereBuffer);
        rayTracingMaterial.SetInt("numSpheres", sphereObjects.Length);
    }

    void allocateBlackHoleBuffer()
    {
        RayTracedBlackHole[] blackHoleObjects = FindObjectsOfType<RayTracedBlackHole>();
        BlackHole[] blackHoles = new BlackHole[blackHoleObjects.Length];

        for (int i = 0; i < blackHoleObjects.Length; i++)
        {
            blackHoles[i] = new BlackHole()
            {
                position = blackHoleObjects[i].transform.position,
                radius = blackHoleObjects[i].transform.localScale.x * 0.5f,
                blackHoleSOIMultiplier = blackHoleObjects[i].blackHoleSOIMultiplier,
            };

            if (blackHoles[i].blackHoleSOIMultiplier <= 0)
            {
                Debug.LogError("BlackHoleSOIMultiplier is less than or equal to 0 for " + blackHoleObjects[i].name);
                blackHoles[i].blackHoleSOIMultiplier = 1.0f;
            }
            
        }

        if (blackHoleObjects.Length > 0)
            ApplyBlackHoleLUT(rayTracingMaterial, blackHoleObjects[0]);

        ShaderHelper.CreateStructuredBuffer(ref blackHoleBuffer, blackHoles);
        rayTracingMaterial.SetBuffer("BlackHoles", blackHoleBuffer);
        rayTracingMaterial.SetInt("numBlackHoles", blackHoleObjects.Length);
    }

    void OnEnable()
    {
        Graphics.Blit(Texture2D.blackTexture, resultTexture);
        numRenderedFrames = 0;
    }
}