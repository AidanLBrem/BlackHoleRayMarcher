using System;
using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using Debug = UnityEngine.Debug;
using UnityEngine.Rendering;
#if UNITY_EDITOR
#endif

[ExecuteAlways, ImageEffectAllowedInSceneView]
public partial class RayTracingManagerWavefront : MonoBehaviour
{
    // ─── Queue Constants ───────────────────────────────────────────────────────
    private const int NUM_QUEUES          = 100;
    private const int ACTIVE_RAY_QUEUE    = 0;
    private const int LINEAR_RAY_QUEUEA   = 1;
    private const int GEODISC_RAY_QUEUEA  = 2;
    private const int LINEAR_RAY_QUEUEB   = 3;
    private const int GEODISC_RAY_QUEUEB  = 4;
    private const int REFLECTION_QUEUE    = 5;
    private const int NEE_QUEUE           = 6;
    private const int SCATTER_QUEUE       = 7;
    private const int SKYBOX_QUEUE        = 8;

    // ─── Kernel Constants ──────────────────────────────────────────────────────
    private const int KERNEL_CLEAR_BUCKETS = 0;
    private const int KERNEL_COUNT_BUCKETS = 1;
    private const int KERNEL_PREFIX_SUM    = 2;
    private const int KERNEL_SCATTER_RAYS  = 3;
    private const int KERNEL_FIXUP_QUEUE   = 4;
    // ─── Compute Kernels ───────────────────────────────────────────────────────
    [Header("Compute Kernels")]
    public ComputeShader initCompute;
    public ComputeShader classifyCompute;
    public ComputeShader reflectionCompute;
    public ComputeShader neeCompute;
    public ComputeShader accumulateCompute;
    public ComputeShader writeIndirectArgsCompute;
    public ComputeShader resetCountCompute;
    public ComputeShader bucketSortCompute;

    // ─── Rendering ─────────────────────────────────────────────────────────────
    [Header("Rendering")]
    [Range(0.05f, 1.0f)]
    public float renderScale = 0.5f;
    public float renderDistance;
    public int   raysPerPixel;
    public int   maxBounces;
    public bool  renderSphere    = true;
    public bool  renderTriangles = true;

    // ─── Ray Tracing ───────────────────────────────────────────────────────────
    [Header("Ray Tracing")]
    public bool             forceSoftwareRaytracing;
    [SerializeField] public bool useTlas = true;
    [SerializeField] public bool useNEE  = true;
    private int numLightSources;
    public bool useRedshifting = true;

    // ─── Ray Sorting ───────────────────────────────────────────────────────────
    [Header("Ray Sorting")]
    [Range(1, 8)] public int sortBucketsPerAxis = 4;
    [Range(1, 8)] public int maxLinearGeodiscPingPongIterations = 4;

    // ─── Accumulation ──────────────────────────────────────────────────────────
    [Header("Accumulation")]
    public Shader accumulatorShader;
    public float  accumWeight           = 1.0f;
    public bool   accumlateInSceneView  = true;
    public bool   accumulateInGameView  = true;
    public float  numFrames             = 10000;

    // ─── Post Processing ───────────────────────────────────────────────────────
    [Header("A-Trous Filter")]
    public bool   atrousFilter        = false;
    public Shader atrousShader;
    public float  atrousColorSigma    = 0.6f;
    public bool   atrousBeforeUpscale = false;

    [Header("Dithering")]
    public bool   ditherPostProcess   = false;
    public Shader ditherShader;
    public bool   ditherBeforeUpscale = false;
    public int    ditherMatrixSize    = 4;

    [Header("Color Quantization")]
    public bool   colorQuantization = false;
    public Shader ColorQuantizationShader;
    public int    numColors         = 256;

    // ─── TLAS Settings ─────────────────────────────────────────────────────────
    [Header("TLAS Settings")]
    public int tlasMaxLeafSize = 2;
    public int tlasMaxDepth    = 32;
    public int tlasNumBins     = 8;

