using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using UnityEngine;
using Unity.Mathematics;
using Unity.VisualScripting;
using Debug = UnityEngine.Debug;
using UnityEngine.Profiling;
using static RaytracerCPURay;
using Random = System.Random;
using UnityEngine.Rendering;
#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways, ImageEffectAllowedInSceneView]
public class RayTracingManagerWavefront : MonoBehaviour
{
    [SerializeField] bool useShaderInSceneView = true;
    [SerializeField] public bool useTlas = true;
    [SerializeField] public bool useNEE = true;
    public bool useRedshifting = true;

    [Header("TLAS Settings")]
    public int tlasMaxLeafSize = 2;
    public int tlasMaxDepth = 32;
    public int tlasNumBins = 8;

    Material initMaterial;
    private Material classifyMaterial;
    private Material LinearMarchMaterial;
    public int marchStepsCount;
    public float renderDistance;
    public int raysPerPixel;
    public int maxBounces;
    public bool accumlateInSceneView = true;
    public bool accumulateInGameView = true;
    public float blackHoleSOIStepSize = 0.01f;

    [NonSerialized] ComputeBuffer sphereBuffer;
    [NonSerialized] ComputeBuffer MeshVerticesBuffer;
    [NonSerialized] ComputeBuffer MeshNormalsBuffer;
    [NonSerialized] ComputeBuffer MeshIndicesBuffer;
    [NonSerialized] ComputeBuffer TriangleBuffer;
    [NonSerialized] ComputeBuffer BVHBuffer;
    [NonSerialized] ComputeBuffer TLASBuffer;
    [NonSerialized] ComputeBuffer TLASRefBuffer;
    [NonSerialized] ComputeBuffer InstanceBuffer;
    [NonSerialized] private ComputeBuffer LightSourceBuffer;
    [NonSerialized] private ComputeBuffer LightTriangleIndicesBuffer;
    [NonSerialized] private ComputeBuffer LightTrianglesDataBuffer;
    [NonSerialized] private ComputeBuffer controlBuffer;
    [NonSerialized] private ComputeBuffer mainRayBuffer;
    [NonSerialized] private ComputeBuffer HitInfoBuffer;
    [NonSerialized] ComputeBuffer blackHoleBuffer;

    private bool tlasDirty = true;
    private int lastInstanceCount = -1;
    private bool buffersHaveRealData = false;

    [Header("Debug")]
    public bool forceBufferRecreation = false;
    public Shader flagVisualizerShader;
    private Material flagVisualizerMaterial;
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
    private RenderTexture cleanAccumBuffer;

    [Header("Post Processing")]
    //public Shader rayTracingShader;
    public Shader InitShader;
    public Shader ClassifyShader;

    [Header("Ray Tracing")]
    public bool forceSoftwareRaytracing;
    public Shader linearMarchShader;
    public RayTracingShader linearMarchRaytraceShader;
    private RayTracingAccelerationStructure accelStructure;

    [Header("Accumulation")]
    public Shader accumulatorShader;
    public float accumWeight = 1.0f;

    [Header("Color Quantization")]
    public Shader ColorQuantizationShader;
    public bool colorQuantization = false;
    public int numColors = 256;

    [Header("Dithering")]
    public Shader ditherShader;
    public bool ditherPostProcess = false;
    public bool ditherBeforeUpscale = false;
    public int ditherMatrixSize = 4;

    [Header("A-Trous Filter")]
    public bool atrousFilter = false;
    public Shader atrousShader;
    public float atrousColorSigma = 0.6f;
    Material atrousMaterial;

    [Header("Pixel Sizing")]
    [Range(0.05f, 1.0f)]
    public float renderScale = 0.5f;
    public bool atrousBeforeUpscale = false;

    int numRenderedFrames = 0;
    private int baseSeed = 0;
    public int emergencyBreakMaxSteps = 1000;
    public float numFrames = 10000;
    bool stopRendering = false;

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

    public bool useStepsPerCollision = true;
    public int StepsPerCollisionTest = 3;
    public bool useRayMagnification = false;

    Vector3 lastCameraPosition;
    Quaternion lastCameraRotation;
    float lastCameraFov;
    int lastScreenWidth;
    int lastScreenHeight;
    bool historyInitialized = false;
    private float lastRenderScale = -1f;

