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
    const uint FLAG_NEEDS_CLASSIFY = (1u << 5);
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
    public ComputeShader reflectionCompute;
    public ComputeShader accumulateCompute;
    public ComputeShader compactCompute;
    public ComputeShader writeIndirectArgsCompute;
    public ComputeShader resetCountCompute;
    
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
    [NonSerialized] private ComputeBuffer linearMarchQueueB;
    [NonSerialized] private ComputeBuffer geodiscMarchQueueA;
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
    private static readonly uint[] zeroOne = new uint[]{ 0 };
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

    struct Control       { private uint flags; private uint rngState; }
    struct MainRay       { public Vector3 rayOrigin; public Vector3 rayDirection; }
    struct RayColorInfo  { private float3 rayColor; private float3 incomingLight; }
    struct Energy        { private float energy; }

    struct MeshOffsets
    {
        public int vertexOffset;
        public int triangleOffset;
        public int blasNodeOffset;
        public int rootNodeIndex;
    }

    struct HitInfo
    {
        private uint   didHit;
        private float  distance;
        private float  u;
        private float  v;
        private uint   triIndex;
        private int    objectIndex;
        private Vector3 worldNormal;
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
            posDelta > 0.0005f || rotDelta > 0.05f || fovDelta > 0.01f ||
            Screen.width != lastScreenWidth || Screen.height != lastScreenHeight;

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
    void Start()
    {
        Startup();
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
        Startup();
    }

    void Startup()
    {
        ReleaseBuffers();
        tlasDirty = true;
        buffersHaveRealData = false;
        lastInstanceCount = -1;
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
    sphereBuffer?.Release();                 sphereBuffer = null;
    MeshVerticesBuffer?.Release();           MeshVerticesBuffer = null;
    MeshNormalsBuffer?.Release();            MeshNormalsBuffer = null;
    MeshIndicesBuffer?.Release();            MeshIndicesBuffer = null;
    TriangleBuffer?.Release();               TriangleBuffer = null;
    BVHBuffer?.Release();                    BVHBuffer = null;
    TLASBuffer?.Release();                   TLASBuffer = null;
    TLASRefBuffer?.Release();                TLASRefBuffer = null;
    InstanceBuffer?.Release();               InstanceBuffer = null;
    LightSourceBuffer?.Release();            LightSourceBuffer = null;
    LightTriangleIndicesBuffer?.Release();   LightTriangleIndicesBuffer = null;
    LightTrianglesDataBuffer?.Release();     LightTrianglesDataBuffer = null;
    controlQueue?.Release();                 controlQueue = null;
    mainRayBuffer?.Release();                mainRayBuffer = null;
    blackHoleBuffer?.Release();              blackHoleBuffer = null;
    HitInfoBuffer?.Release();                HitInfoBuffer = null;
    rayColorInfoBuffer?.Release();           rayColorInfoBuffer = null;
    pixelAccumBuffer?.Release();             pixelAccumBuffer = null;
    activeRayIndicesBuffer?.Release();       activeRayIndicesBuffer = null;
    activeRayCountBuffer?.Release();         activeRayCountBuffer = null;
    indirectArgsBuffer?.Release();           indirectArgsBuffer = null;
    linearMarchQueueBufferA?.Release();      linearMarchQueueBufferA = null;
    linearMarchQueueB?.Release();            linearMarchQueueB = null;
    geodiscMarchQueueA?.Release();           geodiscMarchQueueA = null;
    geodiscMarchQueueBufferB?.Release();     geodiscMarchQueueBufferB = null;
    reflectionQueueBuffer?.Release();        reflectionQueueBuffer = null;
    skyboxQueueBuffer?.Release();            skyboxQueueBuffer = null;
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

        if (TriangleBuffer == null)             ShaderHelper.CreateStructuredBuffer<Triangle>(ref TriangleBuffer, 1);
        if (BVHBuffer == null)                  ShaderHelper.CreateStructuredBuffer<GPUBVHNode>(ref BVHBuffer, 1);
        if (TLASBuffer == null)                 ShaderHelper.CreateStructuredBuffer<GPUBVHNode>(ref TLASBuffer, 1);
        if (TLASRefBuffer == null)              ShaderHelper.CreateStructuredBuffer<uint>(ref TLASRefBuffer, 1);
        if (InstanceBuffer == null)             ShaderHelper.CreateStructuredBuffer<MeshStruct>(ref InstanceBuffer, 1);
        if (MeshVerticesBuffer == null)         ShaderHelper.CreateStructuredBuffer<Vector3>(ref MeshVerticesBuffer, 1);
        if (MeshNormalsBuffer == null)          ShaderHelper.CreateStructuredBuffer<Vector3>(ref MeshNormalsBuffer, 1);
        if (MeshIndicesBuffer == null)          ShaderHelper.CreateStructuredBuffer<uint>(ref MeshIndicesBuffer, 1);
        if (LightSourceBuffer == null)          ShaderHelper.CreateStructuredBuffer<GPULightSource>(ref LightSourceBuffer, 1);
        if (LightTriangleIndicesBuffer == null) ShaderHelper.CreateStructuredBuffer<int>(ref LightTriangleIndicesBuffer, 1);
        if (LightTrianglesDataBuffer == null)   ShaderHelper.CreateStructuredBuffer<GPULightTriangleData>(ref LightTrianglesDataBuffer, 1);
        if (sphereBuffer == null)               ShaderHelper.CreateStructuredBuffer<Sphere>(ref sphereBuffer, 1);
        if (controlQueue == null)              ShaderHelper.CreateStructuredBuffer<Control>(ref controlQueue, pixelCount);
        if (mainRayBuffer == null)              ShaderHelper.CreateStructuredBuffer<MainRay>(ref mainRayBuffer, pixelCount);
        if (blackHoleBuffer == null)            ShaderHelper.CreateStructuredBuffer<BlackHole>(ref blackHoleBuffer, 1);
        if (HitInfoBuffer == null)              ShaderHelper.CreateStructuredBuffer<HitInfo>(ref HitInfoBuffer, pixelCount);
        if (rayColorInfoBuffer == null)         ShaderHelper.CreateStructuredBuffer<RayColorInfo>(ref rayColorInfoBuffer, pixelCount);
        if (pixelAccumBuffer == null)           ShaderHelper.CreateStructuredBuffer<Vector3>(ref pixelAccumBuffer, pixelCount);
        if (linearMarchQueueBufferA == null)           ShaderHelper.CreateStructuredBuffer<int>(ref linearMarchQueueBufferA, pixelCount);
        if (linearMarchQueueB == null)           ShaderHelper.CreateStructuredBuffer<int>(ref linearMarchQueueB, pixelCount);
        if (geodiscMarchQueueA == null)           ShaderHelper.CreateStructuredBuffer<int>(ref geodiscMarchQueueA, pixelCount);
        if (geodiscMarchQueueBufferB == null)           ShaderHelper.CreateStructuredBuffer<int>(ref geodiscMarchQueueBufferB, pixelCount);
        if (reflectionQueueBuffer == null)           ShaderHelper.CreateStructuredBuffer<int>(ref reflectionQueueBuffer, pixelCount);
        if (skyboxQueueBuffer == null)           ShaderHelper.CreateStructuredBuffer<int>(ref skyboxQueueBuffer, pixelCount);
        
        ShaderHelper.CreateStructuredBuffer<uint>(ref activeRayIndicesBuffer, pixelCount);
        ShaderHelper.CreateStructuredBuffer<uint>(ref activeRayCountBuffer, NUM_QUEUES); //TODO: fix me

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
        if (initCompute == null || classifyCompute == null || linearMarchCompute == null ||
            reflectionCompute == null || accumulateCompute == null || flagVisualizerMaterial == null) return;

        BindInitBuffers();
        BindClassifyBuffers();
        BindLinearMarchBuffers();
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
            compactCompute.SetBuffer(0, "controls",         controlQueue);
            compactCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
            compactCompute.SetBuffer(0, "activeRayCount",   activeRayCountBuffer);
        }
        if (writeIndirectArgsCompute != null)
        {
            writeIndirectArgsCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
            writeIndirectArgsCompute.SetBuffer(0, "indirectArgs",   indirectArgsBuffer);
        }
        if (resetCountCompute != null)
        {
            resetCountCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        }
        if (linearMarchCompute != null)
            linearMarchCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
        if (reflectionCompute != null)
            reflectionCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
        if (classifyCompute != null)
            classifyCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
    }

    void BindInitBuffers()
    {
        if (initCompute == null) return;
        initCompute.SetBuffer(0, "controls",        controlQueue);
        initCompute.SetBuffer(0, "main_rays",       mainRayBuffer);
        initCompute.SetBuffer(0, "ray_color_info",  rayColorInfoBuffer);
        initCompute.SetBuffer(0, "hit_info_buffer", HitInfoBuffer);
        initCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
        initCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        initCompute.SetBuffer(0, "pixelAccum", pixelAccumBuffer);
    }

    void BindClassifyBuffers()
    {
        if (classifyCompute == null) return;
        classifyCompute.SetBuffer(0, "controls",   controlQueue);
        classifyCompute.SetBuffer(0, "main_rays",  mainRayBuffer);
        classifyCompute.SetBuffer(0, "blackholes", blackHoleBuffer);
        classifyCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        classifyCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
        classifyCompute.SetBuffer(0, "linearMarchQueue", linearMarchQueueBufferA);
        classifyCompute.SetBuffer(0, "geodiscMarchQueue", geodiscMarchQueueA);
    }

    void BindLinearMarchBuffers()
    {
        if (linearMarchCompute == null) return;
        linearMarchCompute.SetBuffer(0,"controls",        controlQueue);
        linearMarchCompute.SetBuffer(0, "hit_infos",       HitInfoBuffer);
        linearMarchCompute.SetBuffer(0,"main_rays",       mainRayBuffer);
        linearMarchCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        linearMarchCompute.SetBuffer(0, "linearMarchQueue", linearMarchQueueBufferA);
        linearMarchCompute.SetBuffer(0, "geodiscMarchQueue", geodiscMarchQueueBufferB);
        linearMarchCompute.SetBuffer(0, "reflectionQueue", reflectionQueueBuffer);
        linearMarchCompute.SetBuffer(0, "skyboxQueue", skyboxQueueBuffer);
        linearMarchCompute.SetBuffer(0, "pixelAccum", pixelAccumBuffer);
        linearMarchCompute.SetBuffer(0,"Instances",       InstanceBuffer);
        linearMarchCompute.SetBuffer(0,"Normals",         MeshNormalsBuffer);
        linearMarchCompute.SetBuffer(0,"TriangleIndices", MeshIndicesBuffer);
        linearMarchCompute.SetBuffer(0, "blackholes",      blackHoleBuffer);
        linearMarchCompute.SetBuffer(0, "Triangles",       TriangleBuffer);
        linearMarchCompute.SetBuffer(0, "BVHNodes",        BVHBuffer);
        linearMarchCompute.SetBuffer(0, "TLASNodes",       TLASBuffer);
        linearMarchCompute.SetBuffer(0, "TLASRefs",        TLASRefBuffer);
        linearMarchCompute.SetBuffer(0, "Instances",       InstanceBuffer);
        linearMarchCompute.SetBuffer(0, "Vertices",        MeshVerticesBuffer);
        linearMarchCompute.SetBuffer(0, "Normals",         MeshNormalsBuffer);
        linearMarchCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
        
        linearMarchRaytraceShader.SetBuffer("controls",        controlQueue);
        linearMarchRaytraceShader.SetBuffer("main_rays",       mainRayBuffer);
        linearMarchRaytraceShader.SetBuffer("hit_info_buffer", HitInfoBuffer);
        linearMarchRaytraceShader.SetBuffer("Instances",       InstanceBuffer);
        linearMarchRaytraceShader.SetBuffer("Normals",         MeshNormalsBuffer);
        linearMarchRaytraceShader.SetBuffer("TriangleIndices", MeshIndicesBuffer);
    }

    void BindReflectionBuffers()
    {
        if (reflectionCompute == null) return;
        reflectionCompute.SetBuffer(0, "controls",        controlQueue);
        reflectionCompute.SetBuffer(0, "main_rays",       mainRayBuffer);
        reflectionCompute.SetBuffer(0, "hit_info_buffer", HitInfoBuffer);
        reflectionCompute.SetBuffer(0, "ray_color_info",  rayColorInfoBuffer);
        reflectionCompute.SetBuffer(0, "pixelAccum",      pixelAccumBuffer);
        reflectionCompute.SetBuffer(0, "reflectionQueue", reflectionQueueBuffer);
        reflectionCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
        reflectionCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        
        reflectionCompute.SetBuffer(0, "Instances",       InstanceBuffer);
        reflectionCompute.SetBuffer(0, "Triangles",       TriangleBuffer);
        reflectionCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
        reflectionCompute.SetBuffer(0, "Normals",         MeshNormalsBuffer);
        reflectionCompute.SetBuffer(0, "BVHNodes",        BVHBuffer);
        reflectionCompute.SetBuffer(0, "TLASNodes",       TLASBuffer);
        reflectionCompute.SetBuffer(0, "TLASRefs",        TLASRefBuffer);
    }

    void BindAccumulateBuffers()
    {
        if (accumulateCompute == null) return;
        accumulateCompute.SetBuffer(0, "pixelAccum", pixelAccumBuffer);
    }

    void BindFlagVisualizerBuffers()
    {
        if (flagVisualizerMaterial == null) return;
        flagVisualizerMaterial.SetBuffer("controls",        controlQueue);
        flagVisualizerMaterial.SetBuffer("hit_info_buffer", HitInfoBuffer);
        flagVisualizerMaterial.SetBuffer("Instances",       InstanceBuffer);
    }

    void BindIndirectArgs()
    {
        writeIndirectArgsCompute.SetInt("NUM_QUEUES", NUM_QUEUES);
        writeIndirectArgsCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        writeIndirectArgsCompute.SetBuffer(0, "indirectArgs", indirectArgsBuffer);
    }
    void DispatchCompute(ComputeShader cs, int pixelCount, string label = "")
    {
        UnityEngine.Profiling.Profiler.BeginSample(label == "" ? cs.name : label);
        cs.Dispatch(0, Mathf.CeilToInt(pixelCount / 64f), 1, 1);
       UnityEngine.Profiling.Profiler.EndSample();
    }
    void ResetActiveCount()
    {
        resetCountCompute.Dispatch(0, 1, 1, 1);
    }

    void DispatchCompact(uint flagBit, int totalPixels)
    {
        compactCompute.SetInt("filterFlag",  (int)flagBit);
        compactCompute.SetInt("totalPixels", totalPixels);
        compactCompute.Dispatch(0, Mathf.CeilToInt(totalPixels / 64f), 1, 1);
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

    private int[] accelStructureInstanceIDs;
    private RayTracedMesh[] lastBuiltMeshOrder;

    // Builds vertex/normal/index/triangle buffers in ORIGINAL mesh order to match
    // hardware RT's PrimitiveIndex(), which also returns original mesh triangle order.
    // Populates gpuInstances[i].triangleOffset = sum of raw tri counts of prior meshes.
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

        List<float3> vertices        = new(totalVerts);
        List<float3> normals         = new(totalVerts);
        Triangle[]   triangles       = new Triangle[Mathf.Max(1, totalTris)];
        uint[]       triangleIndices = new uint[Mathf.Max(1, totalTris * 3)];

        int vertexOffset   = 0;
        int triangleOffset = 0;

        for (int i = 0; i < meshes.Count; i++)
        {
            MeshFilter filter = meshes[i].GetComponent<MeshFilter>();
            if (filter == null) continue;
            Mesh mesh = filter.sharedMesh;

            mesh.GetVertices(tV);
            mesh.GetNormals(tN);

            for (int v = 0; v < tV.Count; v++) vertices.Add(tV[v]);
            if (tN.Count == tV.Count)
                for (int n = 0; n < tN.Count; n++) normals.Add(tN[n]);
            else
                for (int n = 0; n < tV.Count; n++) normals.Add(Vector3.up);

            int[] meshTris    = mesh.triangles;
            int   rawTriCount = meshTris.Length / 3;

            // store triangleOffset so shader can do triangleOffset + PrimitiveIndex()
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
                    edgeAB    = (Vector3)tV[v1] - (Vector3)tV[v0],
                    edgeAC    = (Vector3)tV[v2] - (Vector3)tV[v0],
                };

                triangleIndices[triIndexBase + 0] = (uint)(vertexOffset + v0);
                triangleIndices[triIndexBase + 1] = (uint)(vertexOffset + v1);
                triangleIndices[triIndexBase + 2] = (uint)(vertexOffset + v2);
            }

            vertexOffset   += mesh.vertexCount;
            triangleOffset += rawTriCount;
            tV.Clear(); tN.Clear();
        }

        ShaderHelper.CreateStructuredBuffer(ref MeshVerticesBuffer, vertices);
        ShaderHelper.CreateStructuredBuffer(ref MeshNormalsBuffer,  normals);
        ShaderHelper.CreateStructuredBuffer(ref TriangleBuffer,     triangles);
        ShaderHelper.CreateStructuredBuffer(ref MeshIndicesBuffer,  triangleIndices);

        // bind to reflection shader so normal lookup works on hardware RT hits
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

        RayTracingAccelerationStructure.Settings settings = new RayTracingAccelerationStructure.Settings
        {
            rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything,
            managementMode    = RayTracingAccelerationStructure.ManagementMode.Manual,
            layerMask         = 255
        };

        accelStructure = new RayTracingAccelerationStructure(settings);

        lastBuiltMeshOrder = meshes.ToArray();
        accelStructureInstanceIDs = new int[meshes.Count];
        MeshStruct[] gpuInstances = new MeshStruct[meshes.Count];

        // build geometry buffers first — populates gpuInstances[i].triangleOffset
        BuildHardwareGeometryBuffers(meshes, gpuInstances);

        for (int i = 0; i < meshes.Count; i++)
        {
            RayTracedMesh m       = meshes[i];
            MeshRenderer renderer = m.GetComponent<MeshRenderer>();
            MeshFilter   filter   = m.GetComponent<MeshFilter>();
            if (renderer == null || filter == null) continue;

            RayTracingMeshInstanceConfig config = new RayTracingMeshInstanceConfig
            {
                mesh                  = filter.sharedMesh,
                subMeshIndex          = 0,
                material              = renderer.sharedMaterial,
                enableTriangleCulling = false, // off for future refraction support
            };

            accelStructureInstanceIDs[i] = accelStructure.AddInstance(
                config, m.transform.localToWorldMatrix, null, (uint)i);

            // triangleOffset already set by BuildHardwareGeometryBuffers above
            gpuInstances[i].localToWorldMatrix = m.transform.localToWorldMatrix;
            gpuInstances[i].worldToLocalMatrix = m.transform.worldToLocalMatrix;
            gpuInstances[i].material           = m.material;
            gpuInstances[i].firstBVHNodeIndex  = 0; // not used on hardware path
        }

        ShaderHelper.UploadStructuredBuffer(ref InstanceBuffer, gpuInstances);

        if (flagVisualizerMaterial != null)
            flagVisualizerMaterial.SetBuffer("Instances", InstanceBuffer);
        if (reflectionCompute != null)
            reflectionCompute.SetBuffer(0, "Instances", InstanceBuffer);

        accelStructure.Build();
    }

    void DispatchHardwareLinearMarch(int width, int height)
    {
        if (linearMarchRaytraceShader == null) { Debug.LogError("linearMarchRaytraceShader not assigned!"); return; }
        if (accelStructure == null)            { Debug.LogError("accelStructure is null!"); return; }

        linearMarchRaytraceShader.Dispatch("RayGen",           width, height, 1);
    }

    void UpdateAtmosphereParams() { }

    List<RayTracedMesh> GetValidMeshInstances()
    {
        List<RayTracedMesh> allMeshes = RayTracedMesh.All;
        List<RayTracedMesh> validMeshes = new(allMeshes.Count);

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
            validMeshes.Add(m);
        }

        return validMeshes;
    }

    List<SharedMeshData> GetUniqueSharedMeshes(List<RayTracedMesh> validInstances)
    {
        HashSet<SharedMeshData> seen   = new();
        List<SharedMeshData>    unique = new();

        for (int i = 0; i < validInstances.Count; i++)
        {
            SharedMeshData sm = validInstances[i].sharedMesh;
            if (sm == null) continue;
            if (seen.Add(sm)) unique.Add(sm);
        }

        return unique;
    }

    bool AnyInstanceNeedsUpdate(List<RayTracedMesh> v) { for (int i = 0; i < v.Count; i++) if (v[i].update) return true; return false; }
    bool AnyTransformDirty(List<RayTracedMesh> v)      { for (int i = 0; i < v.Count; i++) if (v[i].transformDirty) return true; return false; }

    void ApplyRaytracingMode() { }

    void AllocateAccelerationBuffers()
    {
        bool useHardwareRT = SystemInfo.supportsRayTracing && !forceSoftwareRaytracing;
        List<RayTracedMesh> validInstances = GetValidMeshInstances();
        ///Debug.Log($"Valid instances: {validInstances.Count}, tlasDirty: {tlasDirty}, lastInstanceCount: {lastInstanceCount}");
        // force full rebuild when switching between hardware/software
        if (forceSoftwareRaytracing != lastForceSoftware)
        {
            tlasDirty = true;
            buffersHaveRealData = false;
            lastInstanceCount = -1;
            lastForceSoftware = forceSoftwareRaytracing;
        }
        bool anyTransformDirty    = AnyTransformDirty(validInstances);
        bool anyMeshUpdated       = AnyInstanceNeedsUpdate(validInstances);
        if (useHardwareRT)
        {
            bool instanceCountChanged = validInstances.Count != lastInstanceCount;

            if (tlasDirty || anyMeshUpdated || instanceCountChanged)
            {
                // always build geometry first so offsets are ready for BuildAccelStructure
                List<SharedMeshData> uniqueSharedMeshes = GetUniqueSharedMeshes(validInstances);
                Dictionary<SharedMeshData, MeshOffsets> offsets = ComputeMeshOffsets(uniqueSharedMeshes);
                //BuildGlobalBLASGeometry(uniqueSharedMeshes, validInstances, offsets);
                BuildAccelStructure(validInstances);

                tlasDirty = false;
                lastInstanceCount = validInstances.Count;
                for (int i = 0; i < validInstances.Count; i++) validInstances[i].update = false;
            }
            else if (anyTransformDirty)
            {
                for (int i = 0; i < lastBuiltMeshOrder.Length && i < accelStructureInstanceIDs.Length; i++)
                    accelStructure.UpdateInstanceTransform(
                        accelStructureInstanceIDs[i],
                        lastBuiltMeshOrder[i].transform.localToWorldMatrix);
                accelStructure.Build();
            }

            for (int i = 0; i < validInstances.Count; i++) validInstances[i].transformDirty = false;
            linearMarchRaytraceShader.SetAccelerationStructure("_AccelStructure", accelStructure);
            return;
        }

        // software path
        if (forceBufferRecreation) { buffersHaveRealData = false; tlasDirty = true; forceBufferRecreation = false; }
        if (validInstances.Count == 0) { EnsureBuffersCreated(); return; }
        if (validInstances.Count != lastInstanceCount) { tlasDirty = true; lastInstanceCount = validInstances.Count; }
        
        if (!tlasDirty && !anyMeshUpdated && !anyTransformDirty && buffersHaveRealData) return;

        List<SharedMeshData> uniqueMeshes = GetUniqueSharedMeshes(validInstances);
        Dictionary<SharedMeshData, MeshOffsets> softOffsets = ComputeMeshOffsets(uniqueMeshes);
        BuildGlobalBLASGeometry(uniqueMeshes, validInstances, softOffsets);
        BuildAndUploadTLAS(validInstances, softOffsets);
    }

    Dictionary<SharedMeshData, MeshOffsets> ComputeMeshOffsets(List<SharedMeshData> sharedMeshes)
    {
        Dictionary<SharedMeshData, MeshOffsets> offsets = new(sharedMeshes.Count);
        int vertexOffset = 0, triangleOffset = 0, blasOffset = 0;

        for (int i = 0; i < sharedMeshes.Count; i++)
        {
            SharedMeshData mesh = sharedMeshes[i];
            offsets.Add(mesh, new MeshOffsets
            {
                vertexOffset   = vertexOffset,
                triangleOffset = triangleOffset,
                blasNodeOffset = blasOffset,
                rootNodeIndex  = blasOffset + mesh.blas.RootIndex
            });
            vertexOffset   += mesh.mesh.vertexCount;
            triangleOffset += mesh.blas.PrimitiveRefs.Length;
            blasOffset     += mesh.GPUBVH.Count;
        }

        return offsets;
    }

    void BuildGlobalBLASGeometry(
        List<SharedMeshData> sharedMeshes,
        List<RayTracedMesh>  validInstances,
        Dictionary<SharedMeshData, MeshOffsets> offsets)
    {
        int totalVertexCount = 0, totalTris = 0, totalBVHNodes = 0;

        for (int i = 0; i < sharedMeshes.Count; i++)
        {
            totalVertexCount += sharedMeshes[i].mesh.vertexCount;
            totalTris        += sharedMeshes[i].blas.PrimitiveRefs.Length;
            totalBVHNodes    += sharedMeshes[i].GPUBVH.Count;
        }

        bool anyMeshMarkedUpdated = AnyInstanceNeedsUpdate(validInstances);
        bool needTriangles = !buffersHaveRealData || TriangleBuffer == null || TriangleBuffer.count != Mathf.Max(1, totalTris)     || anyMeshMarkedUpdated;
        bool needBVH       = !buffersHaveRealData || BVHBuffer      == null || BVHBuffer.count      != Mathf.Max(1, totalBVHNodes) || anyMeshMarkedUpdated;
        bool needVertices  = !buffersHaveRealData || MeshVerticesBuffer == null || MeshNormalsBuffer == null || MeshIndicesBuffer == null || anyMeshMarkedUpdated;

        if (!(needTriangles || needBVH || needVertices)) return;

        tlasDirty = true;
        float startTime = Time.realtimeSinceStartup;

        Triangle[]   triangles       = new Triangle[Mathf.Max(1, totalTris)];
        uint[]       triangleIndices = new uint[Mathf.Max(1, totalTris * 3)];
        GPUBVHNode[] blasNodes       = new GPUBVHNode[Mathf.Max(1, totalBVHNodes)];
        List<float3> vertices        = new(totalVertexCount);
        List<float3> normals         = new(totalVertexCount);
        int degenerateTriangles = 0;

        for (int i = 0; i < sharedMeshes.Count; i++)
        {
            SharedMeshData sharedMesh = sharedMeshes[i];
            MeshOffsets    off        = offsets[sharedMesh];

            sharedMesh.mesh.GetVertices(tV);
            sharedMesh.mesh.GetNormals(tN);

            for (int v = 0; v < tV.Count; v++) vertices.Add(tV[v]);
            if (tN.Count == tV.Count)
                for (int n = 0; n < tN.Count; n++) normals.Add(tN[n]);
            else
                for (int n = 0; n < tV.Count; n++) normals.Add(Vector3.up);

            int[] meshTriangles = sharedMesh.mesh.triangles;

            // ── Build triangles in BVH ORDER (PrimitiveRefs) ─────────────
            // Software BVH leaf firstIndex points to slots in this order.
            // Hardware path uses BuildHardwareGeometryBuffers instead.
            int[] order = sharedMesh.blas.PrimitiveRefs;
            for (int t = 0; t < order.Length; t++)
            {
                ref buildTri bt             = ref sharedMesh.buildTriangles[order[t]];
                int          globalTriIndex = off.triangleOffset + t;
                int          triIndexBase   = globalTriIndex * 3;

                triangles[globalTriIndex] = new Triangle
                {
                    baseIndex = (uint)triIndexBase,
                    edgeAB    = bt.posB - bt.posA,
                    edgeAC    = bt.posC - bt.posA,
                };

                triangleIndices[triIndexBase + 0] = (uint)(off.vertexOffset + meshTriangles[bt.triangleIndex + 0]);
                triangleIndices[triIndexBase + 1] = (uint)(off.vertexOffset + meshTriangles[bt.triangleIndex + 1]);
                triangleIndices[triIndexBase + 2] = (uint)(off.vertexOffset + meshTriangles[bt.triangleIndex + 2]);
            }

            // ── Build BVH nodes ───────────────────────────────────────────
            for (int j = 0; j < sharedMesh.GPUBVH.Count; j++)
            {
                GPUBVHNode node = sharedMesh.GPUBVH[j];
                if (node.left  != -1) node.left  += off.blasNodeOffset;
                if (node.right != -1) node.right += off.blasNodeOffset;
                // firstIndex is a slot index into the BVH-ordered triangle buffer
                node.firstIndex += (uint)off.triangleOffset;
                blasNodes[off.blasNodeOffset + j] = node;
            }

            tV.Clear(); tN.Clear();
        }

        PerfTimer.Time("StructuredVertexBufferCreation", () =>
            ShaderHelper.CreateStructuredBuffer(ref MeshVerticesBuffer, vertices));

        ShaderHelper.CreateStructuredBuffer(ref MeshNormalsBuffer,  normals);
        ShaderHelper.CreateStructuredBuffer(ref TriangleBuffer,     triangles);
        ShaderHelper.CreateStructuredBuffer(ref BVHBuffer,          blasNodes);
        ShaderHelper.CreateStructuredBuffer(ref MeshIndicesBuffer,  triangleIndices);

        if (linearMarchCompute != null)
        {
            linearMarchCompute.SetBuffer(0, "Triangles",       TriangleBuffer);
            linearMarchCompute.SetBuffer(0, "BVHNodes",        BVHBuffer);
            linearMarchCompute.SetBuffer(0, "Vertices",        MeshVerticesBuffer);
            linearMarchCompute.SetBuffer(0, "Normals",         MeshNormalsBuffer);
            linearMarchCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
            linearMarchCompute.SetInt("numBLASNodes",          totalBVHNodes);
        }

        if (reflectionCompute != null)
        {
            reflectionCompute.SetBuffer(0, "Triangles",       TriangleBuffer);
            reflectionCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
            reflectionCompute.SetBuffer(0, "Normals",         MeshNormalsBuffer);
            reflectionCompute.SetBuffer(0, "BVHNodes",        BVHBuffer);
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

        for (int i = 0; i < meshObjects.Count; i++) meshObjects[i].transformDirty = false;

        BvhInstance[]              instances            = new BvhInstance[meshObjects.Count];
        MeshStruct[]               gpuInstances         = new MeshStruct[meshObjects.Count];
        GPULightSource[]           lightSources         = new GPULightSource[meshObjects.Count];
        List<int>                  lightTriangleIndices = new List<int>();
        List<GPULightTriangleData> lightTrianglesData   = new List<GPULightTriangleData>();
        int numLightSources = 0;

        for (int i = 0; i < meshObjects.Count; i++)
        {
            RayTracedMesh meshObj             = meshObjects[i];
            MeshOffsets   off                 = offsets[meshObj.sharedMesh];
            int           localRootIndex      = meshObj.sharedMesh.blas.RootIndex;
            Bounds        localRootBounds     = meshObj.sharedMesh.blas.Nodes[localRootIndex].bounds;
            Bounds        worldBounds         = TransformBoundsToWorld(localRootBounds, meshObj.transform);
            int           globalBlasRootIndex = off.rootNodeIndex;

            instances[i] = new BvhInstance
            {
                blasIndex = i, blasRootIndex = globalBlasRootIndex,
                localToWorld = meshObj.transform.localToWorldMatrix,
                worldToLocal = meshObj.transform.worldToLocalMatrix,
                localBounds = localRootBounds, worldBounds = worldBounds, materialIndex = i
            };

            gpuInstances[i] = new MeshStruct
            {
                localToWorldMatrix = meshObj.transform.localToWorldMatrix,
                worldToLocalMatrix = meshObj.transform.worldToLocalMatrix,
                material           = meshObj.material,
                firstBVHNodeIndex  = (uint)globalBlasRootIndex,
                triangleOffset     = (uint)off.triangleOffset,
                AABBLeftX  = worldBounds.min.x, AABBLeftY  = worldBounds.min.y, AABBLeftZ  = worldBounds.min.z,
                AABBRightX = worldBounds.max.x, AABBRightY = worldBounds.max.y, AABBRightZ = worldBounds.max.z,
            };

            if (meshObj.material.emissiveStrength > 0)
            {
                Matrix4x4      l2w      = meshObj.transform.localToWorldMatrix;
                SharedMeshData sm       = meshObj.sharedMesh;
                int            triStart = lightTriangleIndices.Count;
                float          totalArea = 0f;
                int[]          order    = sm.blas.PrimitiveRefs;

                for (int t = 0; t < order.Length; t++)
                {
                    ref buildTri bt        = ref sm.buildTriangles[order[t]];
                    Vector3      worldAB   = l2w.MultiplyVector(bt.posB - bt.posA);
                    Vector3      worldAC   = l2w.MultiplyVector(bt.posC - bt.posA);
                    Vector3      worldCross = Vector3.Cross(worldAB, worldAC);
                    float        area      = worldCross.magnitude * 0.5f;
                    totalArea += area;

                    // original tri index is order[t], which maps directly into our buffer
                    int globalTriIndex = off.triangleOffset + order[t];
                    lightTriangleIndices.Add(globalTriIndex);

                    while (lightTrianglesData.Count <= globalTriIndex)
                        lightTrianglesData.Add(new GPULightTriangleData());

                    lightTrianglesData[globalTriIndex] = new GPULightTriangleData
                    {
                        worldSpaceArea = area,
                        worldNormal    = worldCross.magnitude > 1e-10f ? worldCross.normalized : Vector3.up
                    };
                }

                lightSources[numLightSources++] = new GPULightSource
                {
                    instanceIndex = i, totalArea = totalArea, triStart = triStart, triCount = order.Length
                };
            }
        }

        TLASBuilder      tlasBuilder  = new TLASBuilder();
        BvhBuildSettings tlasSettings = new BvhBuildSettings
        {
            maxLeafSize = tlasMaxLeafSize, maxDepth = tlasMaxDepth, numBins = tlasNumBins
        };
        tlasBuilder.Build(instances, tlasSettings);

        GPUBVHNode[] tlasNodes = PackNodes(tlasBuilder.Nodes);
        uint[]       tlasRefs  = new uint[tlasBuilder.PrimitiveRefs.Length];
        for (int i = 0; i < tlasRefs.Length; i++) tlasRefs[i] = (uint)tlasBuilder.PrimitiveRefs[i];

        GPULightSource[] trimmedLightSources = new GPULightSource[Mathf.Max(1, numLightSources)];
        Array.Copy(lightSources, trimmedLightSources, numLightSources);

        if (lightTrianglesData.Count == 0) lightTrianglesData.Add(new GPULightTriangleData());

        ShaderHelper.UploadStructuredBuffer(ref TLASBuffer,                 tlasNodes);
        ShaderHelper.UploadStructuredBuffer(ref TLASRefBuffer,              tlasRefs);
        ShaderHelper.UploadStructuredBuffer(ref InstanceBuffer,             gpuInstances);
        ShaderHelper.UploadStructuredBuffer(ref LightSourceBuffer,          trimmedLightSources);
        ShaderHelper.UploadStructuredBuffer(ref LightTriangleIndicesBuffer, lightTriangleIndices);
        ShaderHelper.UploadStructuredBuffer(ref LightTrianglesDataBuffer,   lightTrianglesData);

        if (linearMarchCompute != null)
        {
            linearMarchCompute.SetBuffer(0, "TLASNodes",            TLASBuffer);
            linearMarchCompute.SetBuffer(0, "TLASRefs",             TLASRefBuffer);
            linearMarchCompute.SetBuffer(0, "Instances",            InstanceBuffer);
            linearMarchCompute.SetBuffer(0, "LightSources",         LightSourceBuffer);
            linearMarchCompute.SetBuffer(0, "LightTriangleIndices", LightTriangleIndicesBuffer);
            linearMarchCompute.SetBuffer(0, "LightTrianglesData",   LightTrianglesDataBuffer);
            linearMarchCompute.SetInt("numMeshes",                  gpuInstances.Length);
            linearMarchCompute.SetInt("numTLASNodes",               tlasNodes.Length);
            linearMarchCompute.SetInt("numInstances",               gpuInstances.Length);
            linearMarchCompute.SetInt("TLASRootIndex",              tlasBuilder.RootIndex);
            linearMarchCompute.SetInt("numLightSources",            numLightSources);
            linearMarchCompute.SetInt("BVHTestsSaturation",               BVHNodeTestSaturationValue);
            linearMarchCompute.SetInt("triTestsSaturation",               triTestFullSaturationValue);
            linearMarchCompute.SetInt("TLASNodeVisitsSaturation",         TLASNodeVisitsSaturationValue);
            linearMarchCompute.SetInt("BLASNodeVisitsSaturation",         BLASNodeVisitsSaturationValue);
            linearMarchCompute.SetInt("InstanceBLASTraversalsSaturation", InstanceBLASTraversalsSaturationValue);
            linearMarchCompute.SetInt("TLASLeafRefsVisitedSaturation",    TLASLeafRefsSaturationValue);
            linearMarchCompute.SetInt("u_StepsPerCollisionTest",          StepsPerCollisionTest);
        }

        if (reflectionCompute != null)
        {
            reflectionCompute.SetBuffer(0, "Instances", InstanceBuffer);
            reflectionCompute.SetBuffer(0, "TLASNodes", TLASBuffer);
            reflectionCompute.SetBuffer(0, "TLASRefs",  TLASRefBuffer);
        }

        if (flagVisualizerMaterial != null)
            flagVisualizerMaterial.SetBuffer("Instances", InstanceBuffer);

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
                left = n.leftChild, right = n.rightChild,
                firstIndex = (uint)n.start, count = (uint)n.count,
                AABBLeftX  = n.bounds.min.x, AABBLeftY  = n.bounds.min.y, AABBLeftZ  = n.bounds.min.z,
                AABBRightX = n.bounds.max.x, AABBRightY = n.bounds.max.y, AABBRightZ = n.bounds.max.z,
            };
        }
        return packed;
    }

    void ApplyBlackHoleLUT(ComputeShader cs, RayTracedBlackHole blackHole)
    {
        cs.SetFloat("bendStrength", bendStrength);
        cs.SetFloat("strongFieldCurvatureRadPetMeterCutoff", strongFieldRadPerMeterCuttoff);
    }
