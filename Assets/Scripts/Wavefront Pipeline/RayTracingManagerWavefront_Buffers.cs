using UnityEngine;

public partial class RayTracingManagerWavefront
{
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
        bucketCountsBuffer?.Release();         bucketCountsBuffer         = null;
        bucketOffsetsBuffer?.Release();        bucketOffsetsBuffer        = null;
        sortedRaysBuffer?.Release();           sortedRaysBuffer           = null;
        sortedRayIndicesBuffer?.Release();     sortedRayIndicesBuffer     = null;
        pixelForSlotBuffer?.Release();         pixelForSlotBuffer         = null;
        sortedPixelForSlotBuffer?.Release();   sortedPixelForSlotBuffer   = null;
        newSlotForOldSlotBuffer?.Release();    newSlotForOldSlotBuffer    = null;
        sortedControlsBuffer?.Release();       sortedControlsBuffer = null;
        sortedRayColorInfoBuffer?.Release();   sortedRayColorInfoBuffer = null;
        neeQueueBuffer?.Release();             neeQueueBuffer = null;
        if (accelStructure != null) { accelStructure.Release(); accelStructure = null; }
        resultTexture?.Release();    resultTexture = null;
        cleanAccumBuffer?.Release(); cleanAccumBuffer = null;
        
        if (accumulatorMaterial       != null) { DestroyImmediate(accumulatorMaterial);       accumulatorMaterial       = null; }
        if (ditherMaterial            != null) { DestroyImmediate(ditherMaterial);            ditherMaterial            = null; }
        if (colorQuantizationMaterial != null) { DestroyImmediate(colorQuantizationMaterial); colorQuantizationMaterial = null; }
        if (atrousMaterial            != null) { DestroyImmediate(atrousMaterial);            atrousMaterial            = null; }

        buffersHaveRealData = false;
        
    }

    void EnsureBuffersCreated(bool forceRecreate = false)
    {
        int bufferWidth  = Mathf.Max(1, Mathf.RoundToInt(Screen.width * renderScale));
        int bufferHeight = Mathf.Max(1, Mathf.RoundToInt(Screen.height * renderScale));
        int pixelCount   = bufferWidth * bufferHeight;

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

        ShaderHelper.CreateStructuredBuffer<Control>(ref controlQueue,           pixelCount*raysPerPixel);
        ShaderHelper.CreateStructuredBuffer<MainRay>(ref mainRayBuffer,          pixelCount*raysPerPixel);
        ShaderHelper.CreateStructuredBuffer<MainRay>(ref sortedRaysBuffer,       pixelCount*raysPerPixel); // ping-pong twin
        ShaderHelper.CreateStructuredBuffer<HitInfo>(ref HitInfoBuffer,          pixelCount*raysPerPixel);
        ShaderHelper.CreateStructuredBuffer<RayColorInfo>(ref rayColorInfoBuffer, pixelCount*raysPerPixel);
        ShaderHelper.CreateStructuredBuffer<PixelAccum>(ref pixelAccumBuffer, pixelCount);
        ShaderHelper.CreateStructuredBuffer<uint>(ref reflectionQueueBuffer,    pixelCount*raysPerPixel);
        ShaderHelper.CreateStructuredBuffer<uint>(ref skyboxQueueBuffer,        pixelCount*raysPerPixel);

        ShaderHelper.CreateStructuredBuffer<uint>(ref activeRayIndicesBuffer,   pixelCount*raysPerPixel);
        ShaderHelper.CreateStructuredBuffer<uint>(ref sortedRayIndicesBuffer,   pixelCount*raysPerPixel); // ping-pong twin
        ShaderHelper.CreateStructuredBuffer<uint>(ref activeRayCountBuffer,     NUM_QUEUES);

        // Bucket sort buckets
        int numBuckets = 6 * sortBucketsPerAxis * sortBucketsPerAxis;
        lastSortBucketsPerAxis = sortBucketsPerAxis;
        ShaderHelper.CreateStructuredBuffer<uint>(ref bucketCountsBuffer,  numBuckets);
        ShaderHelper.CreateStructuredBuffer<uint>(ref bucketOffsetsBuffer, numBuckets);

        // Mapping buffers
        ShaderHelper.CreateStructuredBuffer<uint>(ref pixelForSlotBuffer,       pixelCount*raysPerPixel); // slot -> pixel
        ShaderHelper.CreateStructuredBuffer<uint>(ref sortedPixelForSlotBuffer, pixelCount*raysPerPixel); // scratch
        ShaderHelper.CreateStructuredBuffer<uint>(ref newSlotForOldSlotBuffer,  pixelCount*raysPerPixel); // old slot -> new slot
        ShaderHelper.CreateStructuredBuffer<Control>(ref sortedControlsBuffer, pixelCount*raysPerPixel);
        ShaderHelper.CreateStructuredBuffer<RayColorInfo>(ref sortedRayColorInfoBuffer, pixelCount*raysPerPixel);
        ShaderHelper.CreateStructuredBuffer<uint>(ref neeQueueBuffer, pixelCount * raysPerPixel);
        if (indirectArgsBuffer == null || !indirectArgsBuffer.IsValid())
        {
            indirectArgsBuffer?.Release();
            indirectArgsBuffer = new ComputeBuffer(NUM_QUEUES * 3, sizeof(uint), ComputeBufferType.IndirectArguments);
        }
    }
    ComputeShader[] AllComputeShaders => new[]
{
    initCompute, classifyCompute, reflectionCompute, neeCompute,
    accumulateCompute, writeIndirectArgsCompute, resetCountCompute, bucketSortCompute
};