    void Swap(ref RenderTexture a, ref RenderTexture b) => (a, b) = (b, a);
    [Flags]
    public enum PixelFlags : uint
    {
        None                = 0,
        NeedsLinearMarch    = 1 << 0,
        NeedsGeodesicMarch  = 1 << 1,
        NeedsNEELinear      = 1 << 2,
        NeedsNEEGeodesic    = 1 << 3,
        NeedsScatterLinear  = 1 << 4,
        NeedsScatterGeodesic= 1 << 5,
        NeedsSkybox         = 1 << 6,
        Done                = 1 << 7,
    }
    struct Control //carries flags and numBounces, needs to be sent to everyone
    {
        private uint flags;
        private uint numBounces;
    }

    struct MainRay //Carried between pretty much all structs. Needs to be persistant as these numbers are used as starting points for NEE and Scattering
    {
        public Vector3 rayOrigin;
        public Vector3 rayDirection;
    }

    struct RayColorInfo 
    {
        private float3 rayColor;
        private float3 incomingLight;
    }

    struct Energy
    {
        private float energy;
    }
    
    struct MeshOffsets
    {
        public int vertexOffset;
        public int triangleOffset;
        public int blasNodeOffset;
        public int rootNodeIndex;
    }

    struct HitInfo
    {
        private uint didHit;
        private float distance;
        private Vector3 hitPoint;
        private float u;
        private float v;
        private uint triIndex;
        uint objectType;
        private int objectIndex;
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

    void Awake()
    {
        /*
        ShaderHelper.InitMaterial(rayTracingShader, ref rayTracingMaterial);
        ShaderHelper.InitMaterial(accumulatorShader, ref accumulatorMaterial);
        ShaderHelper.InitMaterial(ditherShader, ref ditherMaterial);
        ShaderHelper.InitMaterial(ColorQuantizationShader, ref colorQuantizationMaterial);
        ShaderHelper.InitMaterial(atrousShader, ref atrousMaterial);*/
    
        EnsureBuffersCreated();
        EnsureMaterialsCreated();
        BindBuffersToMaterial();

        UpdateCameraParams(Camera.current != null ? Camera.current : GetComponent<Camera>());
        ApplyRaytracingMode();
        ShaderVariantCollection variants = Resources.Load<ShaderVariantCollection>("RayTracerVariants");
        if (variants == null)
            Debug.LogError("RayTracerVariants not found in Resources");
        else
        {
            //Debug.Log("Variant count: " + variants.variantCount);
            variants.WarmUp();
            //Debug.Log("Warmed up: " + variants.isWarmedUp);
        }
    }

    void OnEnable()
    {
        tlasDirty = true;
        numRenderedFrames = 0;
        if (resultTexture != null)
            Graphics.Blit(Texture2D.blackTexture, resultTexture);
    }

    void OnValidate()
    {
        tlasDirty = true;
        /*
        rayTracingMaterial = null;
        accumulatorMaterial = null;
        ditherMaterial = null;
        colorQuantizationMaterial = null;
        atrousMaterial = null;*/
        classifyMaterial = null;
        initMaterial = null;
        LinearMarchMaterial = null;
        buffersHaveRealData = false;
        numRenderedFrames = 0;
    }

    void OnDestroy()
    {
        ReleaseBuffers();
    }

    void OnApplicationQuit()
    {
        ReleaseBuffers();
    }

    void ReleaseBuffers()
    {
        /*sphereBuffer?.Release();

        MeshVerticesBuffer?.Release();
        MeshNormalsBuffer?.Release();
        MeshIndicesBuffer?.Release();
        TriangleBuffer?.Release();
        BVHBuffer?.Release();
        TLASBuffer?.Release();
        TLASRefBuffer?.Release();
        InstanceBuffer?.Release();
        LightSourceBuffer?.Release();
        LightTriangleIndicesBuffer?.Release();
        LightTrianglesDataBuffer?.Release();

        sphereBuffer = null;

        MeshVerticesBuffer = null;
        MeshNormalsBuffer = null;
        MeshIndicesBuffer = null;
        TriangleBuffer = null;
        BVHBuffer = null;
        TLASBuffer = null;
        TLASRefBuffer = null;
        InstanceBuffer = null;
        LightSourceBuffer = null;
        LightTriangleIndicesBuffer = null;
        LightTrianglesDataBuffer = null;*/
        controlBuffer?.Release();
        mainRayBuffer?.Release();
        blackHoleBuffer?.Release();
        HitInfoBuffer?.Release();
        blackHoleBuffer = null;
        buffersHaveRealData = false;
        if (accelStructure != null)
        {
            accelStructure.Release();
            accelStructure = null;
        }
    }

