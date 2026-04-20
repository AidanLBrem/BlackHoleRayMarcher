using System;
using System.Collections.Generic;
using System.Linq;
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
public partial class RayTracingManagerWavefront : MonoBehaviour
{
    private const int NUM_QUEUES = 100;
    private const int ACTIVE_RAY_QUEUE = 0;
    private const int LINEAR_RAY_QUEUEA = 1;
    private const int GEODISC_RAY_QUEUEA = 2;
    private const int LINEAR_RAY_QUEUEB = 3;
    private const int GEODISC_RAY_QUEUEB = 4;
    private const int REFLECTION_QUEUE = 5;
    private const int NEE_QUEUE = 6;
    private const int SCATTER_QUEUE = 7;
    private const int SKYBOX_QUEUE = 8;

    // Kernel indices into bucketSortCompute
    private const int KERNEL_CLEAR_BUCKETS = 0;
    private const int KERNEL_COUNT_BUCKETS = 1;
    private const int KERNEL_PREFIX_SUM    = 2;
    private const int KERNEL_SCATTER_RAYS  = 3;
    private const int KERNEL_FIXUP_QUEUE   = 4;

    const uint FLAG_NEEDS_LINEAR_MARCH = (1u << 0);
    const uint FLAG_NEEDS_REFLECTION   = (1u << 2);
    const uint FLAG_NEEDS_CLASSIFY     = (1u << 5);

    [SerializeField] bool useShaderInSceneView = true;
    [SerializeField] public bool useTlas = true;
    [SerializeField] public bool useNEE = true;
    public bool useRedshifting = true;

    [Header("TLAS Settings")]
    public int tlasMaxLeafSize = 2;
    public int tlasMaxDepth = 32;
    public int tlasNumBins = 8;

    [Header("Compute Kernels")]
    public ComputeShader initCompute;
    public ComputeShader classifyCompute;
    public ComputeShader reflectionCompute;
    public ComputeShader accumulateCompute;
    public ComputeShader compactCompute;
    public ComputeShader writeIndirectArgsCompute;
    public ComputeShader resetCountCompute;

    [Header("Ray Sorting")]
    public ComputeShader bucketSortCompute;
    [Range(1, 8)]
    public int sortBucketsPerAxis = 4;

    [Header("Wavefront Ping Pong")]
    [Range(1, 8)]
    public int maxLinearGeodiscPingPongIterations = 4;

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
    [NonSerialized] private ComputeBuffer controlQueue;
    [NonSerialized] private ComputeBuffer mainRayBuffer;
    [NonSerialized] private ComputeBuffer HitInfoBuffer;
    [NonSerialized] private ComputeBuffer rayColorInfoBuffer;
    [NonSerialized] ComputeBuffer blackHoleBuffer;
    [NonSerialized] private ComputeBuffer pixelAccumBuffer;
    [NonSerialized] private ComputeBuffer activeRayIndicesBuffer;
    [NonSerialized] private ComputeBuffer activeRayCountBuffer;
    [NonSerialized] private ComputeBuffer indirectArgsBuffer;

    [NonSerialized] private ComputeBuffer linearMarchQueueBufferA;
    [NonSerialized] private ComputeBuffer linearMarchQueueBufferB;
    [NonSerialized] private ComputeBuffer geodiscMarchQueueBufferA;
    [NonSerialized] private ComputeBuffer geodiscMarchQueueBufferB;
    [NonSerialized] private ComputeBuffer reflectionQueueBuffer;
    [NonSerialized] private ComputeBuffer skyboxQueueBuffer;

