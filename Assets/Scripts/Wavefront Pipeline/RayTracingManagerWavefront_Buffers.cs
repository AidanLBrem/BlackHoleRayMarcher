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
    
    void BindBuffersToShaders()
    {
        BindInitBuffers();
        BindClassifyBuffers();
        BindReflectionBuffers();
        BindNEEBuffers();
        BindAccumulateBuffers();
        BindWavefrontBuffers();
        BindIndirectArgs();
    }

    void BindWavefrontBuffers()
    {

        if (writeIndirectArgsCompute != null)
        {
            writeIndirectArgsCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
            writeIndirectArgsCompute.SetBuffer(0, "indirectArgs",   indirectArgsBuffer);
        }

        if (resetCountCompute != null)
            resetCountCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        
        if (reflectionCompute != null)
        {
            reflectionCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
            reflectionCompute.SetBuffer(0, "main_rays",        mainRayBuffer);
        }

        if (classifyCompute != null)
        {
            classifyCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
            classifyCompute.SetBuffer(0, "main_rays",        mainRayBuffer);
        }

        if (neeCompute != null)
        {
            reflectionCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
            reflectionCompute.SetBuffer(0, "main_rays",        mainRayBuffer);
        }
    }

    void BindInitBuffers()
    {
        if (initCompute == null) return;

        initCompute.SetBuffer(0, "controls",         controlQueue);
        initCompute.SetBuffer(0, "main_rays",        mainRayBuffer);
        initCompute.SetBuffer(0, "ray_color_info",   rayColorInfoBuffer);
        initCompute.SetBuffer(0, "hit_info_buffer",  HitInfoBuffer);
        initCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
        initCompute.SetBuffer(0, "activeRayCount",   activeRayCountBuffer);
        initCompute.SetBuffer(0, "pixelAccum",       pixelAccumBuffer);
        initCompute.SetBuffer(0, "pixelForSlot", pixelForSlotBuffer);
    }

    void BindClassifyBuffers()
    {
        if (classifyCompute == null) return;

        classifyCompute.SetBuffer(0, "controls",         controlQueue);
        classifyCompute.SetBuffer(0, "main_rays",        mainRayBuffer);
        classifyCompute.SetBuffer(0, "hit_infos",        HitInfoBuffer);
        classifyCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
        classifyCompute.SetBuffer(0, "activeRayCount",   activeRayCountBuffer);
        classifyCompute.SetBuffer(0, "reflectionQueue",  reflectionQueueBuffer);
        classifyCompute.SetBuffer(0, "skyboxQueue",      skyboxQueueBuffer);
        classifyCompute.SetBuffer(0, "Instances",        InstanceBuffer);
        classifyCompute.SetBuffer(0, "Normals",          MeshNormalsBuffer);
        classifyCompute.SetBuffer(0, "TriangleIndices",  MeshIndicesBuffer);
        classifyCompute.SetBuffer(0, "blackholes",       blackHoleBuffer);
        classifyCompute.SetBuffer(0, "Triangles",        TriangleBuffer);
        classifyCompute.SetBuffer(0, "BVHNodes",         BVHBuffer);
        classifyCompute.SetBuffer(0, "TLASNodes",        TLASBuffer);
        classifyCompute.SetBuffer(0, "TLASRefs",         TLASRefBuffer);
        classifyCompute.SetBuffer(0, "Vertices",         MeshVerticesBuffer);
        classifyCompute.SetFloat("renderDistance",        renderDistance);
        classifyCompute.SetFloat("stepSize",              blackHoleSOIStepSize);
        classifyCompute.SetInt("emergencyBreakMaxSteps",  emergencyBreakMaxSteps);
        classifyCompute.SetBuffer(0, "pixelAccum",        pixelAccumBuffer);
        classifyCompute.SetBuffer(0, "pixelForSlot", pixelForSlotBuffer);

    }

    void BindReflectionBuffers()
    {
        if (reflectionCompute == null) return;

        reflectionCompute.SetBuffer(0, "controls",         controlQueue);
        reflectionCompute.SetBuffer(0, "main_rays",        mainRayBuffer);
        reflectionCompute.SetBuffer(0, "hit_info_buffer",  HitInfoBuffer);
        reflectionCompute.SetBuffer(0, "ray_color_info",   rayColorInfoBuffer);
        reflectionCompute.SetBuffer(0, "pixelAccum",       pixelAccumBuffer);
        reflectionCompute.SetBuffer(0, "reflectionQueue",  reflectionQueueBuffer);
        reflectionCompute.SetBuffer(0, "activeRayIndices", activeRayIndicesBuffer);
        reflectionCompute.SetBuffer(0, "activeRayCount",   activeRayCountBuffer);
        reflectionCompute.SetBuffer(0, "Instances",        InstanceBuffer);
        reflectionCompute.SetBuffer(0, "Triangles",        TriangleBuffer);
        reflectionCompute.SetBuffer(0, "TriangleIndices",  MeshIndicesBuffer);
        reflectionCompute.SetBuffer(0, "Normals",          MeshNormalsBuffer);
        reflectionCompute.SetBuffer(0, "BVHNodes",         BVHBuffer);
        reflectionCompute.SetBuffer(0, "TLASNodes",        TLASBuffer);
        reflectionCompute.SetBuffer(0, "TLASRefs",         TLASRefBuffer);
        reflectionCompute.SetBuffer(0, "pixelForSlot", pixelForSlotBuffer);
        reflectionCompute.SetBuffer(0, "neeQueue",             neeQueueBuffer);
        reflectionCompute.SetBuffer(0, "LightSources",         LightSourceBuffer);
        //reflectionCompute.SetInt("numLightSources",             (int)numLightSources);
    }

    void BindNEEBuffers()
    {
        if (neeCompute == null) return;
        neeCompute.SetBuffer(0, "neeQueue",             neeQueueBuffer);
        neeCompute.SetBuffer(0, "activeRayCount",       activeRayCountBuffer);
        neeCompute.SetBuffer(0, "controls",             controlQueue);
        neeCompute.SetBuffer(0, "main_rays",            mainRayBuffer);
        neeCompute.SetBuffer(0, "hit_info_buffer",      HitInfoBuffer);
        neeCompute.SetBuffer(0, "ray_color_info",       rayColorInfoBuffer);
        neeCompute.SetBuffer(0, "Instances",            InstanceBuffer);
        neeCompute.SetBuffer(0, "Triangles",            TriangleBuffer);
        neeCompute.SetBuffer(0, "TriangleIndices",      MeshIndicesBuffer);
        neeCompute.SetBuffer(0, "Normals",              MeshNormalsBuffer);
        neeCompute.SetBuffer(0, "Vertices",             MeshVerticesBuffer);
        neeCompute.SetBuffer(0, "LightSources",         LightSourceBuffer);
        neeCompute.SetBuffer(0, "LightTriangleIndices", LightTriangleIndicesBuffer);
        neeCompute.SetBuffer(0, "LightTrianglesData",   LightTrianglesDataBuffer);
        neeCompute.SetBuffer(0, "Instances",        InstanceBuffer);
        neeCompute.SetBuffer(0, "Triangles",        TriangleBuffer);
        neeCompute.SetBuffer(0, "TriangleIndices",  MeshIndicesBuffer);
        neeCompute.SetBuffer(0, "Normals",          MeshNormalsBuffer);
        neeCompute.SetBuffer(0, "BVHNodes",         BVHBuffer);
        neeCompute.SetBuffer(0, "TLASNodes",        TLASBuffer);
        neeCompute.SetBuffer(0, "TLASRefs",         TLASRefBuffer);
        neeCompute.SetBuffer(0, "pixelAccum", pixelAccumBuffer);
    }

    void BindAccumulateBuffers()
    {
        if (accumulateCompute == null) return;
        accumulateCompute.SetBuffer(0, "pixelAccum", pixelAccumBuffer);
    }
    void RebindPixelForSlotBuffer()
    {
        initCompute?.SetBuffer(0, "pixelForSlot", pixelForSlotBuffer);
        classifyCompute?.SetBuffer(0, "pixelForSlot", pixelForSlotBuffer);
        reflectionCompute?.SetBuffer(0, "pixelForSlot", pixelForSlotBuffer);
    }
    

    void BindIndirectArgs()
    {
        if (writeIndirectArgsCompute == null) return;
        writeIndirectArgsCompute.SetInt("NUM_QUEUES",           NUM_QUEUES);
        writeIndirectArgsCompute.SetBuffer(0, "activeRayCount", activeRayCountBuffer);
        writeIndirectArgsCompute.SetBuffer(0, "indirectArgs",   indirectArgsBuffer);
    }

    // Rebinds main_rays and activeRayIndices to all shaders after a buffer swap.
    void RebindRayBuffers()
    {
        classifyCompute?.SetBuffer(0,    "activeRayIndices", activeRayIndicesBuffer);
        classifyCompute?.SetBuffer(0,    "main_rays",        mainRayBuffer);
        reflectionCompute?.SetBuffer(0,  "activeRayIndices", activeRayIndicesBuffer);
        reflectionCompute?.SetBuffer(0,  "main_rays",        mainRayBuffer);
        initCompute?.SetBuffer(0,        "main_rays",        mainRayBuffer);
    }
    
    void RebindControlBuffer()
    {
        initCompute?.SetBuffer(0, "controls", controlQueue);
        classifyCompute?.SetBuffer(0, "controls", controlQueue);
        reflectionCompute?.SetBuffer(0, "controls", controlQueue);
    }

    void RebindRayColorInfoBuffer()
    {
        initCompute?.SetBuffer(0, "ray_color_info", rayColorInfoBuffer);
        reflectionCompute?.SetBuffer(0, "ray_color_info", rayColorInfoBuffer);
    }
    void BindHardwareRTBuffers()
    {
        if (classifyCompute == null) return;

        // Bind the RTAS
        classifyCompute.SetRayTracingAccelerationStructure(0, "_RTAS", accelStructure);

        // Bind Instances and Triangles — populated by BuildHardwareGeometryBuffers
        classifyCompute.SetBuffer(0, "Instances", InstanceBuffer);
        classifyCompute.SetBuffer(0, "Triangles", TriangleBuffer);
        classifyCompute.SetBuffer(0, "TriangleIndices", MeshIndicesBuffer);
        classifyCompute.SetBuffer(0, "Normals", MeshNormalsBuffer);
        classifyCompute.SetBuffer(0, "Vertices", MeshVerticesBuffer);

        // Safety — clear BVH buffers so software path data can't bleed through
        if (BVHBuffer != null)
        {
            uint[] zeros = new uint[BVHBuffer.count * (BVHBuffer.stride / sizeof(uint))];
            BVHBuffer.SetData(zeros);
        }

        classifyCompute.SetInt("numMeshes", tlasGpuInstances.Length);
        classifyCompute.SetInt("numInstances", tlasGpuInstances.Length);
    }
}