    void EnsureBuffersCreated(bool forceRecreate = false)
    {
        int pixelCount = Screen.width * Screen.height;
        if (forceRecreate)
            ReleaseBuffers();

        if (TriangleBuffer == null)
            ShaderHelper.CreateStructuredBuffer<Triangle>(ref TriangleBuffer, 1);
        if (BVHBuffer == null)
            ShaderHelper.CreateStructuredBuffer<GPUBVHNode>(ref BVHBuffer, 1);
        if (TLASBuffer == null)
            ShaderHelper.CreateStructuredBuffer<GPUBVHNode>(ref TLASBuffer, 1);
        if (TLASRefBuffer == null)
            ShaderHelper.CreateStructuredBuffer<uint>(ref TLASRefBuffer, 1);
        if (InstanceBuffer == null)
            ShaderHelper.CreateStructuredBuffer<MeshStruct>(ref InstanceBuffer, 1);
        if (MeshVerticesBuffer == null)
            ShaderHelper.CreateStructuredBuffer<Vector3>(ref MeshVerticesBuffer, 1);
        if (MeshNormalsBuffer == null)
            ShaderHelper.CreateStructuredBuffer<Vector3>(ref MeshNormalsBuffer, 1);
        if (MeshIndicesBuffer == null)
            ShaderHelper.CreateStructuredBuffer<uint>(ref MeshIndicesBuffer, 1);
        if (LightSourceBuffer == null)
            ShaderHelper.CreateStructuredBuffer<GPULightSource>(ref LightSourceBuffer, 1);
        if (LightTriangleIndicesBuffer == null)
            ShaderHelper.CreateStructuredBuffer<int>(ref LightTriangleIndicesBuffer, 1);
        if (LightTrianglesDataBuffer == null)
            ShaderHelper.CreateStructuredBuffer<GPULightTriangleData>(ref LightTrianglesDataBuffer, 1);
        if (sphereBuffer == null)
            ShaderHelper.CreateStructuredBuffer<Sphere>(ref sphereBuffer, 1);
        if (controlBuffer == null)
            ShaderHelper.CreateStructuredBuffer<Control>(ref controlBuffer, pixelCount);
        if (mainRayBuffer == null)
            ShaderHelper.CreateStructuredBuffer<MainRay>(ref mainRayBuffer, pixelCount);
        if (blackHoleBuffer == null)
            ShaderHelper.CreateStructuredBuffer<BlackHole>(ref blackHoleBuffer, 1);
        if (HitInfoBuffer == null)
            ShaderHelper.CreateStructuredBuffer<HitInfo>(ref HitInfoBuffer, pixelCount);
    }

    void EnsureMaterialsCreated()
    {
        if (initMaterial == null)
            ShaderHelper.InitMaterial(InitShader, ref initMaterial);
        if (classifyMaterial == null) 
            ShaderHelper.InitMaterial(ClassifyShader, ref classifyMaterial);
        if (flagVisualizerMaterial == null && flagVisualizerShader != null)
            flagVisualizerMaterial = new Material(flagVisualizerShader);
        if (LinearMarchMaterial == null)
        {
            ShaderHelper.InitMaterial(linearMarchShader, ref LinearMarchMaterial);
            ApplyRaytracingMode();
        }
    }

    void BindBuffersToMaterial()
    {
        if (initMaterial == null || classifyMaterial == null || LinearMarchMaterial == null || flagVisualizerMaterial == null) return;
    
        BindInitBuffers();
        BindClassifyBuffers();
        BindLinearMarchBuffers();
        BindFlagVisualizerBuffers();
    }

    void BindInitBuffers()
    {
        initMaterial.SetBuffer("controls",  controlBuffer);
        initMaterial.SetBuffer("main_rays", mainRayBuffer);
    }

    void BindClassifyBuffers()
    {
        classifyMaterial.SetBuffer("controls",   controlBuffer);
        classifyMaterial.SetBuffer("main_rays",  mainRayBuffer);
        classifyMaterial.SetBuffer("blackholes", blackHoleBuffer);
    }