    // Bucket sort buffers
    [NonSerialized] private ComputeBuffer bucketCountsBuffer;
    [NonSerialized] private ComputeBuffer bucketOffsetsBuffer;
    [NonSerialized] private ComputeBuffer sortedRaysBuffer;          // ping-pong twin for mainRayBuffer
    [NonSerialized] private ComputeBuffer sortedRayIndicesBuffer;    // ping-pong twin for activeRayIndicesBuffer
// Mapping buffers — survive the sort so queues can be fixed up
    [NonSerialized] private ComputeBuffer pixelForSlotBuffer;        // slot -> original pixel
    [NonSerialized] private ComputeBuffer sortedPixelForSlotBuffer;  // scratch during sort
    [NonSerialized] private ComputeBuffer newSlotForOldSlotBuffer;   // old slot -> new slot
    [NonSerialized] private ComputeBuffer sortedControlsBuffer;
    [NonSerialized] private ComputeBuffer sortedRayColorInfoBuffer;
    private bool tlasDirty = true;
    private int lastInstanceCount = -1;
    private bool buffersHaveRealData = false;
    private bool lastForceSoftware = false;
    private int lastSortBucketsPerAxis = -1;

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

    [Header("Ray Tracing")]
    public bool forceSoftwareRaytracing;
    public RayTracingShader linearMarchRaytraceShader;
    private RayTracingAccelerationStructure accelStructure;

    [Header("Temporal Accumulation")]
    public Shader accumulatorShader;
    private Material accumulatorMaterial;
    public float accumWeight = 1.0f;
    private RenderTexture cleanAccumBuffer;
    private float lastRenderScale = -1f;

    [Header("Color Quantization")]
    public Shader ColorQuantizationShader;
    public bool colorQuantization = false;
    public int numColors = 256;
    private Material colorQuantizationMaterial;

    [Header("Dithering")]
    public Shader ditherShader;
    public bool ditherPostProcess = false;
    public bool ditherBeforeUpscale = false;
    public int ditherMatrixSize = 4;
    private Material ditherMaterial;

    [Header("A-Trous Filter")]
    public bool atrousFilter = false;
    public Shader atrousShader;
    public float atrousColorSigma = 0.6f;
    private Material atrousMaterial;
    public bool atrousBeforeUpscale = false;

    [Header("Pixel Sizing")]
    [Range(0.05f, 1.0f)]
    public float renderScale = 0.5f;

    int numRenderedFrames = 0;
    private int baseSeed = 0;
    public int emergencyBreakMaxSteps = 1000;
    public float numFrames = 10000;

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

    int scaledW = 1;
    int scaledH = 1;

    private static readonly uint[] zeroOne         = { 0 };
    private static readonly uint[] zerosNUM_QUEUES = new uint[NUM_QUEUES];
    private static readonly int[] atrousStepSizes  = { 1, 2, 4, 8, 16 };

    private readonly List<RayTracedMesh>                     validInstancesCache = new();
    private readonly List<SharedMeshData>                    uniqueMeshesCache   = new();
    private readonly HashSet<SharedMeshData>                 uniqueMeshesSet     = new();
    private readonly Dictionary<SharedMeshData, MeshOffsets> offsetsCache        = new();

    private RayTracedBlackHole[] cachedBlackHoleObjects = Array.Empty<RayTracedBlackHole>();
    private BlackHole[]          cachedBlackHoles       = Array.Empty<BlackHole>();
    private bool                 blackHolesDirty        = true;

    private BvhInstance[]                       tlasInstances       = Array.Empty<BvhInstance>();
    private MeshStruct[]                        tlasGpuInstances    = Array.Empty<MeshStruct>();
    private GPULightSource[]                    tlasLightSources    = Array.Empty<GPULightSource>();
    private uint[]                              tlasRefsCache       = Array.Empty<uint>();
    private GPUBVHNode[]                        tlasNodesCache      = Array.Empty<GPUBVHNode>();
    private readonly List<int>                  lightTriIndicesCache = new();
    private readonly List<GPULightTriangleData> lightTriDataCache    = new();

