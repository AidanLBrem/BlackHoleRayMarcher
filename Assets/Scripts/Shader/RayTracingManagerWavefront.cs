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
public class RayTracingManagerWavefront : MonoBehaviour
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
    public ComputeShader linearMarchCompute;
    public ComputeShader geodiscMarchCompute;
    public ComputeShader reflectionCompute;
    public ComputeShader accumulateCompute;
    public ComputeShader compactCompute;
    public ComputeShader writeIndirectArgsCompute;
    public ComputeShader resetCountCompute;

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

    private bool tlasDirty = true;
    private int lastInstanceCount = -1;
    private bool buffersHaveRealData = false;
    private bool lastForceSoftware = false;

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

    struct Control      { private uint flags; private uint rngState; }
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
        private uint    didHit;
        private float   distance;
        private float   u;
        private float   v;
        private uint    triIndex;
        private int     objectIndex;
        private Vector3 worldNormal;
    }

    bool ShouldResetAccumulation(Camera cam)
    {
        if (!historyInitialized)
        {
            lastCameraPosition = cam.transform.position;
            lastCameraRotation = cam.transform.rotation;
            lastCameraFov      = cam.fieldOfView;
            lastScreenWidth    = Screen.width;
            lastScreenHeight   = Screen.height;
            historyInitialized = true;
            return true;
        }

        float posDelta = Vector3.Distance(cam.transform.position, lastCameraPosition);
        float rotDelta = Quaternion.Angle(cam.transform.rotation, lastCameraRotation);
        float fovDelta = Mathf.Abs(cam.fieldOfView - lastCameraFov);

        bool changed =
            posDelta > 0.0005f || rotDelta > 0.05f || fovDelta > 0.01f ||
            Screen.width != lastScreenWidth || Screen.height != lastScreenHeight;

        if (changed)
        {
            lastCameraPosition = cam.transform.position;
            lastCameraRotation = cam.transform.rotation;
            lastCameraFov      = cam.fieldOfView;
            lastScreenWidth    = Screen.width;
            lastScreenHeight   = Screen.height;
        }

        return changed;
    }

    void Start() { }

    void OnEnable()
    {
        tlasDirty = true;
        buffersHaveRealData = false;
        lastInstanceCount = -1;
        numRenderedFrames = 0;
        historyInitialized = false;
        blackHolesDirty = true;
        startupDone = false;

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
    }

    void Startup()
    {
        ReleaseBuffers();
        tlasDirty = true;
        buffersHaveRealData = false;
        lastInstanceCount = -1;
        blackHolesDirty = true;
        flagVisualizerMaterial = null;
        accumulatorMaterial = null;
        ditherMaterial = null;
        colorQuantizationMaterial = null;
        atrousMaterial = null;
        numRenderedFrames = 0;
        EnsureBuffersCreated();
        EnsureMaterialsCreated();
        BindBuffersToShaders();
        UpdateCameraParams(Camera.current != null ? Camera.current : GetComponent<Camera>());
        ApplyRaytracingMode();
    }

    void OnDestroy()         { ReleaseBuffers(); }
    void OnApplicationQuit() { ReleaseBuffers(); }

    void ReleaseBuffers()
    {
        sphereBuffer?.Release();               sphereBuffer               = null;
        MeshVerticesBuffer?.Release();         MeshVerticesBuffer         = null;
        MeshNormalsBuffer?.Release();          MeshNormalsBuffer          = null;
        MeshIndicesBuffer?.Release();          MeshIndicesBuffer          = null;
        TriangleBuffer?.Release();             TriangleBuffer             = null;
        BVHBuffer?.Release();                  BVHBuffer                  = null;
        TLASBuffer?.Release();                 TLASBuffer                 = null;
        TLASRefBuffer?.Release();              TLASRefBuffer              = null;
        InstanceBuffer?.Release();             InstanceBuffer             = null;
        LightSourceBuffer?.Release();          LightSourceBuffer          = null;
        LightTriangleIndicesBuffer?.Release(); LightTriangleIndicesBuffer = null;
        LightTrianglesDataBuffer?.Release();   LightTrianglesDataBuffer   = null;
        controlQueue?.Release();               controlQueue               = null;
        mainRayBuffer?.Release();              mainRayBuffer              = null;
        blackHoleBuffer?.Release();            blackHoleBuffer            = null;
        HitInfoBuffer?.Release();              HitInfoBuffer              = null;
        rayColorInfoBuffer?.Release();         rayColorInfoBuffer         = null;
        pixelAccumBuffer?.Release();           pixelAccumBuffer           = null;
        activeRayIndicesBuffer?.Release();     activeRayIndicesBuffer     = null;
        activeRayCountBuffer?.Release();       activeRayCountBuffer       = null;
        indirectArgsBuffer?.Release();         indirectArgsBuffer         = null;
        linearMarchQueueBufferA?.Release();    linearMarchQueueBufferA    = null;
        linearMarchQueueBufferB?.Release();    linearMarchQueueBufferB    = null;
        geodiscMarchQueueBufferA?.Release();   geodiscMarchQueueBufferA   = null;
        geodiscMarchQueueBufferB?.Release();   geodiscMarchQueueBufferB   = null;
        reflectionQueueBuffer?.Release();      reflectionQueueBuffer      = null;
        skyboxQueueBuffer?.Release();          skyboxQueueBuffer          = null;

        if (accelStructure != null) { accelStructure.Release(); accelStructure = null; }
        resultTexture?.Release();    resultTexture = null;
        cleanAccumBuffer?.Release(); cleanAccumBuffer = null;

        if (flagVisualizerMaterial    != null) { DestroyImmediate(flagVisualizerMaterial);    flagVisualizerMaterial    = null; }
        if (accumulatorMaterial       != null) { DestroyImmediate(accumulatorMaterial);       accumulatorMaterial       = null; }
        if (ditherMaterial            != null) { DestroyImmediate(ditherMaterial);            ditherMaterial            = null; }
        if (colorQuantizationMaterial != null) { DestroyImmediate(colorQuantizationMaterial); colorQuantizationMaterial = null; }
        if (atrousMaterial            != null) { DestroyImmediate(atrousMaterial);            atrousMaterial            = null; }

        buffersHaveRealData = false;
    }

    void EnsureBuffersCreated(bool forceRecreate = false)
    {
        int pixelCount = Screen.width * Screen.height;
        if (forceRecreate) ReleaseBuffers();

        if (TriangleBuffer             == null) ShaderHelper.CreateStructuredBuffer<Triangle>(ref TriangleBuffer, 1);
        if (BVHBuffer                  == null) ShaderHelper.CreateStructuredBuffer<GPUBVHNode>(ref BVHBuffer, 1);
        if (TLASBuffer                 == null) ShaderHelper.CreateStructuredBuffer<GPUBVHNode>(ref TLASBuffer, 1);
        if (TLASRefBuffer              == null) ShaderHelper.CreateStructuredBuffer<uint>(ref TLASRefBuffer, 1);
        if (InstanceBuffer             == null) ShaderHelper.CreateStructuredBuffer<MeshStruct>(ref InstanceBuffer, 1);
        if (MeshVerticesBuffer         == null) ShaderHelper.CreateStructuredBuffer<Vector3>(ref MeshVerticesBuffer, 1);
        if (MeshNormalsBuffer          == null) ShaderHelper.CreateStructuredBuffer<Vector3>(ref MeshNormalsBuffer, 1);
        if (MeshIndicesBuffer          == null) ShaderHelper.CreateStructuredBuffer<uint>(ref MeshIndicesBuffer, 1);
        if (LightSourceBuffer          == null) ShaderHelper.CreateStructuredBuffer<GPULightSource>(ref LightSourceBuffer, 1);
        if (LightTriangleIndicesBuffer == null) ShaderHelper.CreateStructuredBuffer<int>(ref LightTriangleIndicesBuffer, 1);
        if (LightTrianglesDataBuffer   == null) ShaderHelper.CreateStructuredBuffer<GPULightTriangleData>(ref LightTrianglesDataBuffer, 1);
        if (sphereBuffer               == null) ShaderHelper.CreateStructuredBuffer<Sphere>(ref sphereBuffer, 1);
        if (blackHoleBuffer            == null) ShaderHelper.CreateStructuredBuffer<BlackHole>(ref blackHoleBuffer, 1);

        ShaderHelper.CreateStructuredBuffer<Control>(ref controlQueue, pixelCount);
        ShaderHelper.CreateStructuredBuffer<MainRay>(ref mainRayBuffer, pixelCount);
        ShaderHelper.CreateStructuredBuffer<HitInfo>(ref HitInfoBuffer, pixelCount);
        ShaderHelper.CreateStructuredBuffer<RayColorInfo>(ref rayColorInfoBuffer, pixelCount);
        ShaderHelper.CreateStructuredBuffer<Vector3>(ref pixelAccumBuffer, pixelCount);

        ShaderHelper.CreateStructuredBuffer<uint>(ref linearMarchQueueBufferA, pixelCount);
        ShaderHelper.CreateStructuredBuffer<uint>(ref linearMarchQueueBufferB, pixelCount);
        ShaderHelper.CreateStructuredBuffer<uint>(ref geodiscMarchQueueBufferA, pixelCount);
        ShaderHelper.CreateStructuredBuffer<uint>(ref geodiscMarchQueueBufferB, pixelCount);
        ShaderHelper.CreateStructuredBuffer<uint>(ref reflectionQueueBuffer, pixelCount);
        ShaderHelper.CreateStructuredBuffer<uint>(ref skyboxQueueBuffer, pixelCount);

        ShaderHelper.CreateStructuredBuffer<uint>(ref activeRayIndicesBuffer, pixelCount);
        ShaderHelper.CreateStructuredBuffer<uint>(ref activeRayCountBuffer, NUM_QUEUES);

        if (indirectArgsBuffer == null || !indirectArgsBuffer.IsValid())
        {
            indirectArgsBuffer?.Release();
            indirectArgsBuffer = new ComputeBuffer(NUM_QUEUES * 3, sizeof(uint), ComputeBufferType.IndirectArguments);
        }
    }

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

    void BindBuffersToShaders()
    {
        BindInitBuffers();
        BindClassifyBuffers();
        BindLinearMarchBuffers();
        BindGeodiscMarchBuffers();
        BindReflectionBuffers();
        BindAccumulateBuffers();
        BindFlagVisualizerBuffers();
        BindWavefrontBuffers();
        BindIndirectArgs();
    }

    void BindWavefrontBuffers()
    {
        if (compactCompute != null)
        {
            compactCompute.SetBuffer(0, "controls", controlQueue);
            compactCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
            compactCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        }

        if (writeIndirectArgsCompute != null)
        {
            writeIndirectArgsCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
            writeIndirectArgsCompute.SetBuffer(0, "indirectArgs", indirectArgsBuffer);
        }

        if (resetCountCompute != null)
            resetCountCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);

        if (linearMarchCompute != null)
            linearMarchCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);

        if (geodiscMarchCompute != null)
            geodiscMarchCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);

        if (reflectionCompute != null)
            reflectionCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);

        if (classifyCompute != null)
            classifyCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
    }

    void BindInitBuffers()
    {
        if (initCompute == null) return;

        initCompute.SetBuffer(0, "controls", controlQueue);
        initCompute.SetBuffer(0, "main_rays", mainRayBuffer);
        initCompute.SetBuffer(0, "ray_color_info", rayColorInfoBuffer);
        initCompute.SetBuffer(0, "hit_info_buffer", HitInfoBuffer);
        initCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
        initCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        initCompute.SetBuffer(0, "pixelAccum", pixelAccumBuffer);
    }

    void BindClassifyBuffers()
    {
        if (classifyCompute == null) return;

        classifyCompute.SetBuffer(0, "controls", controlQueue);
        classifyCompute.SetBuffer(0, "main_rays", mainRayBuffer);
        classifyCompute.SetBuffer(0, "blackholes", blackHoleBuffer);
        classifyCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        classifyCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
        classifyCompute.SetBuffer(0, "linearMarchQueue", linearMarchQueueBufferA);
        classifyCompute.SetBuffer(0, "geodiscMarchQueue", geodiscMarchQueueBufferA);
    }

    void BindLinearMarchBuffers()
    {
        if (linearMarchCompute == null) return;

        linearMarchCompute.SetBuffer(0, "controls", controlQueue);
        linearMarchCompute.SetBuffer(0, "hit_infos", HitInfoBuffer);
        linearMarchCompute.SetBuffer(0, "main_rays", mainRayBuffer);
        linearMarchCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        linearMarchCompute.SetBuffer(0, "linearMarchQueue", linearMarchQueueBufferA);
        linearMarchCompute.SetBuffer(0, "geodiscMarchQueue", geodiscMarchQueueBufferB);
        linearMarchCompute.SetBuffer(0, "reflectionQueue", reflectionQueueBuffer);
        linearMarchCompute.SetBuffer(0, "skyboxQueue", skyboxQueueBuffer);
        linearMarchCompute.SetBuffer(0, "pixelAccum", pixelAccumBuffer);
        linearMarchCompute.SetBuffer(0, "Instances", InstanceBuffer);
        linearMarchCompute.SetBuffer(0, "Normals", MeshNormalsBuffer);
        linearMarchCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
        linearMarchCompute.SetBuffer(0, "blackholes", blackHoleBuffer);
        linearMarchCompute.SetBuffer(0, "Triangles", TriangleBuffer);
        linearMarchCompute.SetBuffer(0, "BVHNodes", BVHBuffer);
        linearMarchCompute.SetBuffer(0, "TLASNodes", TLASBuffer);
        linearMarchCompute.SetBuffer(0, "TLASRefs", TLASRefBuffer);
        linearMarchCompute.SetBuffer(0, "Vertices", MeshVerticesBuffer);

        if (linearMarchRaytraceShader != null)
        {
            linearMarchRaytraceShader.SetBuffer("controls", controlQueue);
            linearMarchRaytraceShader.SetBuffer("main_rays", mainRayBuffer);
            linearMarchRaytraceShader.SetBuffer("hit_info_buffer", HitInfoBuffer);
            linearMarchRaytraceShader.SetBuffer("Instances", InstanceBuffer);
            linearMarchRaytraceShader.SetBuffer("Normals", MeshNormalsBuffer);
            linearMarchRaytraceShader.SetBuffer("TriangleIndices", MeshIndicesBuffer);
        }
    }

    void BindGeodiscMarchBuffers()
    {
        if (geodiscMarchCompute == null) return;

        geodiscMarchCompute.SetBuffer(0, "controls", controlQueue);
        geodiscMarchCompute.SetBuffer(0, "main_rays", mainRayBuffer);
        geodiscMarchCompute.SetBuffer(0, "hit_infos", HitInfoBuffer);
        geodiscMarchCompute.SetBuffer(0, "Instances", InstanceBuffer);
        geodiscMarchCompute.SetBuffer(0, "Normals", MeshNormalsBuffer);
        geodiscMarchCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
        geodiscMarchCompute.SetBuffer(0, "blackholes", blackHoleBuffer);
        geodiscMarchCompute.SetBuffer(0, "pixelAccum", pixelAccumBuffer);
        geodiscMarchCompute.SetBuffer(0, "Triangles", TriangleBuffer);
        geodiscMarchCompute.SetBuffer(0, "BVHNodes", BVHBuffer);
        geodiscMarchCompute.SetBuffer(0, "TLASNodes", TLASBuffer);
        geodiscMarchCompute.SetBuffer(0, "TLASRefs", TLASRefBuffer);
        geodiscMarchCompute.SetBuffer(0, "Vertices", MeshVerticesBuffer);
        geodiscMarchCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        geodiscMarchCompute.SetBuffer(0, "geodiscMarchQueue", geodiscMarchQueueBufferA);
        geodiscMarchCompute.SetBuffer(0, "linearMarchQueue", linearMarchQueueBufferB);
        geodiscMarchCompute.SetBuffer(0, "reflectionQueue", reflectionQueueBuffer);
        geodiscMarchCompute.SetBuffer(0, "skyboxQueue", skyboxQueueBuffer);
        geodiscMarchCompute.SetInt("emergencyBreakMaxSteps", emergencyBreakMaxSteps);
        geodiscMarchCompute.SetFloat("stepSize", blackHoleSOIStepSize);

    }

    void BindReflectionBuffers()
    {
        if (reflectionCompute == null) return;

        reflectionCompute.SetBuffer(0, "controls", controlQueue);
        reflectionCompute.SetBuffer(0, "main_rays", mainRayBuffer);
        reflectionCompute.SetBuffer(0, "hit_info_buffer", HitInfoBuffer);
        reflectionCompute.SetBuffer(0, "ray_color_info", rayColorInfoBuffer);
        reflectionCompute.SetBuffer(0, "pixelAccum", pixelAccumBuffer);
        reflectionCompute.SetBuffer(0, "reflectionQueue", reflectionQueueBuffer);
        reflectionCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
        reflectionCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        reflectionCompute.SetBuffer(0, "Instances", InstanceBuffer);
        reflectionCompute.SetBuffer(0, "Triangles", TriangleBuffer);
        reflectionCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
        reflectionCompute.SetBuffer(0, "Normals", MeshNormalsBuffer);
        reflectionCompute.SetBuffer(0, "BVHNodes", BVHBuffer);
        reflectionCompute.SetBuffer(0, "TLASNodes", TLASBuffer);
        reflectionCompute.SetBuffer(0, "TLASRefs", TLASRefBuffer);
    }

    void BindAccumulateBuffers()
    {
        if (accumulateCompute == null) return;
        accumulateCompute.SetBuffer(0, "pixelAccum", pixelAccumBuffer);
    }

    void BindFlagVisualizerBuffers()
    {
        if (flagVisualizerMaterial == null) return;
        flagVisualizerMaterial.SetBuffer("controls", controlQueue);
        flagVisualizerMaterial.SetBuffer("hit_info_buffer", HitInfoBuffer);
        flagVisualizerMaterial.SetBuffer("Instances", InstanceBuffer);
    }

    void BindIndirectArgs()
    {
        if (writeIndirectArgsCompute == null) return;
        writeIndirectArgsCompute.SetInt("NUM_QUEUES", NUM_QUEUES);
        writeIndirectArgsCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        writeIndirectArgsCompute.SetBuffer(0, "indirectArgs", indirectArgsBuffer);
    }

    void DispatchCompute(ComputeShader cs, int pixelCount, string label = "")
    {
        Profiler.BeginSample(label == "" ? cs.name : label);
        cs.Dispatch(0, Mathf.CeilToInt(pixelCount / 64f), 1, 1);
        Profiler.EndSample();
    }

    void DispatchWavefront(ComputeShader cs, int queueIndex, string label = "")
    {
        int groups = Mathf.CeilToInt(NUM_QUEUES / 8f);
        writeIndirectArgsCompute.Dispatch(0, groups, 1, 1);

        Profiler.BeginSample(label);
        uint byteOffset = (uint)queueIndex * 3 * sizeof(uint);
        cs.DispatchIndirect(0, indirectArgsBuffer, byteOffset);
        Profiler.EndSample();
    }

    void SetLinearMarchQueueBindings(
        ComputeBuffer linearInput,
        ComputeBuffer geodiscOutput,
        int linearInputQueueIndex,
        int geodiscOutputQueueIndex)
    {
        if (linearMarchCompute == null) return;

        linearMarchCompute.SetBuffer(0, "linearMarchQueue", linearInput);
        linearMarchCompute.SetBuffer(0, "geodiscMarchQueue", geodiscOutput);

        // REQUIRED shader-side ints in LinearMarch.compute:
        // int linearInputQueueIndex;
        // int linearToGeodiscOutputQueueIndex;
        linearMarchCompute.SetInt("linearInputQueue", linearInputQueueIndex);
        linearMarchCompute.SetInt("linearToGeodiscQueue", geodiscOutputQueueIndex);
    }

    void SetGeodiscMarchQueueBindings(
        ComputeBuffer geodiscInput,
        ComputeBuffer linearOutput,
        int geodiscInputQueueIndex,
        int linearOutputQueueIndex)
    {
        if (geodiscMarchCompute == null) return;

        geodiscMarchCompute.SetBuffer(0, "geodiscMarchQueue", geodiscInput);
        geodiscMarchCompute.SetBuffer(0, "linearMarchQueue", linearOutput);

        // REQUIRED shader-side ints in GeodiscMarch.compute:
        // int geodiscInputQueueIndex;
        // int geodiscToLinearOutputQueueIndex;
        geodiscMarchCompute.SetInt("geodiscInputQueueIndex", geodiscInputQueueIndex);
        geodiscMarchCompute.SetInt("geodiscToLinearOutputQueueIndex", linearOutputQueueIndex);
    }

    void BuildHardwareGeometryBuffers(List<RayTracedMesh> meshes, MeshStruct[] gpuInstances)
    {
        int totalVerts = 0, totalTris = 0;
        for (int i = 0; i < meshes.Count; i++)
        {
            MeshFilter filter = meshes[i].GetComponent<MeshFilter>();
            if (filter == null) continue;
            totalVerts += filter.sharedMesh.vertexCount;
            totalTris  += (int)filter.sharedMesh.GetIndexCount(0) / 3;
        }

        blasVertices.Clear();
        blasNormals.Clear();

        int triCount = Mathf.Max(1, totalTris);
        Triangle[] triangles = new Triangle[triCount];
        uint[] triangleIndices = new uint[triCount * 3];

        int vertexOffset = 0;
        int triangleOffset = 0;

        for (int i = 0; i < meshes.Count; i++)
        {
            MeshFilter filter = meshes[i].GetComponent<MeshFilter>();
            if (filter == null) continue;
            Mesh mesh = filter.sharedMesh;

            mesh.GetVertices(tV);
            mesh.GetNormals(tN);

            for (int v = 0; v < tV.Count; v++) blasVertices.Add(tV[v]);
            if (tN.Count == tV.Count)
                for (int n = 0; n < tN.Count; n++) blasNormals.Add(tN[n]);
            else
                for (int n = 0; n < tV.Count; n++) blasNormals.Add(Vector3.up);

            int[] meshTris = mesh.triangles;
            int rawTriCount = meshTris.Length / 3;

            gpuInstances[i].triangleOffset = (uint)triangleOffset;

            for (int t = 0; t < rawTriCount; t++)
            {
                int globalTriIndex = triangleOffset + t;
                int triIndexBase   = globalTriIndex * 3;
                int v0 = meshTris[t * 3 + 0];
                int v1 = meshTris[t * 3 + 1];
                int v2 = meshTris[t * 3 + 2];

                triangles[globalTriIndex] = new Triangle
                {
                    baseIndex = (uint)triIndexBase,
                    edgeAB = (Vector3)tV[v1] - (Vector3)tV[v0],
                    edgeAC = (Vector3)tV[v2] - (Vector3)tV[v0],
                };

                triangleIndices[triIndexBase + 0] = (uint)(vertexOffset + v0);
                triangleIndices[triIndexBase + 1] = (uint)(vertexOffset + v1);
                triangleIndices[triIndexBase + 2] = (uint)(vertexOffset + v2);
            }

            vertexOffset += mesh.vertexCount;
            triangleOffset += rawTriCount;
            tV.Clear();
            tN.Clear();
        }

        ShaderHelper.CreateStructuredBuffer(ref MeshVerticesBuffer, blasVertices);
        ShaderHelper.CreateStructuredBuffer(ref MeshNormalsBuffer,  blasNormals);
        ShaderHelper.CreateStructuredBuffer(ref TriangleBuffer,     triangles);
        ShaderHelper.CreateStructuredBuffer(ref MeshIndicesBuffer,  triangleIndices);

        if (reflectionCompute != null)
        {
            reflectionCompute.SetBuffer(0, "Triangles",       TriangleBuffer);
            reflectionCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
            reflectionCompute.SetBuffer(0, "Normals",         MeshNormalsBuffer);
        }
    }

    void BuildAccelStructure(List<RayTracedMesh> meshes)
    {
        if (accelStructure != null) accelStructure.Release();

        accelStructure = new RayTracingAccelerationStructure(new RayTracingAccelerationStructure.Settings
        {
            rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything,
            managementMode = RayTracingAccelerationStructure.ManagementMode.Manual,
            layerMask = 255
        });

        lastBuiltMeshOrderList.Clear();
        lastBuiltMeshOrderList.AddRange(meshes);

        if (accelStructureInstanceIDs == null || accelStructureInstanceIDs.Length != meshes.Count)
            accelStructureInstanceIDs = new int[meshes.Count];

        if (tlasGpuInstances.Length != meshes.Count)
            tlasGpuInstances = new MeshStruct[meshes.Count];

        BuildHardwareGeometryBuffers(meshes, tlasGpuInstances);

        for (int i = 0; i < meshes.Count; i++)
        {
            RayTracedMesh m = meshes[i];
            MeshRenderer renderer = m.GetComponent<MeshRenderer>();
            MeshFilter filter = m.GetComponent<MeshFilter>();
            if (renderer == null || filter == null) continue;

            accelStructureInstanceIDs[i] = accelStructure.AddInstance(
                new RayTracingMeshInstanceConfig
                {
                    mesh = filter.sharedMesh,
                    subMeshIndex = 0,
                    material = renderer.sharedMaterial,
                    enableTriangleCulling = false,
                },
                m.transform.localToWorldMatrix, null, (uint)i);

            tlasGpuInstances[i].localToWorldMatrix = m.transform.localToWorldMatrix;
            tlasGpuInstances[i].worldToLocalMatrix = m.transform.worldToLocalMatrix;
            tlasGpuInstances[i].material = m.material;
            tlasGpuInstances[i].firstBVHNodeIndex = 0;
        }

        ShaderHelper.UploadStructuredBuffer(ref InstanceBuffer, tlasGpuInstances);

        if (flagVisualizerMaterial != null) flagVisualizerMaterial.SetBuffer("Instances", InstanceBuffer);
        if (reflectionCompute != null) reflectionCompute.SetBuffer(0, "Instances", InstanceBuffer);

        accelStructure.Build();
    }

    void DispatchHardwareLinearMarch(int width, int height)
    {
        if (linearMarchRaytraceShader == null) { Debug.LogError("linearMarchRaytraceShader not assigned!"); return; }
        if (accelStructure == null) { Debug.LogError("accelStructure is null!"); return; }
        linearMarchRaytraceShader.Dispatch("RayGen", width, height, 1);
    }

    void UpdateAtmosphereParams() { }

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

    void ApplyRaytracingMode() { }

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
                ComputeMeshOffsetsCached(uniqueMeshesCache, offsetsCache);
                BuildAccelStructure(validInstancesCache);
                tlasDirty = false;
                lastInstanceCount = validInstancesCache.Count;
                for (int i = 0; i < validInstancesCache.Count; i++) validInstancesCache[i].update = false;
            }
            else if (anyTransformDirty)
            {
                for (int i = 0; i < lastBuiltMeshOrderList.Count && i < accelStructureInstanceIDs.Length; i++)
                    accelStructure.UpdateInstanceTransform(
                        accelStructureInstanceIDs[i],
                        lastBuiltMeshOrderList[i].transform.localToWorldMatrix);

                accelStructure.Build();
            }

            for (int i = 0; i < validInstancesCache.Count; i++) validInstancesCache[i].transformDirty = false;
            linearMarchRaytraceShader.SetAccelerationStructure("_AccelStructure", accelStructure);
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

    void BuildGlobalBLASGeometry(
        List<SharedMeshData> sharedMeshes,
        List<RayTracedMesh> validInstances,
        Dictionary<SharedMeshData, MeshOffsets> offsets)
    {
        int totalVertexCount = 0, totalTris = 0, totalBVHNodes = 0;
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

        if (linearMarchCompute != null)
        {
            linearMarchCompute.SetBuffer(0, "Triangles", TriangleBuffer);
            linearMarchCompute.SetBuffer(0, "BVHNodes", BVHBuffer);
            linearMarchCompute.SetBuffer(0, "Vertices", MeshVerticesBuffer);
            linearMarchCompute.SetBuffer(0, "Normals", MeshNormalsBuffer);
            linearMarchCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
            linearMarchCompute.SetInt("numBLASNodes", totalBVHNodes);
        }
        

        if (reflectionCompute != null)
        {
            reflectionCompute.SetBuffer(0, "Triangles", TriangleBuffer);
            reflectionCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
            reflectionCompute.SetBuffer(0, "Normals", MeshNormalsBuffer);
            reflectionCompute.SetBuffer(0, "BVHNodes", BVHBuffer);
        }

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

        int count = meshObjects.Count;

        if (tlasInstances.Length != count) tlasInstances = new BvhInstance[count];
        if (tlasGpuInstances.Length != count) tlasGpuInstances = new MeshStruct[count];
        if (tlasLightSources.Length != count) tlasLightSources = new GPULightSource[count];

        lightTriIndicesCache.Clear();
        lightTriDataCache.Clear();
        int numLightSources = 0;

        for (int i = 0; i < count; i++) meshObjects[i].transformDirty = false;

        for (int i = 0; i < count; i++)
        {
            RayTracedMesh meshObj = meshObjects[i];
            MeshOffsets off = offsets[meshObj.sharedMesh];
            int localRootIndex = meshObj.sharedMesh.blas.RootIndex;
            Bounds localRootBounds = meshObj.sharedMesh.blas.Nodes[localRootIndex].bounds;
            Bounds worldBounds = TransformBoundsToWorld(localRootBounds, meshObj.transform);
            int globalBlasRootIndex = off.rootNodeIndex;

            tlasInstances[i] = new BvhInstance
            {
                blasIndex = i,
                blasRootIndex = globalBlasRootIndex,
                localToWorld = meshObj.transform.localToWorldMatrix,
                worldToLocal = meshObj.transform.worldToLocalMatrix,
                localBounds = localRootBounds,
                worldBounds = worldBounds,
                materialIndex = i
            };

            tlasGpuInstances[i] = new MeshStruct
            {
                localToWorldMatrix = meshObj.transform.localToWorldMatrix,
                worldToLocalMatrix = meshObj.transform.worldToLocalMatrix,
                material = meshObj.material,
                firstBVHNodeIndex = (uint)globalBlasRootIndex,
                triangleOffset = (uint)off.triangleOffset,
                AABBLeftX  = worldBounds.min.x,
                AABBLeftY  = worldBounds.min.y,
                AABBLeftZ  = worldBounds.min.z,
                AABBRightX = worldBounds.max.x,
                AABBRightY = worldBounds.max.y,
                AABBRightZ = worldBounds.max.z,
            };

            if (meshObj.material.emissiveStrength > 0)
            {
                Matrix4x4 l2w = meshObj.transform.localToWorldMatrix;
                SharedMeshData sm = meshObj.sharedMesh;
                int triStart = lightTriIndicesCache.Count;
                float totalArea = 0f;
                int[] order = sm.blas.PrimitiveRefs;

                for (int t = 0; t < order.Length; t++)
                {
                    ref buildTri bt = ref sm.buildTriangles[order[t]];
                    Vector3 worldAB = l2w.MultiplyVector(bt.posB - bt.posA);
                    Vector3 worldAC = l2w.MultiplyVector(bt.posC - bt.posA);
                    Vector3 worldCross = Vector3.Cross(worldAB, worldAC);
                    float area = worldCross.magnitude * 0.5f;
                    totalArea += area;

                    int globalTriIndex = off.triangleOffset + order[t];
                    lightTriIndicesCache.Add(globalTriIndex);

                    while (lightTriDataCache.Count <= globalTriIndex)
                        lightTriDataCache.Add(new GPULightTriangleData());

                    lightTriDataCache[globalTriIndex] = new GPULightTriangleData
                    {
                        worldSpaceArea = area,
                        worldNormal = worldCross.magnitude > 1e-10f ? worldCross.normalized : Vector3.up
                    };
                }

                tlasLightSources[numLightSources++] = new GPULightSource
                {
                    instanceIndex = i,
                    totalArea = totalArea,
                    triStart = triStart,
                    triCount = order.Length
                };
            }
        }

        TLASBuilder tlasBuilder = new TLASBuilder();
        tlasBuilder.Build(tlasInstances, new BvhBuildSettings
        {
            maxLeafSize = tlasMaxLeafSize,
            maxDepth = tlasMaxDepth,
            numBins = tlasNumBins
        });

        if (tlasNodesCache.Length != tlasBuilder.Nodes.Length)
            tlasNodesCache = new GPUBVHNode[tlasBuilder.Nodes.Length];

        for (int i = 0; i < tlasBuilder.Nodes.Length; i++)
        {
            BvhNode n = tlasBuilder.Nodes[i];
            tlasNodesCache[i] = new GPUBVHNode
            {
                left = n.leftChild,
                right = n.rightChild,
                firstIndex = (uint)n.start,
                count = (uint)n.count,
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

        if (lightTriDataCache.Count == 0) lightTriDataCache.Add(new GPULightTriangleData());

        ShaderHelper.CreateStructuredBuffer<GPULightSource>(ref LightSourceBuffer, Mathf.Max(1, numLightSources));
        if (numLightSources > 0) LightSourceBuffer.SetData(tlasLightSources, 0, 0, numLightSources);

        ShaderHelper.UploadStructuredBuffer(ref TLASBuffer, tlasNodesCache);
        ShaderHelper.UploadStructuredBuffer(ref TLASRefBuffer, tlasRefsCache);
        ShaderHelper.UploadStructuredBuffer(ref InstanceBuffer, tlasGpuInstances);
        ShaderHelper.UploadStructuredBuffer(ref LightTriangleIndicesBuffer, lightTriIndicesCache);
        ShaderHelper.UploadStructuredBuffer(ref LightTrianglesDataBuffer, lightTriDataCache);

        if (linearMarchCompute != null)
        {
            linearMarchCompute.SetBuffer(0, "TLASNodes", TLASBuffer);
            linearMarchCompute.SetBuffer(0, "TLASRefs", TLASRefBuffer);
            linearMarchCompute.SetBuffer(0, "Instances", InstanceBuffer);
            linearMarchCompute.SetBuffer(0, "LightSources", LightSourceBuffer);
            linearMarchCompute.SetBuffer(0, "LightTriangleIndices", LightTriangleIndicesBuffer);
            linearMarchCompute.SetBuffer(0, "LightTrianglesData", LightTrianglesDataBuffer);
            linearMarchCompute.SetInt("numMeshes", tlasGpuInstances.Length);
            linearMarchCompute.SetInt("numTLASNodes", tlasNodesCache.Length);
            linearMarchCompute.SetInt("numInstances", tlasGpuInstances.Length);
            linearMarchCompute.SetInt("TLASRootIndex", tlasBuilder.RootIndex);
            linearMarchCompute.SetInt("numLightSources", numLightSources);
            linearMarchCompute.SetInt("BVHTestsSaturation", BVHNodeTestSaturationValue);
            linearMarchCompute.SetInt("triTestsSaturation", triTestFullSaturationValue);
            linearMarchCompute.SetInt("TLASNodeVisitsSaturation", TLASNodeVisitsSaturationValue);
            linearMarchCompute.SetInt("BLASNodeVisitsSaturation", BLASNodeVisitsSaturationValue);
            linearMarchCompute.SetInt("InstanceBLASTraversalsSaturation", InstanceBLASTraversalsSaturationValue);
            linearMarchCompute.SetInt("TLASLeafRefsVisitedSaturation", TLASLeafRefsSaturationValue);
            linearMarchCompute.SetInt("u_StepsPerCollisionTest", StepsPerCollisionTest);
        }
        if (geodiscMarchCompute != null)
        {
            geodiscMarchCompute.SetBuffer(0, "Instances", InstanceBuffer);
            geodiscMarchCompute.SetBuffer(0, "TLASNodes", TLASBuffer);
            geodiscMarchCompute.SetBuffer(0, "TLASRefs", TLASRefBuffer);
            geodiscMarchCompute.SetBuffer(0, "BVHNodes", BVHBuffer);
            geodiscMarchCompute.SetBuffer(0, "Triangles", TriangleBuffer);
            geodiscMarchCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
            geodiscMarchCompute.SetBuffer(0, "Vertices", MeshVerticesBuffer);
            geodiscMarchCompute.SetBuffer(0, "Normals", MeshNormalsBuffer);

            geodiscMarchCompute.SetInt("numMeshes", tlasGpuInstances.Length);
            geodiscMarchCompute.SetInt("numTLASNodes", tlasNodesCache.Length);
            geodiscMarchCompute.SetInt("numInstances", tlasGpuInstances.Length);
            geodiscMarchCompute.SetInt("TLASRootIndex", tlasBuilder.RootIndex);
        }

        if (reflectionCompute != null)
        {
            reflectionCompute.SetBuffer(0, "Instances", InstanceBuffer);
            reflectionCompute.SetBuffer(0, "TLASNodes", TLASBuffer);
            reflectionCompute.SetBuffer(0, "TLASRefs", TLASRefBuffer);
        }

        if (flagVisualizerMaterial != null)
            flagVisualizerMaterial.SetBuffer("Instances", InstanceBuffer);

        buffersHaveRealData = true;
    }

    void ApplyBlackHoleLUT(ComputeShader cs, RayTracedBlackHole blackHole)
    {
        cs.SetFloat("bendStrength", bendStrength);
        cs.SetFloat("strongFieldCurvatureRadPetMeterCutoff", strongFieldRadPerMeterCuttoff);
    }

    void CPUSortActiveRays(int totalPixels)
    {
        uint[] countArr = new uint[1];
        activeRayCountBuffer.GetData(countArr);
        uint activeCount = countArr[0];
        if (activeCount == 0) return;

        uint[] indices = new uint[activeCount];
        MainRay[] rays = new MainRay[totalPixels];
        activeRayIndicesBuffer.GetData(indices, 0, 0, (int)activeCount);
        mainRayBuffer.GetData(rays);

        uint[] keys = new uint[activeCount];
        for (int i = 0; i < activeCount; i++)
            keys[i] = DirectionToSortKey(rays[indices[i]].rayDirection);

        Array.Sort(keys, indices);
        activeRayIndicesBuffer.SetData(indices, 0, 0, (int)activeCount);
    }

    uint DirectionToSortKey(Vector3 dir)
    {
        dir.Normalize();
        float l1 = Mathf.Abs(dir.x) + Mathf.Abs(dir.y) + Mathf.Abs(dir.z);
        float ox = dir.x / l1;
        float oy = dir.y / l1;
        if (dir.z < 0)
        {
            float sx = ox >= 0 ? 1f : -1f;
            float sy = oy >= 0 ? 1f : -1f;
            ox = (1f - Mathf.Abs(oy)) * sx;
            oy = (1f - Mathf.Abs(ox)) * sy;
        }
        uint x = (uint)Mathf.Clamp((ox * 0.5f + 0.5f) * 1023f, 0, 1023);
        uint y = (uint)Mathf.Clamp((oy * 0.5f + 0.5f) * 1023f, 0, 1023);
        return (x << 10) | y;
    }

    void OnRenderImage(RenderTexture source, RenderTexture target)
    {
        if (Camera.current.name == "SceneCamera")
        {
            Graphics.Blit(source, target);
            return;
        }

        InitFrame();

        Camera cam = GetComponent<Camera>();
        if (ShouldResetAccumulation(cam))
        {
            numRenderedFrames = 0;
            baseSeed = UnityEngine.Random.Range(0, int.MaxValue);
        }

        if (!accumulateInGameView)
            numRenderedFrames = 0;

        scaledW = Mathf.Max(1, Mathf.RoundToInt(source.width * renderScale));
        scaledH = Mathf.Max(1, Mathf.RoundToInt(source.height * renderScale));
        int pixelCount = scaledW * scaledH;

        ShaderHelper.CreateRenderTexture(ref resultTexture, scaledW, scaledH,
            FilterMode.Bilinear, ShaderHelper.RGBA_SFloat, "Result");

        RenderTexture scaledFrame  = RenderTexture.GetTemporary(scaledW, scaledH, 0, ShaderHelper.RGBA_SFloat);
        RenderTexture scaledTemp   = RenderTexture.GetTemporary(scaledW, scaledH, 0, ShaderHelper.RGBA_SFloat);
        RenderTexture currentFrame = RenderTexture.GetTemporary(source.width, source.height, 0, ShaderHelper.RGBA_SFloat);
        RenderTexture tempBuffer   = RenderTexture.GetTemporary(source.width, source.height, 0, ShaderHelper.RGBA_SFloat);

        try
        {
            initCompute.SetInt("_ScreenWidth", scaledW);
            initCompute.SetInt("_ScreenHeight", scaledH);
            initCompute.SetInt("numRenderedFrames", numRenderedFrames);

            classifyCompute.SetInt("_ScreenWidth", scaledW);
            classifyCompute.SetInt("_ScreenHeight", scaledH);

            linearMarchCompute.SetInt("_ScreenWidth", scaledW);
            linearMarchCompute.SetInt("_ScreenHeight", scaledH);
            linearMarchCompute.SetFloat("renderDistance", renderDistance);

            if (geodiscMarchCompute != null)
            {
                geodiscMarchCompute.SetInt("_ScreenWidth", scaledW);
                geodiscMarchCompute.SetInt("_ScreenHeight", scaledH);
                geodiscMarchCompute.SetInt("emergencyBreakMaxSteps", emergencyBreakMaxSteps);
                geodiscMarchCompute.SetFloat("stepSize", blackHoleSOIStepSize);
            }

            if (linearMarchRaytraceShader != null)
                linearMarchRaytraceShader.SetFloat("renderDistance", renderDistance);

            reflectionCompute.SetInt("_ScreenWidth", scaledW);
            reflectionCompute.SetInt("_ScreenHeight", scaledH);

            for (int ray = 0; ray < raysPerPixel; ray++)
            {
                initCompute.SetInt("currentRayNum", ray);
                DispatchCompute(initCompute, pixelCount, "Init");

                for (int bounce = 0; bounce < maxBounces; bounce++)
                {
                    // Reset per-bounce producer queues.
                    activeRayCountBuffer.SetData(zeroOne, 0, LINEAR_RAY_QUEUEA, 1);
                    activeRayCountBuffer.SetData(zeroOne, 0, GEODISC_RAY_QUEUEA, 1);
                    activeRayCountBuffer.SetData(zeroOne, 0, LINEAR_RAY_QUEUEB, 1);
                    activeRayCountBuffer.SetData(zeroOne, 0, GEODISC_RAY_QUEUEB, 1);
                    activeRayCountBuffer.SetData(zeroOne, 0, REFLECTION_QUEUE, 1);
                    activeRayCountBuffer.SetData(zeroOne, 0, SKYBOX_QUEUE, 1);

                    // Seed A queues from the current active rays.
                    DispatchWavefront(classifyCompute, ACTIVE_RAY_QUEUE, "Classify");
                    activeRayCountBuffer.SetData(zeroOne, 0, ACTIVE_RAY_QUEUE, 1);

                    bool useAAsInput = true;

                    for (int iter = 0; iter < maxLinearGeodiscPingPongIterations; iter++)
                    {
                        ComputeBuffer linearInput   = useAAsInput ? linearMarchQueueBufferA   : linearMarchQueueBufferB;
                        ComputeBuffer linearOutput  = useAAsInput ? linearMarchQueueBufferB   : linearMarchQueueBufferA;
                        ComputeBuffer geodiscInput  = useAAsInput ? geodiscMarchQueueBufferA  : geodiscMarchQueueBufferB;
                        ComputeBuffer geodiscOutput = useAAsInput ? geodiscMarchQueueBufferB  : geodiscMarchQueueBufferA;

                        int linearInputQueueIndex   = useAAsInput ? LINEAR_RAY_QUEUEA   : LINEAR_RAY_QUEUEB;
                        int linearOutputQueueIndex  = useAAsInput ? LINEAR_RAY_QUEUEB   : LINEAR_RAY_QUEUEA;
                        int geodiscInputQueueIndex  = useAAsInput ? GEODISC_RAY_QUEUEA  : GEODISC_RAY_QUEUEB;
                        int geodiscOutputQueueIndex = useAAsInput ? GEODISC_RAY_QUEUEB  : GEODISC_RAY_QUEUEA;

                        // Clear the output side of this iteration only.
                        activeRayCountBuffer.SetData(zeroOne, 0, linearOutputQueueIndex, 1);
                        activeRayCountBuffer.SetData(zeroOne, 0, geodiscOutputQueueIndex, 1);

                        SetLinearMarchQueueBindings(
                            linearInput,
                            geodiscOutput,
                            linearInputQueueIndex,
                            geodiscOutputQueueIndex);

                        SetGeodiscMarchQueueBindings(
                            geodiscInput,
                            linearOutput,
                            geodiscInputQueueIndex,
                            linearOutputQueueIndex);

                        DispatchWavefront(linearMarchCompute,  linearInputQueueIndex,  $"LinearMarch_{iter}");
                        DispatchWavefront(geodiscMarchCompute, geodiscInputQueueIndex, $"GeodiscMarch_{iter}");

                        useAAsInput = !useAAsInput;
                    }

                    DispatchWavefront(reflectionCompute, REFLECTION_QUEUE, "Reflection");
                }
            }

            activeRayCountBuffer.SetData(zerosNUM_QUEUES);

            accumulateCompute.SetInt("_ScreenWidth", scaledW);
            accumulateCompute.SetInt("_ScreenHeight", scaledH);
            accumulateCompute.SetInt("raysPerPixel", raysPerPixel);
            accumulateCompute.SetBuffer(0, "pixelAccum", pixelAccumBuffer);
            accumulateCompute.SetTexture(0, "_Output", resultTexture);
            DispatchCompute(accumulateCompute, pixelCount);

            if (accumulatorMaterial != null)
            {
                accumulatorMaterial.SetInt("numRenderedFrames", numRenderedFrames);
                accumulatorMaterial.SetTexture("_MainTexOld", cleanAccumBuffer);
                accumulatorMaterial.SetFloat("accumWeight", accumWeight);
                Graphics.Blit(resultTexture, scaledTemp, accumulatorMaterial);
                Swap(ref scaledTemp, ref scaledFrame);
                Graphics.Blit(scaledFrame, cleanAccumBuffer);
            }
            else
            {
                Graphics.Blit(resultTexture, scaledFrame);
            }

            if (atrousFilter && atrousMaterial != null && atrousBeforeUpscale)
            {
                foreach (int step in atrousStepSizes)
                {
                    atrousMaterial.SetInt("stepSize", step);
                    atrousMaterial.SetFloat("colorSigma", atrousColorSigma);
                    Graphics.Blit(scaledFrame, scaledTemp, atrousMaterial);
                    Swap(ref scaledFrame, ref scaledTemp);
                }
            }

            if (ditherPostProcess && ditherBeforeUpscale && ditherMaterial != null)
            {
                ditherMaterial.SetInt("matrixSize", ditherMatrixSize);
                Graphics.Blit(scaledFrame, scaledTemp, ditherMaterial);
                Swap(ref scaledFrame, ref scaledTemp);
            }

            scaledFrame.filterMode = FilterMode.Point;
            Graphics.Blit(scaledFrame, currentFrame);

            if (atrousFilter && atrousMaterial != null && !atrousBeforeUpscale)
            {
                foreach (int step in atrousStepSizes)
                {
                    atrousMaterial.SetInt("stepSize", step);
                    atrousMaterial.SetFloat("colorSigma", atrousColorSigma);
                    Graphics.Blit(currentFrame, tempBuffer, atrousMaterial);
                    Swap(ref currentFrame, ref tempBuffer);
                }
            }

            if (ditherPostProcess && !ditherBeforeUpscale && ditherMaterial != null)
            {
                ditherMaterial.SetInt("matrixSize", ditherMatrixSize);
                Graphics.Blit(currentFrame, tempBuffer, ditherMaterial);
                Swap(ref currentFrame, ref tempBuffer);
            }

            if (colorQuantization && colorQuantizationMaterial != null)
            {
                colorQuantizationMaterial.SetInt("numColors", numColors);
                Graphics.Blit(currentFrame, tempBuffer, colorQuantizationMaterial);
                Swap(ref currentFrame, ref tempBuffer);
            }

            Graphics.Blit(currentFrame, target);

            numRenderedFrames++;
            if (numRenderedFrames % 100 == 0)
                Debug.Log("Num Rendered Frames: " + numRenderedFrames);
        }
        catch (Exception e)
        {
            Debug.LogError("OnRenderImage error: " + e);
            Graphics.Blit(source, target);
        }
        finally
        {
            RenderTexture.ReleaseTemporary(scaledFrame);
            RenderTexture.ReleaseTemporary(scaledTemp);
            RenderTexture.ReleaseTemporary(currentFrame);
            RenderTexture.ReleaseTemporary(tempBuffer);
        }
    }

    void InitFrame()
    {
        if (!startupDone)
        {
            EnsureMaterialsCreated();
            EnsureBuffersCreated();
            BindBuffersToShaders();
            startupDone = true;
        }

        List<RayTracedMesh> meshObjects = RayTracedMesh.All;
        if (AnyTransformDirty(meshObjects)) tlasDirty = true;

        EnsureMaterialsCreated();
        EnsureBuffersCreated();

        if (!Mathf.Approximately(renderScale, lastRenderScale))
        {
            if (cleanAccumBuffer != null)
            {
                cleanAccumBuffer.Release();
                cleanAccumBuffer = null;
            }
            lastRenderScale = renderScale;
            numRenderedFrames = 0;
        }

        int sw = Mathf.Max(1, Mathf.RoundToInt(Screen.width * renderScale));
        int sh = Mathf.Max(1, Mathf.RoundToInt(Screen.height * renderScale));

        ShaderHelper.CreateRenderTexture(ref cleanAccumBuffer, sw, sh,
            FilterMode.Bilinear, ShaderHelper.RGBA_SFloat, "cleanAccumBuffer");

        AllocateAccelerationBuffers();
        BindBuffersToShaders();
        allocateBlackHoleBuffer();
        UpdateAtmosphereParams();
        UpdateCameraParams(Camera.current);
        UpdateRayTracingParams();
    }

    void UpdateCameraParams(Camera camera)
    {
        float planeHeight = camera.nearClipPlane * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 2f;
        float planeWidth = planeHeight * camera.aspect;

        if (initCompute != null)
        {
            initCompute.SetVector("ViewParams", new Vector3(planeWidth, planeHeight, camera.nearClipPlane));
            initCompute.SetMatrix("CameraLocalToWorldMatrix", camera.transform.localToWorldMatrix);
            initCompute.SetVector("CameraWorldPos", camera.transform.position);
            initCompute.SetInt("numRenderedFrames", numRenderedFrames);
        }

        if (blackHoleSOIStepSize == 0) blackHoleSOIStepSize = 1.0f;
    }

    void UpdateRayTracingParams()
    {
        if (linearMarchCompute != null)
        {
            if (useTlas) linearMarchCompute.EnableKeyword("USE_TLAS");
            else linearMarchCompute.DisableKeyword("USE_TLAS");
        }

        if (geodiscMarchCompute != null)
        {
            if (useTlas) geodiscMarchCompute.EnableKeyword("USE_TLAS");
            else geodiscMarchCompute.DisableKeyword("USE_TLAS");
        }
    }

    void allocateBlackHoleBuffer()
    {
        if (blackHolesDirty)
        {
            cachedBlackHoleObjects = FindObjectsOfType<RayTracedBlackHole>();

            if (cachedBlackHoles.Length != cachedBlackHoleObjects.Length)
                cachedBlackHoles = new BlackHole[cachedBlackHoleObjects.Length];

            for (int i = 0; i < cachedBlackHoleObjects.Length; i++)
            {
                cachedBlackHoles[i] = new BlackHole
                {
                    position = cachedBlackHoleObjects[i].transform.position,
                    radius = cachedBlackHoleObjects[i].transform.localScale.x * 0.5f,
                    blackHoleSOIMultiplier = cachedBlackHoleObjects[i].blackHoleSOIMultiplier,
                };

                if (cachedBlackHoles[i].blackHoleSOIMultiplier <= 0)
                {
                    Debug.LogError("BlackHoleSOIMultiplier is <= 0 for " + cachedBlackHoleObjects[i].name);
                    cachedBlackHoles[i].blackHoleSOIMultiplier = 1.0f;
                }
            }

            blackHolesDirty = false;
        }
        else
        {
            for (int i = 0; i < cachedBlackHoleObjects.Length; i++)
            {
                cachedBlackHoles[i].position = cachedBlackHoleObjects[i].transform.position;
                cachedBlackHoles[i].radius = cachedBlackHoleObjects[i].transform.localScale.x * 0.5f;
            }
        }

        if (cachedBlackHoleObjects.Length > 0 && classifyCompute != null)
            ApplyBlackHoleLUT(classifyCompute, cachedBlackHoleObjects[0]);

        ShaderHelper.UploadStructuredBuffer(ref blackHoleBuffer, cachedBlackHoles);

        if (classifyCompute != null)
        {
            classifyCompute.SetBuffer(0, "blackholes", blackHoleBuffer);
            classifyCompute.SetInt("num_black_holes", cachedBlackHoleObjects.Length);
        }

        if (linearMarchCompute != null)
        {
            linearMarchCompute.SetBuffer(0, "blackholes", blackHoleBuffer);
            linearMarchCompute.SetInt("num_black_holes", cachedBlackHoleObjects.Length);
        }

        if (geodiscMarchCompute != null)
        {
            geodiscMarchCompute.SetBuffer(0, "blackholes", blackHoleBuffer);
            geodiscMarchCompute.SetInt("num_black_holes", cachedBlackHoleObjects.Length);
        }

        if (SystemInfo.supportsRayTracing && !forceSoftwareRaytracing && linearMarchRaytraceShader != null)
        {
            linearMarchRaytraceShader.SetBuffer("blackholes", blackHoleBuffer);
            linearMarchRaytraceShader.SetInt("num_black_holes", cachedBlackHoleObjects.Length);
        }
    }
}