// Temporary CPU sort validation — add this method
    void CPUSortActiveRays(int totalPixels)
    {
        // read back count
        uint[] countArr = new uint[1];
        activeRayCountBuffer.GetData(countArr);
        uint activeCount = countArr[0];
        if (activeCount == 0) return;

        // read back indices
        uint[] indices = new uint[activeCount];
        activeRayIndicesBuffer.GetData(indices, 0, 0, (int)activeCount);

        // read back ray directions for sorting
        // MainRay struct is position(float3) + direction(float3) = 24 bytes
        // We need direction which is at offset 12
        Vector3[] directions = new Vector3[activeCount];
        // read full ray buffer — expensive but this is just validation
        MainRay[] rays = new MainRay[totalPixels];
        mainRayBuffer.GetData(rays);

        // build sort keys — quantized octahedral direction encoding
        uint[] keys = new uint[activeCount];
        for (int i = 0; i < activeCount; i++)
        {
            Vector3 d = rays[indices[i]].rayDirection;
            keys[i] = DirectionToSortKey(d);
        }

        // sort indices by key
        System.Array.Sort(keys, indices);

        // upload sorted indices back
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

        bool useHardwareRT = SystemInfo.supportsRayTracing && !forceSoftwareRaytracing;

        scaledW = Mathf.Max(1, Mathf.RoundToInt(source.width  * renderScale));
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
            initCompute.SetInt("_ScreenWidth",      scaledW);
            initCompute.SetInt("_ScreenHeight",     scaledH);
            initCompute.SetInt("numRenderedFrames", numRenderedFrames);

            classifyCompute.SetInt("_ScreenWidth",  scaledW);
            classifyCompute.SetInt("_ScreenHeight", scaledH);

            linearMarchCompute.SetInt("_ScreenWidth",     scaledW);
            linearMarchCompute.SetInt("_ScreenHeight",    scaledH);
            linearMarchCompute.SetFloat("renderDistance", renderDistance);
            linearMarchRaytraceShader.SetFloat("renderDistance",   renderDistance);
            reflectionCompute.SetInt("_ScreenWidth",  scaledW);
            reflectionCompute.SetInt("_ScreenHeight", scaledH);


            for (int ray = 0; ray < raysPerPixel; ray++)
            {
                initCompute.SetInt("currentRayNum", ray);
                DispatchCompute(initCompute, pixelCount, "Init");
                for (int bounce = 0; bounce < maxBounces; bounce++)
                {                
                    activeRayCountBuffer.SetData(zeroOne, 0, REFLECTION_QUEUE, 1); //kill reflection queue
                    DispatchWavefront(classifyCompute, ACTIVE_RAY_QUEUE, "Classify");
                    activeRayCountBuffer.SetData(zeroOne, 0, ACTIVE_RAY_QUEUE, 1); //kill the queue needed to classify
                    DispatchWavefront(linearMarchCompute, LINEAR_RAY_QUEUEA, "LinearMarchA");
                    activeRayCountBuffer.SetData(zeroOne, 0, LINEAR_RAY_QUEUEA, 1); //kill the linear march queue
                    DispatchWavefront(reflectionCompute, REFLECTION_QUEUE, "Reflection");
                    
                }

                /*for (int bounce = 0; bounce < maxBounces; bounce++)
                {
                    DispatchWavefront(classifyCompute,    FLAG_NEEDS_CLASSIFY,     pixelCount, $"Classify_b{bounce}");
                    uint[] count = new uint[1];
                    //activeRayCountBuffer.GetData(count);
                    //Debug.Log($"Bounce {bounce} active after classify: {count[0]} / {pixelCount}");
                    if (useHardwareRT)
                        DispatchHardwareLinearMarch(scaledW, scaledH);
                    else
                        DispatchWavefront(linearMarchCompute, FLAG_NEEDS_LINEAR_MARCH, pixelCount, $"LinearMarch_b{bounce}");

                    DispatchWavefront(reflectionCompute, FLAG_NEEDS_REFLECTION,   pixelCount, $"Reflection_b{bounce}");
                }*/
            }
            uint[] zeros = new uint[NUM_QUEUES]; //garbage collector is not gonna like this, oh well
            activeRayCountBuffer.SetData(zeros);
            accumulateCompute.SetInt("_ScreenWidth",  scaledW);
            accumulateCompute.SetInt("_ScreenHeight", scaledH);
            accumulateCompute.SetInt("raysPerPixel",  raysPerPixel);
            accumulateCompute.SetBuffer(0, "pixelAccum", pixelAccumBuffer);
            accumulateCompute.SetTexture(0, "_Output",   resultTexture);
            DispatchCompute(accumulateCompute, pixelCount);

            if (accumulatorMaterial != null)
            {
                accumulatorMaterial.SetInt("numRenderedFrames", numRenderedFrames);
                accumulatorMaterial.SetTexture("_MainTexOld",   cleanAccumBuffer);
                accumulatorMaterial.SetFloat("accumWeight",     accumWeight);
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
                int[] stepSizes = { 1, 2, 4, 8, 16 };
                foreach (int step in stepSizes)
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
                int[] stepSizes = { 1, 2, 4, 8, 16 };
                foreach (int step in stepSizes)
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
        List<RayTracedMesh> meshObjects = RayTracedMesh.All;
        if (AnyTransformDirty(meshObjects)) tlasDirty = true;

        EnsureMaterialsCreated();
        EnsureBuffersCreated();

        if (!Mathf.Approximately(renderScale, lastRenderScale))
        {
            if (cleanAccumBuffer != null) { cleanAccumBuffer.Release(); cleanAccumBuffer = null; }
            lastRenderScale = renderScale;
            numRenderedFrames = 0;
        }

        int sw = Mathf.Max(1, Mathf.RoundToInt(Screen.width  * renderScale));
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
        float planeWidth  = planeHeight * camera.aspect;

        if (initCompute != null)
        {
            initCompute.SetVector("ViewParams",               new Vector3(planeWidth, planeHeight, camera.nearClipPlane));
            initCompute.SetMatrix("CameraLocalToWorldMatrix", camera.transform.localToWorldMatrix);
            initCompute.SetVector("CameraWorldPos",           camera.transform.position);
            initCompute.SetInt   ("numRenderedFrames",        numRenderedFrames);
        }

        if (blackHoleSOIStepSize == 0) blackHoleSOIStepSize = 1.0f;
    }

    void UpdateRayTracingParams()
    {
        if (linearMarchCompute == null) return;
        if (useTlas) linearMarchCompute.EnableKeyword("USE_TLAS");
        else         linearMarchCompute.DisableKeyword("USE_TLAS");
    }

    void allocateBlackHoleBuffer()
    {
        RayTracedBlackHole[] blackHoleObjects = FindObjectsOfType<RayTracedBlackHole>();
        BlackHole[]          blackHoles       = new BlackHole[blackHoleObjects.Length];

        for (int i = 0; i < blackHoleObjects.Length; i++)
        {
            blackHoles[i] = new BlackHole()
            {
                position               = blackHoleObjects[i].transform.position,
                radius                 = blackHoleObjects[i].transform.localScale.x * 0.5f,
                blackHoleSOIMultiplier = blackHoleObjects[i].blackHoleSOIMultiplier,
            };

            if (blackHoles[i].blackHoleSOIMultiplier <= 0)
            {
                Debug.LogError("BlackHoleSOIMultiplier is <= 0 for " + blackHoleObjects[i].name);
                blackHoles[i].blackHoleSOIMultiplier = 1.0f;
            }
        }

        if (blackHoleObjects.Length > 0)
            ApplyBlackHoleLUT(classifyCompute, blackHoleObjects[0]);

        ShaderHelper.UploadStructuredBuffer(ref blackHoleBuffer, blackHoles);

        classifyCompute.SetBuffer(0, "blackholes",      blackHoleBuffer);
        classifyCompute.SetInt   ("num_black_holes",    blackHoleObjects.Length);

        linearMarchCompute.SetBuffer(0, "blackholes",   blackHoleBuffer);
        linearMarchCompute.SetInt   ("num_black_holes", blackHoleObjects.Length);

        if (SystemInfo.supportsRayTracing && !forceSoftwareRaytracing && linearMarchRaytraceShader != null)
        {
            linearMarchRaytraceShader.SetBuffer("blackholes",      blackHoleBuffer);
            linearMarchRaytraceShader.SetInt   ("num_black_holes", blackHoleObjects.Length);
        }
    }
}