    private Triangle[]            blasTriangles       = Array.Empty<Triangle>();
    private uint[]                blasTriangleIndices = Array.Empty<uint>();
    private GPUBVHNode[]          blasBVHNodes        = Array.Empty<GPUBVHNode>();
    private readonly List<float3> blasVertices        = new();
    private readonly List<float3> blasNormals         = new();

    private readonly List<RayTracedMesh> lastBuiltMeshOrderList = new();

    private bool startupDone = false;
    private int[] accelStructureInstanceIDs;

    void Swap(ref RenderTexture a, ref RenderTexture b) => (a, b) = (b, a);

    [Flags]
    public enum PixelFlags : uint
    {
        None                 = 0,
        NeedsLinearMarch     = 1 << 0,
        NeedsGeodesicMarch   = 1 << 1,
        NeedsNEELinear       = 1 << 2,
        NeedsNEEGeodesic     = 1 << 3,
        NeedsScatterLinear   = 1 << 4,
        NeedsScatterGeodesic = 1 << 5,
        NeedsSkybox          = 1 << 6,
        Done                 = 1 << 7,
    }
    struct PixelAccum
    {
        public uint r;
        public uint g;
        public uint b;
    }
    struct Control      {  private uint rngState;
        private uint pixelIndex;
    }
    struct MainRay      { public Vector3 rayOrigin; public Vector3 rayDirection; }
    struct RayColorInfo { private float3 rayColor; private float3 incomingLight; }
    struct Energy       { private float energy; }

    struct MeshOffsets
    {
        public int vertexOffset;
        public int triangleOffset;
        public int blasNodeOffset;
        public int rootNodeIndex;
    }

    struct HitInfo
    {
        //private uint    didHit;
        private float   distance;
        private float   u;
        private float   v;
        private uint    triIndex;
        private int     objectIndex;
        //private Vector3 worldNormal;
    }
    
    void OnEnable()
    {
        tlasDirty = true;
        buffersHaveRealData = false;
        lastInstanceCount = -1;
        numRenderedFrames = 0;
        historyInitialized = false;
        blackHolesDirty = true;
        startupDone = false;
        if (!SystemInfo.supportsInlineRayTracing)
        {
            Debug.LogError("Inline ray tracing not supported on this systems.");
            Debug.Log($"graphicsDeviceType: {SystemInfo.graphicsDeviceType}");
            Debug.Log($"graphicsDeviceName: {SystemInfo.graphicsDeviceName}");
            Debug.Log($"graphicsDeviceVersion: {SystemInfo.graphicsDeviceVersion}");
            Debug.Log($"supportsRayTracing: {SystemInfo.supportsRayTracing}");
            Debug.Log($"supportsInlineRayTracing: {SystemInfo.supportsInlineRayTracing}");
            return;
        }

        if (resultTexture != null)
            Graphics.Blit(Texture2D.blackTexture, resultTexture);
    }

    void OnDisable()
    {
        ReleaseBuffers();
        startupDone = false;
    }

    void OnValidate()
    {
        tlasDirty = true;
        blackHolesDirty = true;
        numRenderedFrames = 0;

        if (sortBucketsPerAxis != lastSortBucketsPerAxis)
        {
            buffersHaveRealData = false;
            bucketCountsBuffer?.Release();  bucketCountsBuffer  = null;
            bucketOffsetsBuffer?.Release(); bucketOffsetsBuffer = null;
        }
    }

    void OnDestroy()         { ReleaseBuffers(); }
    void OnApplicationQuit() { ReleaseBuffers(); }
    
    void EnsureMaterialsCreated()
    {
        if (flagVisualizerMaterial == null && flagVisualizerShader != null)
            flagVisualizerMaterial = new Material(flagVisualizerShader);

        if (accumulatorMaterial == null && accumulatorShader != null)
            ShaderHelper.InitMaterial(accumulatorShader, ref accumulatorMaterial);

        if (ditherMaterial == null && ditherShader != null)
            ShaderHelper.InitMaterial(ditherShader, ref ditherMaterial);

        if (colorQuantizationMaterial == null && ColorQuantizationShader != null)
            ShaderHelper.InitMaterial(ColorQuantizationShader, ref colorQuantizationMaterial);

        if (atrousMaterial == null && atrousShader != null)
            ShaderHelper.InitMaterial(atrousShader, ref atrousMaterial);
    }
    