    void BindLinearMarchBuffers()
    {
        bool usingSoftware = !SystemInfo.supportsRayTracing || forceSoftwareRaytracing;
        if (usingSoftware)
        {
            LinearMarchMaterial.SetBuffer("Triangles",       TriangleBuffer);
            LinearMarchMaterial.SetBuffer("BVHNodes",        BVHBuffer);
            LinearMarchMaterial.SetBuffer("TLASNodes",       TLASBuffer);
            LinearMarchMaterial.SetBuffer("TLASRefs",        TLASRefBuffer);
            LinearMarchMaterial.SetBuffer("Instances",       InstanceBuffer);
            LinearMarchMaterial.SetBuffer("Vertices",        MeshVerticesBuffer);
            LinearMarchMaterial.SetBuffer("Normals",         MeshNormalsBuffer);
            LinearMarchMaterial.SetBuffer("TriangleIndices", MeshIndicesBuffer);
        }
        LinearMarchMaterial.SetBuffer("controls",        controlBuffer);
        LinearMarchMaterial.SetBuffer("main_rays",       mainRayBuffer);
        LinearMarchMaterial.SetBuffer("blackholes",      blackHoleBuffer);
        LinearMarchMaterial.SetBuffer("hit_info_buffer", HitInfoBuffer);
    }

    void BindFlagVisualizerBuffers()
    {
        if (flagVisualizerMaterial == null) return;
        flagVisualizerMaterial.SetBuffer("controls",        controlBuffer);
        flagVisualizerMaterial.SetBuffer("hit_info_buffer", HitInfoBuffer);
        flagVisualizerMaterial.SetBuffer("Instances", InstanceBuffer);
    }
    private int[] accelStructureInstanceIDs;
    private RayTracedMesh[] lastBuiltMeshOrder; // ADD THIS FIELD
    void BuildAccelStructure()
    {

        if (accelStructure != null)
            accelStructure.Release();

        RayTracingAccelerationStructure.Settings settings = new RayTracingAccelerationStructure.Settings
        {
            rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything,
            managementMode    = RayTracingAccelerationStructure.ManagementMode.Manual,
            layerMask         = 255
        };

        accelStructure = new RayTracingAccelerationStructure(settings);

        RayTracedMesh[] meshes = FindObjectsOfType<RayTracedMesh>();
        lastBuiltMeshOrder = meshes; // STORE THE ORDER
        accelStructureInstanceIDs = new int[meshes.Length];
        MeshStruct[] gpuInstances = new MeshStruct[meshes.Length];
        for (int i = 0; i < meshes.Length; i++)
        {
            RayTracedMesh m = meshes[i];
            MeshRenderer renderer = m.GetComponent<MeshRenderer>();
            MeshFilter filter = m.GetComponent<MeshFilter>();
            if (renderer == null || filter == null) continue;

            RayTracingMeshInstanceConfig config = new RayTracingMeshInstanceConfig
            {
                mesh         = filter.sharedMesh,
                subMeshIndex = 0,
                material     = renderer.sharedMaterial,
            };

            accelStructureInstanceIDs[i] = accelStructure.AddInstance(config, m.transform.localToWorldMatrix, null, (uint)i);
            gpuInstances[i] = new MeshStruct
            {
                localToWorldMatrix = meshes[i].transform.localToWorldMatrix,
                worldToLocalMatrix = meshes[i].transform.worldToLocalMatrix,
                material           = meshes[i].material,
            };
        }
        
        ShaderHelper.UploadStructuredBuffer(ref InstanceBuffer, gpuInstances);
        flagVisualizerMaterial.SetBuffer("Instances", InstanceBuffer);

        accelStructure.Build();
    }