void SetBufferAll(string name, ComputeBuffer buffer)
{
    foreach (var cs in AllComputeShaders)
        if (cs != null) cs.SetBuffer(0, name, buffer);
}

void SetIntAll(string name, int value)
{
    foreach (var cs in AllComputeShaders)
        if (cs != null) cs.SetInt(name, value);
}

void SetFloatAll(string name, float value)
{
    foreach (var cs in AllComputeShaders)
        if (cs != null) cs.SetFloat(name, value);
}

void BindBuffersToShaders()
{
    // ─── Ray / control buffers ─────────────────────────────────────────────
    SetBufferAll("main_rays",        mainRayBuffer);
    SetBufferAll("controls",         controlQueue);
    SetBufferAll("hit_info_buffer",  HitInfoBuffer);
    SetBufferAll("ray_color_info",   rayColorInfoBuffer);
    SetBufferAll("activeRayIndices", activeRayIndicesBuffer);
    SetBufferAll("activeRayCount",   activeRayCountBuffer);
    SetBufferAll("pixelAccum",       pixelAccumBuffer);
    SetBufferAll("pixelForSlot",     pixelForSlotBuffer);
    SetBufferAll("neeQueue",         neeQueueBuffer);
    SetBufferAll("reflectionQueue",  reflectionQueueBuffer);
    SetBufferAll("skyboxQueue",      skyboxQueueBuffer);
    SetBufferAll("indirectArgs",     indirectArgsBuffer);
    SetBufferAll("blackholes",       blackHoleBuffer);

    // ─── Geometry buffers ──────────────────────────────────────────────────
    SetBufferAll("Instances",        InstanceBuffer);
    SetBufferAll("Triangles",        TriangleBuffer);
    SetBufferAll("TriangleIndices",  MeshIndicesBuffer);
    SetBufferAll("Normals",          MeshNormalsBuffer);
    SetBufferAll("Vertices",         MeshVerticesBuffer);
    SetBufferAll("BVHNodes",         BVHBuffer);
    SetBufferAll("TLASNodes",        TLASBuffer);
    SetBufferAll("TLASRefs",         TLASRefBuffer);

    // ─── Light buffers ─────────────────────────────────────────────────────
    SetBufferAll("LightSources",         LightSourceBuffer);
    SetBufferAll("LightTriangleIndices", LightTriangleIndicesBuffer);
    SetBufferAll("LightTrianglesData",   LightTrianglesDataBuffer);

    // ─── Ints ──────────────────────────────────────────────────────────────
    SetIntAll("numMeshes",                          tlasGpuInstances.Length);
    SetIntAll("numInstances",                       tlasGpuInstances.Length);
    SetIntAll("numTLASNodes",                       tlasNodesCache.Length);
    SetIntAll("TLASRootIndex",                      tlasBuilder.RootIndex);
    SetIntAll("numLightSources",                    numLightSources);
    SetIntAll("numBLASNodes",                       totalBVHNodes);
    SetIntAll("NUM_QUEUES",                         NUM_QUEUES);
    SetIntAll("emergencyBreakMaxSteps",             emergencyBreakMaxSteps);
    SetIntAll("BVHTestsSaturation",                 BVHNodeTestSaturationValue);
    SetIntAll("triTestsSaturation",                 triTestFullSaturationValue);
    SetIntAll("TLASNodeVisitsSaturation",           TLASNodeVisitsSaturationValue);
    SetIntAll("BLASNodeVisitsSaturation",           BLASNodeVisitsSaturationValue);
    SetIntAll("InstanceBLASTraversalsSaturation",   InstanceBLASTraversalsSaturationValue);
    SetIntAll("TLASLeafRefsVisitedSaturation",      TLASLeafRefsSaturationValue);
    SetIntAll("u_StepsPerCollisionTest",            StepsPerCollisionTest);

    // ─── Floats ────────────────────────────────────────────────────────────
    SetFloatAll("renderDistance", renderDistance);
    SetFloatAll("stepSize",       blackHoleSOIStepSize);

    // ─── Hardware RT only ──────────────────────────────────────────────────
    bool useHardwareRT = SystemInfo.supportsRayTracing && !forceSoftwareRaytracing;
    if (useHardwareRT && classifyCompute != null)
    {
        classifyCompute.SetRayTracingAccelerationStructure(0, "_RTAS", accelStructure);
        neeCompute.SetRayTracingAccelerationStructure(0, "_RTAS", accelStructure);
    }
}
}