    bool AnyInstanceNeedsUpdate(List<RayTracedMesh> v)
    {
        for (int i = 0; i < v.Count; i++) if (v[i].update) return true;
        return false;
    }

    bool AnyTransformDirty(List<RayTracedMesh> v)
    {
        for (int i = 0; i < v.Count; i++) if (v[i].transformDirty) return true;
        return false;
    }
    
    void AllocateAccelerationBuffers()
    {
        bool useHardwareRT = SystemInfo.supportsRayTracing && !forceSoftwareRaytracing;
        GetValidMeshInstancesCached(validInstancesCache);

        if (forceSoftwareRaytracing != lastForceSoftware)
        {
            tlasDirty = true;
            buffersHaveRealData = false;
            lastInstanceCount = -1;
            lastForceSoftware = forceSoftwareRaytracing;
        }

        bool anyTransformDirty = AnyTransformDirty(validInstancesCache);
        bool anyMeshUpdated = AnyInstanceNeedsUpdate(validInstancesCache);

        if (useHardwareRT)
        {
            bool instanceCountChanged = validInstancesCache.Count != lastInstanceCount;

            if (tlasDirty || anyMeshUpdated || instanceCountChanged)
            {
                GetUniqueSharedMeshesCached(validInstancesCache, uniqueMeshesCache);
                ComputeHardwareMeshOffsetsCached(uniqueMeshesCache, offsetsCache); // hardware version
                BuildAccelStructure(validInstancesCache);
                BindHardwareRTBuffers();
                tlasDirty = false;
                lastInstanceCount = validInstancesCache.Count;
                for (int i = 0; i < validInstancesCache.Count; i++)
                    validInstancesCache[i].update = false;
            }
            else if (anyTransformDirty)
            {
                // update transforms only, no full rebuild
                for (int i = 0; i < lastBuiltMeshOrderList.Count && i < accelStructureInstanceIDs.Length; i++)
                    accelStructure.UpdateInstanceTransform(
                        accelStructureInstanceIDs[i],
                        lastBuiltMeshOrderList[i].transform.localToWorldMatrix);

                accelStructure.Build(); // incremental update
            }

            for (int i = 0; i < validInstancesCache.Count; i++) validInstancesCache[i].transformDirty = false;
            classifyCompute.SetRayTracingAccelerationStructure(0, "_RTAS", accelStructure);
            return;
        }

        if (forceBufferRecreation)
        {
            buffersHaveRealData = false;
            tlasDirty = true;
            forceBufferRecreation = false;
        }

        if (validInstancesCache.Count == 0)
        {
            EnsureBuffersCreated();
            return;
        }

        if (validInstancesCache.Count != lastInstanceCount)
        {
            tlasDirty = true;
            lastInstanceCount = validInstancesCache.Count;
        }

        if (!tlasDirty && !anyMeshUpdated && !anyTransformDirty && buffersHaveRealData) return;

        GetUniqueSharedMeshesCached(validInstancesCache, uniqueMeshesCache);
        ComputeMeshOffsetsCached(uniqueMeshesCache, offsetsCache);
        BuildGlobalBLASGeometry(uniqueMeshesCache, validInstancesCache, offsetsCache);
        BuildAndUploadTLAS(validInstancesCache, offsetsCache);
    }

    void ApplyBlackHoleLUT(ComputeShader cs, RayTracedBlackHole blackHole)
    {
        cs.SetFloat("bendStrength", bendStrength);
        cs.SetFloat("strongFieldCurvatureRadPetMeterCutoff", strongFieldRadPerMeterCuttoff);
    }
    
}