    void DispatchHardwareLinearMarch(int width, int height)
    {
        if (linearMarchRaytraceShader == null)
        {
            Debug.LogError("linearMarchRaytraceShader is not assigned!");
            return;
        }
        if (accelStructure == null)
        {
            Debug.LogError("accelStructure is null, was BuildAccelStructure called?");
            return;
        }

        linearMarchRaytraceShader.SetAccelerationStructure("_AccelStructure", accelStructure);
        linearMarchRaytraceShader.SetBuffer("controls",       controlBuffer);
        linearMarchRaytraceShader.SetBuffer("main_rays",      mainRayBuffer);
        flagVisualizerMaterial.SetBuffer("hit_info_buffer",      HitInfoBuffer);
        flagVisualizerMaterial.SetBuffer("Instances", InstanceBuffer);
        linearMarchRaytraceShader.SetBuffer("hit_info_buffer",      HitInfoBuffer);
        linearMarchRaytraceShader.SetFloat ("renderDistance", renderDistance);
        linearMarchRaytraceShader.Dispatch ("RayGen", width, height, 1);
    }
    void UpdateAtmosphereParams()
    {
        /*
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
            rayTracingMaterial.SetInt("framesPerScatter", 1);

        DirectionalGeodesic2DLutSolver.StepSize = blackHoleSOIStepSize;*/
    }

    List<RayTracedMesh> GetValidMeshInstances()
    {
        RayTracedMesh[] allMeshes = FindObjectsOfType<RayTracedMesh>();
        List<RayTracedMesh> validMeshes = new(allMeshes.Length);

        for (int i = 0; i < allMeshes.Length; i++)
        {
            RayTracedMesh m = allMeshes[i];
            if (m == null) continue;
            if (m.sharedMesh == null)
                m.RebuildStaticData();
            if (m.sharedMesh == null) continue;
            if (m.sharedMesh.mesh == null) continue;
            if (m.sharedMesh.buildTriangles == null || m.sharedMesh.buildTriangles.Length == 0) continue;
            if (m.sharedMesh.blas == null) continue;
            if (m.sharedMesh.blas.Nodes == null || m.sharedMesh.blas.Nodes.Length == 0) continue;
            if (m.sharedMesh.blas.PrimitiveRefs == null || m.sharedMesh.blas.PrimitiveRefs.Length == 0) continue;
            if (m.sharedMesh.GPUBVH == null || m.sharedMesh.GPUBVH.Count == 0) continue;
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
            if (sm == null) continue;
            if (seen.Add(sm))
                unique.Add(sm);
        }

        return unique;
    }

    bool AnyInstanceNeedsUpdate(List<RayTracedMesh> validInstances)
    {
        for (int i = 0; i < validInstances.Count; i++)
            if (validInstances[i].update) return true;
        return false;
    }