    // ─── Black Hole ────────────────────────────────────────────────────────────
    [Header("Black Hole")]
    public float blackHoleSOIStepSize       = 0.01f;
    public int   emergencyBreakMaxSteps      = 1000;
    public int   marchStepsCount;
    public bool  enable_lensing              = true;
    public float bendStrength                = 1.0f;
    public bool  impactParameterDebug        = false;
    public bool  useOrbitalPlaneCullingIfAble = true;
    public float strongFieldRadPerMeterCuttoff = 0.01f;
    public bool  useStepsPerCollision        = true;
    public int   StepsPerCollisionTest       = 3;
    public bool  useRayMagnification         = false;

    // ─── Black Hole Debug ──────────────────────────────────────────────────────
    [Header("Black Hole Debug")]
    public bool displayTriTests              = false;
    public int  triTestFullSaturationValue   = 1000;
    public bool displayBVHNodeTests          = false;
    public int  BVHNodeTestSaturationValue   = 10;
    public bool displayTLASNodeVisits        = false;
    public int  TLASNodeVisitsSaturationValue = 1000;
    public bool displayBLASNodeVisits        = false;
    public int  BLASNodeVisitsSaturationValue = 1000;
    public bool displayInstanceBLASTraversals = false;
    public int  InstanceBLASTraversalsSaturationValue = 1000;
    public bool displayTLASLeafRefs          = false;
    public int  TLASLeafRefsSaturationValue  = 1000;

    // ─── Atmosphere ────────────────────────────────────────────────────────────
    [Header("Atmosphere")]
    public bool  applyScattering   = true;
    public bool  applyRayleigh     = true;
    public bool  applyMie          = true;
    public bool  applySundisk      = true;
    public bool  applySunLighting  = true;
    public float planetRadius      = 6378137.0f;
    public float atmosphereRadius  = 6538137.0f;
    public int   framesPerScatter  = 10;
    public int   numOpticalDepthPoints = 8;
    public int   inScatteringPoints    = 8;

    [Header("Rayleigh Scattering")]
    public float  densityFalloffRayleigh        = 4f;
    public Vector3 rayleighScatteringCoefficients = new Vector3(0.0058f, 0.0135f, 0.0331f);

    [Header("Mie Scattering")]
    public float  densityFalloffMie          = 4f;
    public float  mieForwardScatter          = 0.76f;
    public float  mieBackwardScatter         = -0.5f;
    public Vector3 mieScatteringCoefficients = new Vector3(21e-6f, 21e-6f, 21e-6f);

    // ─── Sun ───────────────────────────────────────────────────────────────────
    [Header("Sun")]
    public Color     sunLightColor     = Color.white;
    public float     sunLightIntensity = 1;
    public Transform sun;

    // ─── Debug ─────────────────────────────────────────────────────────────────
    [Header("Debug")]
    public bool   forceBufferRecreation = false;
    [SerializeField] bool useShaderInSceneView = true;

    // ─── Runtime State (non-serialized) ────────────────────────────────────────
    int  numRenderedFrames = 0;
    int  scaledW           = 1;
    int  scaledH           = 1;
    private int   baseSeed         = 0;
    private float lastRenderScale  = -1f;
    private bool  startupDone      = false;
    private bool  tlasDirty        = true;
    private int   lastInstanceCount      = -1;
    private bool  buffersHaveRealData    = false;
    private bool  lastForceSoftware      = false;
    private int   lastSortBucketsPerAxis = -1;
    private bool  blackHolesDirty        = true;
    private bool  historyInitialized     = false;

    Vector3    lastCameraPosition;
    Quaternion lastCameraRotation;
    float      lastCameraFov;
    int        lastScreenWidth;
    int        lastScreenHeight;

    private static readonly uint[] zeroOne         = { 0 };
    private static readonly uint[] zerosNUM_QUEUES = new uint[NUM_QUEUES];
    private static readonly int[]  atrousStepSizes = { 1, 2, 4, 8, 16 };

    // ─── Render Textures ───────────────────────────────────────────────────────
    RenderTexture resultTexture;
    private RenderTexture cleanAccumBuffer;
    private Material      accumulatorMaterial;
    private Material      ditherMaterial;
    private Material      colorQuantizationMaterial;
    private Material      atrousMaterial;

    // ─── Compute Buffers ───────────────────────────────────────────────────────
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
    [NonSerialized] ComputeBuffer         blackHoleBuffer;
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

    [NonSerialized] private ComputeBuffer neeQueueBuffer;
    // ─── Bucket Sort Buffers ───────────────────────────────────────────────────
    [NonSerialized] private ComputeBuffer bucketCountsBuffer;
    [NonSerialized] private ComputeBuffer bucketOffsetsBuffer;
    [NonSerialized] private ComputeBuffer sortedRaysBuffer;
    [NonSerialized] private ComputeBuffer sortedRayIndicesBuffer;
    [NonSerialized] private ComputeBuffer pixelForSlotBuffer;
    [NonSerialized] private ComputeBuffer sortedPixelForSlotBuffer;
    [NonSerialized] private ComputeBuffer newSlotForOldSlotBuffer;
    [NonSerialized] private ComputeBuffer sortedControlsBuffer;
    [NonSerialized] private ComputeBuffer sortedRayColorInfoBuffer;
    
    // ─── Acceleration Structure ────────────────────────────────────────────────
    private RayTracingAccelerationStructure accelStructure;
    private int[] accelStructureInstanceIDs;
    private readonly List<RayTracedMesh> lastBuiltMeshOrderList = new();

    // ─── CPU-side Caches ───────────────────────────────────────────────────────
    private readonly List<RayTracedMesh>                     validInstancesCache  = new();
    private readonly List<SharedMeshData>                    uniqueMeshesCache    = new();
    private readonly HashSet<SharedMeshData>                 uniqueMeshesSet      = new();
    private readonly Dictionary<SharedMeshData, MeshOffsets> offsetsCache         = new();

    private RayTracedBlackHole[] cachedBlackHoleObjects = Array.Empty<RayTracedBlackHole>();
    private BlackHole[]          cachedBlackHoles       = Array.Empty<BlackHole>();

    private BvhInstance[]                       tlasInstances        = Array.Empty<BvhInstance>();
    private MeshStruct[]                        tlasGpuInstances     = Array.Empty<MeshStruct>();
    private GPULightSource[]                    tlasLightSources     = Array.Empty<GPULightSource>();
    private uint[]                              tlasRefsCache        = Array.Empty<uint>();
    private GPUBVHNode[]                        tlasNodesCache       = Array.Empty<GPUBVHNode>();
    private readonly List<int>                  lightTriIndicesCache = new();
    private readonly List<GPULightTriangleData> lightTriDataCache    = new();

    private Triangle[]            blasTriangles       = Array.Empty<Triangle>();
    private uint[]                blasTriangleIndices = Array.Empty<uint>();
    private GPUBVHNode[]          blasBVHNodes        = Array.Empty<GPUBVHNode>();
    private readonly List<float3> blasVertices        = new();
    private readonly List<float3> blasNormals         = new();

    static readonly List<Vector3> tV = new();
    static readonly List<Vector3> tN = new();
    private int totalBVHNodes;
    private TLASBuilder tlasBuilder;
    void Swap(ref RenderTexture a, ref RenderTexture b) => (a, b) = (b, a);
    
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
        BindBuffersToShaders();
    }

    void ApplyBlackHoleLUT(ComputeShader cs, RayTracedBlackHole blackHole)
    {
        cs.SetFloat("bendStrength", bendStrength);
        cs.SetFloat("strongFieldCurvatureRadPetMeterCutoff", strongFieldRadPerMeterCuttoff);
    }
    
}