    bool AnyTransformDirty(List<RayTracedMesh> validInstances)
    {
        for (int i = 0; i < validInstances.Count; i++)
            if (validInstances[i].transformDirty) return true;
        return false;
    }
    void ApplyRaytracingMode()
    {   
    }
    void AllocateAccelerationBuffers()
    {
        bool useHardwareRT = SystemInfo.supportsRayTracing && !forceSoftwareRaytracing;
        List<RayTracedMesh> validInstances = GetValidMeshInstances();
        if (useHardwareRT)
        {
            bool anyTransformDirty = AnyTransformDirty(validInstances);
            bool anyMeshUpdated = AnyInstanceNeedsUpdate(validInstances);
            bool instanceCountChanged = validInstances.Count != lastInstanceCount;

            if (tlasDirty || anyMeshUpdated || instanceCountChanged)
            {
                // full rebuild — geometry or instances changed
                BuildAccelStructure();
                tlasDirty = false;
                lastInstanceCount = validInstances.Count;
                for (int i = 0; i < validInstances.Count; i++)
                    validInstances[i].update = false;
            }
            else if (anyTransformDirty)
            {
                MeshStruct[] gpuInstances = new MeshStruct[lastBuiltMeshOrder.Length];
                for (int i = 0; i < lastBuiltMeshOrder.Length && i < accelStructureInstanceIDs.Length; i++)
                {
                    accelStructure.UpdateInstanceTransform(accelStructureInstanceIDs[i], lastBuiltMeshOrder[i].transform.localToWorldMatrix);
                }
                accelStructure.Build();
            }

            for (int i = 0; i < validInstances.Count; i++)
                validInstances[i].transformDirty = false;

            return;
        }

        // software path — unchanged below
        if (forceBufferRecreation)
        {
            buffersHaveRealData = false;
            tlasDirty = true;
            forceBufferRecreation = false;
        }


        if (validInstances.Count == 0) { EnsureBuffersCreated(); return; }

        if (validInstances.Count != lastInstanceCount)
        {
            tlasDirty = true;
            lastInstanceCount = validInstances.Count;
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

        bool needTriangles = !buffersHaveRealData || TriangleBuffer == null || TriangleBuffer.count != Mathf.Max(1, totalTris) || anyMeshMarkedUpdated;
        bool needBVH = !buffersHaveRealData || BVHBuffer == null || BVHBuffer.count != Mathf.Max(1, totalBVHNodes) || anyMeshMarkedUpdated;
        bool needVertices = !buffersHaveRealData || MeshVerticesBuffer == null || MeshNormalsBuffer == null || MeshIndicesBuffer == null || anyMeshMarkedUpdated;

        if (!(needTriangles || needBVH || needVertices))
            return;

        tlasDirty = true;

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

                int globalTriIndex = off.triangleOffset + t;
                int triIndexBase = globalTriIndex * 3;

                triangles[globalTriIndex] = new Triangle
                {
                    baseIndex = (uint)triIndexBase,
                    edgeAB = edgeAB,
                    edgeAC = edgeAC,
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

        LinearMarchMaterial.SetBuffer("Triangles",       TriangleBuffer);
        LinearMarchMaterial.SetBuffer("BVHNodes",        BVHBuffer);
        LinearMarchMaterial.SetBuffer("Vertices",        MeshVerticesBuffer);
        LinearMarchMaterial.SetBuffer("Normals",         MeshNormalsBuffer);
        LinearMarchMaterial.SetBuffer("TriangleIndices", MeshIndicesBuffer);
        LinearMarchMaterial.SetInt("numBLASNodes", totalBVHNodes);

        for (int i = 0; i < validInstances.Count; i++)
            validInstances[i].update = false;

        float endTime = Time.realtimeSinceStartup;
        Debug.Log($"BLAS/global geometry upload took {(endTime - startTime) * 1000f:F3} ms, allocated triangles" + TriangleBuffer.count);
        Debug.Log("Warning: " + degenerateTriangles + " degenerate triangles detected");
    }

    void BuildAndUploadTLAS(
        List<RayTracedMesh> meshObjects,
        Dictionary<SharedMeshData, MeshOffsets> offsets)
    {
        //Debug.Log($"BuildAndUploadTLAS LinearMarchMaterial instanceID: {LinearMarchMaterial.GetInstanceID()}\n{System.Environment.StackTrace}");
        if (!tlasDirty && TLASBuffer != null && buffersHaveRealData)
            return;
        tlasDirty = false;

        for (int i = 0; i < meshObjects.Count; i++)
            meshObjects[i].transformDirty = false;
        BvhInstance[] instances = new BvhInstance[meshObjects.Count];
        MeshStruct[] gpuInstances = new MeshStruct[meshObjects.Count];

        GPULightSource[] lightSources = new GPULightSource[meshObjects.Count];
        List<int> lightTriangleIndices = new List<int>();
        List<GPULightTriangleData> lightTrianglesData = new List<GPULightTriangleData>();
        int numLightSources = 0;

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

            if (meshObj.material.emissiveStrength > 0)
            {
                Matrix4x4 l2w = meshObj.transform.localToWorldMatrix;
                SharedMeshData sm = meshObj.sharedMesh;

                int triStart = lightTriangleIndices.Count;
                float totalArea = 0f;

                int[] order = sm.blas.PrimitiveRefs;
                for (int t = 0; t < order.Length; t++)
                {
                    int triId = order[t];
                    ref buildTri bt = ref sm.buildTriangles[triId];

                    Vector3 edgeAB = bt.posB - bt.posA;
                    Vector3 edgeAC = bt.posC - bt.posA;

                    Vector3 worldAB = l2w.MultiplyVector(edgeAB);
                    Vector3 worldAC = l2w.MultiplyVector(edgeAC);
                    Vector3 worldCross = Vector3.Cross(worldAB, worldAC);

                    float area = worldCross.magnitude * 0.5f;
                    totalArea += area;

                    int globalTriIndex = off.triangleOffset + t;
                    lightTriangleIndices.Add(globalTriIndex);

                    while (lightTrianglesData.Count <= globalTriIndex)
                        lightTrianglesData.Add(new GPULightTriangleData());

                    lightTrianglesData[globalTriIndex] = new GPULightTriangleData
                    {
                        worldSpaceArea = area,
                        worldNormal = worldCross.magnitude > 1e-10f
                            ? worldCross.normalized
                            : Vector3.up
                    };
                }

                lightSources[numLightSources++] = new GPULightSource
                {
                    instanceIndex = i,
                    totalArea = totalArea,
                    triStart = triStart,
                    triCount = order.Length
                };
            }
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

        GPULightSource[] trimmedLightSources = new GPULightSource[Mathf.Max(1, numLightSources)];
        Array.Copy(lightSources, trimmedLightSources, numLightSources);

        if (lightTrianglesData.Count == 0)
            lightTrianglesData.Add(new GPULightTriangleData());

        int[] lightTriIndicesArray = lightTriangleIndices.Count > 0
            ? lightTriangleIndices.ToArray()
            : new int[1];

        ShaderHelper.UploadStructuredBuffer(ref TLASBuffer,                 tlasNodes);
        ShaderHelper.UploadStructuredBuffer(ref TLASRefBuffer,              tlasRefs);
        ShaderHelper.UploadStructuredBuffer(ref InstanceBuffer,             gpuInstances);
        ShaderHelper.UploadStructuredBuffer(ref LightSourceBuffer,          trimmedLightSources);
        ShaderHelper.UploadStructuredBuffer(ref LightTriangleIndicesBuffer, lightTriIndicesArray);
        ShaderHelper.UploadStructuredBuffer(ref LightTrianglesDataBuffer,   lightTrianglesData.ToArray());

        LinearMarchMaterial.SetBuffer("TLASNodes",            TLASBuffer);
        LinearMarchMaterial.SetBuffer("TLASRefs",             TLASRefBuffer);
        LinearMarchMaterial.SetBuffer("Instances",            InstanceBuffer);
        flagVisualizerMaterial.SetBuffer("Instances",            InstanceBuffer);
        LinearMarchMaterial.SetBuffer("LightSources",         LightSourceBuffer);
        LinearMarchMaterial.SetBuffer("LightTriangleIndices", LightTriangleIndicesBuffer);
        LinearMarchMaterial.SetBuffer("LightTrianglesData",   LightTrianglesDataBuffer);

        LinearMarchMaterial.SetInt("numMeshes",       gpuInstances.Length);
        LinearMarchMaterial.SetInt("numTLASNodes",    tlasNodes.Length);
        LinearMarchMaterial.SetInt("numInstances",    gpuInstances.Length);
        LinearMarchMaterial.SetInt("TLASRootIndex",   tlasBuilder.RootIndex);
        LinearMarchMaterial.SetInt("numLightSources", numLightSources);

        LinearMarchMaterial.SetInt("BVHTestsSaturation",               BVHNodeTestSaturationValue);
        LinearMarchMaterial.SetInt("triTestsSaturation",               triTestFullSaturationValue);
        LinearMarchMaterial.SetInt("TLASNodeVisitsSaturation",         TLASNodeVisitsSaturationValue);
        LinearMarchMaterial.SetInt("BLASNodeVisitsSaturation",         BLASNodeVisitsSaturationValue);
        LinearMarchMaterial.SetInt("InstanceBLASTraversalsSaturation", InstanceBLASTraversalsSaturationValue);
        LinearMarchMaterial.SetInt("TLASLeafRefsVisitedSaturation",    TLASLeafRefsSaturationValue);
        LinearMarchMaterial.SetInt("u_StepsPerCollisionTest",          StepsPerCollisionTest);
        //Debug.Log($"Instance 0 position in gpuInstances: {meshObjects[0].transform.position}");
        buffersHaveRealData = true;
        
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

    void OnRenderImage(RenderTexture source, RenderTexture target)
    {
        if (Camera.current.name == "SceneCamera")
        {
            Graphics.Blit(source, target);
            return;
        }
        Graphics.ClearRandomWriteTargets();
        InitFrame();


        bool useHardwareRT = SystemInfo.supportsRayTracing && !forceSoftwareRaytracing;

        int scaledW = Mathf.Max(1, Mathf.RoundToInt(source.width  * renderScale));
        int scaledH = Mathf.Max(1, Mathf.RoundToInt(source.height * renderScale));

        RenderTexture scaledFrame  = RenderTexture.GetTemporary(scaledW, scaledH, 0, ShaderHelper.RGBA_SFloat);
        RenderTexture scaledTemp   = RenderTexture.GetTemporary(scaledW, scaledH, 0, ShaderHelper.RGBA_SFloat);
        RenderTexture currentFrame = RenderTexture.GetTemporary(source.width, source.height, 0, ShaderHelper.RGBA_SFloat);
        RenderTexture tempBuffer   = RenderTexture.GetTemporary(source.width, source.height, 0, ShaderHelper.RGBA_SFloat);
        LinearMarchMaterial.SetFloat("renderDistance", renderDistance);
        // init and classify always run as blits — they don't do traversal
        Graphics.SetRandomWriteTarget(1, controlBuffer, false);
        Graphics.SetRandomWriteTarget(2, mainRayBuffer, false);
        Graphics.SetRandomWriteTarget(3, HitInfoBuffer, false);
        Graphics.Blit(null, scaledFrame, initMaterial);
        Graphics.Blit(null, scaledFrame, classifyMaterial);
        // linear march — hardware RT dispatch or software BVH blit
        if (useHardwareRT)
        {
            //Debug.Log("Running hardware RT");   
            DispatchHardwareLinearMarch(scaledW, scaledH);
        }
        else
        {
            //Debug.Log($"Blit LinearMarchMaterial instanceID: {LinearMarchMaterial.GetInstanceID()}");
            Graphics.Blit(null, scaledFrame, LinearMarchMaterial);
        }
        Graphics.ClearRandomWriteTargets();
        Graphics.Blit(null, scaledFrame, flagVisualizerMaterial);
        Graphics.Blit(scaledFrame, target);

        RenderTexture.ReleaseTemporary(scaledFrame);
        RenderTexture.ReleaseTemporary(scaledTemp);
        RenderTexture.ReleaseTemporary(currentFrame);
        RenderTexture.ReleaseTemporary(tempBuffer);
    }

    void InitFrame()
    {
        RayTracedMesh[] meshObjects = FindObjectsOfType<RayTracedMesh>();
        bool anyTransformDirty = AnyTransformDirty(meshObjects.ToList());

        if (anyTransformDirty)
            tlasDirty = true;
        EnsureMaterialsCreated();
        EnsureBuffersCreated();
        ShaderHelper.CreateRenderTexture(
            ref resultTexture,
            Screen.width,
            Screen.height,
            FilterMode.Bilinear,
            ShaderHelper.RGBA_SFloat,
            "Result"
        );

        AllocateAccelerationBuffers();
        BindBuffersToMaterial();
        allocateBlackHoleBuffer();
        UpdateAtmosphereParams();
        UpdateCameraParams(Camera.current);
        UpdateRayTracingParams();

    }

    void UpdateCameraParams(Camera camera)
    {
        float planeHeight = camera.nearClipPlane * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 2f;
        float planeWidth = planeHeight * camera.aspect;
        initMaterial.SetVector("ViewParams", new Vector3(planeWidth, planeHeight, camera.nearClipPlane));
        initMaterial.SetMatrix("CameraLocalToWorldMatrix", camera.transform.localToWorldMatrix);
        initMaterial.SetVector("CameraWorldPos", camera.transform.position);
        initMaterial.SetInt("numRenderedFrames", numRenderedFrames);
        if (blackHoleSOIStepSize == 0)
            blackHoleSOIStepSize = 1.0f;
    }

    void UpdateRayTracingParams()
    {
        SetKeyword(LinearMarchMaterial, "USE_TLAS", useTlas);
    }

    void SetKeyword(Material mat, string keyword, bool enabled)
    {
        if (enabled) mat.EnableKeyword(keyword);
        else mat.DisableKeyword(keyword);
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
            ApplyBlackHoleLUT(classifyMaterial, blackHoleObjects[0]);

        ShaderHelper.UploadStructuredBuffer(ref blackHoleBuffer, blackHoles);
        classifyMaterial.SetBuffer("blackholes", blackHoleBuffer);
        classifyMaterial.SetInt("num_black_holes", blackHoleObjects.Length);
        LinearMarchMaterial.SetBuffer("blackholes",   blackHoleBuffer);
        LinearMarchMaterial.SetInt("num_black_holes", blackHoleObjects.Length);

        if (SystemInfo.supportsRayTracing && !forceSoftwareRaytracing && linearMarchRaytraceShader != null)
        {
            linearMarchRaytraceShader.SetBuffer("blackholes",      blackHoleBuffer);
            linearMarchRaytraceShader.SetInt   ("num_black_holes", blackHoleObjects.Length);
        }
    }
}