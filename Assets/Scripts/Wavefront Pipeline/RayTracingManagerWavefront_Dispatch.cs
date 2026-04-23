using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Debug = UnityEngine.Debug;
using UnityEngine.Profiling;

public partial class RayTracingManagerWavefront
{
    
    void DispatchCompute(ComputeShader cs, int pixelCount, string label = "")
    {
        Profiler.BeginSample(label == "" ? cs.name : label);
        cs.Dispatch(0, Mathf.CeilToInt(pixelCount / 64f), 1, 1);
        Profiler.EndSample();
    }

    void DispatchWavefront(ComputeShader cs, int queueIndex, string label = "", int kernelIndex = 0)
    {
        int groups = Mathf.CeilToInt(NUM_QUEUES / 8f);
        writeIndirectArgsCompute.Dispatch(0, groups, 1, 1);

        Profiler.BeginSample(label);
        uint byteOffset = (uint)queueIndex * 3 * sizeof(uint);
        cs.DispatchIndirect(kernelIndex, indirectArgsBuffer, byteOffset);
        Profiler.EndSample();
    }
    // Sorts main_rays physically by direction bucket into sortedRaysBuffer,
    // scatters activeRayIndices into sortedRayIndicesBuffer,
    // builds oldPixelForSlot (slot->pixel) and newSlotForPixel (pixel->slot),
    // swaps both pairs of ping-pong buffers,
    // then fixups reflectionQueue and skyboxQueue to use new slot indices.
    void DispatchBucketSort()
    {
        if (bucketSortCompute == null) return;

        Profiler.BeginSample("BucketSort");

        int numBuckets = 6 * sortBucketsPerAxis * sortBucketsPerAxis;
        bucketSortCompute.SetInt("_BucketsPerAxis", sortBucketsPerAxis);
        bucketSortCompute.SetInt("_NumBuckets", numBuckets);

        // Pass 0 — clear bucket counts
        bucketSortCompute.SetBuffer(KERNEL_CLEAR_BUCKETS, "bucketCounts", bucketCountsBuffer);
        bucketSortCompute.Dispatch(KERNEL_CLEAR_BUCKETS, Mathf.CeilToInt(numBuckets / 64f), 1, 1);

        int groups = Mathf.CeilToInt(NUM_QUEUES / 8f);
        writeIndirectArgsCompute.Dispatch(0, groups, 1, 1);

        uint activeByteOffset = (uint)ACTIVE_RAY_QUEUE * 3 * sizeof(uint);

        // Pass 1 — count rays per bucket
        bucketSortCompute.SetBuffer(KERNEL_COUNT_BUCKETS, "activeRayIndices", activeRayIndicesBuffer);
        bucketSortCompute.SetBuffer(KERNEL_COUNT_BUCKETS, "activeRayCount", activeRayCountBuffer);
        bucketSortCompute.SetBuffer(KERNEL_COUNT_BUCKETS, "main_rays", mainRayBuffer);
        bucketSortCompute.SetBuffer(KERNEL_COUNT_BUCKETS, "bucketCounts", bucketCountsBuffer);
        bucketSortCompute.DispatchIndirect(KERNEL_COUNT_BUCKETS, indirectArgsBuffer, activeByteOffset);

        // Pass 2 — prefix sum
        bucketSortCompute.SetBuffer(KERNEL_PREFIX_SUM, "bucketCounts", bucketCountsBuffer);
        bucketSortCompute.SetBuffer(KERNEL_PREFIX_SUM, "bucketOffsets", bucketOffsetsBuffer);
        bucketSortCompute.Dispatch(KERNEL_PREFIX_SUM, 1, 1, 1);

        // Pass 3 — scatter rays into sorted buffers, preserve slot->pixel mapping
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "activeRayIndices", activeRayIndicesBuffer);
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "activeRayCount", activeRayCountBuffer);
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "main_rays", mainRayBuffer);
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "sortedRays", sortedRaysBuffer);
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "pixelForSlot", pixelForSlotBuffer);
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "sortedPixelForSlot", sortedPixelForSlotBuffer);
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "newSlotForOldSlot", newSlotForOldSlotBuffer);
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "sortedRayIndices", sortedRayIndicesBuffer);
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "bucketCounts", bucketCountsBuffer);
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "bucketOffsets", bucketOffsetsBuffer);
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "controls", controlQueue);
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "sortedControls", sortedControlsBuffer);
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "ray_color_info", rayColorInfoBuffer);
        bucketSortCompute.SetBuffer(KERNEL_SCATTER_RAYS, "sortedRayColorInfo", sortedRayColorInfoBuffer);
        bucketSortCompute.DispatchIndirect(KERNEL_SCATTER_RAYS, indirectArgsBuffer, activeByteOffset);

        // Swap in sorted storage
        (mainRayBuffer, sortedRaysBuffer) = (sortedRaysBuffer, mainRayBuffer);
        (pixelForSlotBuffer, sortedPixelForSlotBuffer) = (sortedPixelForSlotBuffer, pixelForSlotBuffer);
        (activeRayIndicesBuffer, sortedRayIndicesBuffer) = (sortedRayIndicesBuffer, activeRayIndicesBuffer);
        (controlQueue, sortedControlsBuffer) = (sortedControlsBuffer, controlQueue);
        (rayColorInfoBuffer, sortedRayColorInfoBuffer) = (sortedRayColorInfoBuffer, rayColorInfoBuffer);
        //RebindRayBuffers();
        //RebindPixelForSlotBuffer();
        //RebindControlBuffer();
        //RebindRayColorInfoBuffer();
        BindBuffersToShaders();
        // Pass 4 — fix reflection queue (queue stores old slots)
        bucketSortCompute.SetBuffer(KERNEL_FIXUP_QUEUE, "queueToFixup", reflectionQueueBuffer);
        bucketSortCompute.SetBuffer(KERNEL_FIXUP_QUEUE, "queueCount", activeRayCountBuffer);
        bucketSortCompute.SetBuffer(KERNEL_FIXUP_QUEUE, "newSlotForOldSlot", newSlotForOldSlotBuffer);
        bucketSortCompute.SetInt("queueIndex", REFLECTION_QUEUE);

        uint reflByteOffset = (uint)REFLECTION_QUEUE * 3 * sizeof(uint);
        writeIndirectArgsCompute.Dispatch(0, groups, 1, 1);
        bucketSortCompute.DispatchIndirect(KERNEL_FIXUP_QUEUE, indirectArgsBuffer, reflByteOffset);

        // Pass 5 — fix skybox queue
        bucketSortCompute.SetBuffer(KERNEL_FIXUP_QUEUE, "queueToFixup", skyboxQueueBuffer);
        bucketSortCompute.SetInt("queueIndex", SKYBOX_QUEUE);

        uint skyByteOffset = (uint)SKYBOX_QUEUE * 3 * sizeof(uint);
        bucketSortCompute.DispatchIndirect(KERNEL_FIXUP_QUEUE, indirectArgsBuffer, skyByteOffset);

        Profiler.EndSample();
    }
    void InitFrame()
    {
        EnsureMaterialsCreated();
        EnsureBuffersCreated();

        List<RayTracedMesh> meshObjects = RayTracedMesh.All;
        if (AnyTransformDirty(meshObjects)) tlasDirty = true;
        
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
        UpdateRayTracingParams();  
        BindBuffersToShaders();
        allocateBlackHoleBuffer();
        UpdateAtmosphereParams();
        UpdateCameraParams(Camera.current);
        //UpdateRayTracingParams();
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
            initCompute.SetInt("_ScreenWidth",  scaledW);
            initCompute.SetInt("_ScreenHeight", scaledH);
            initCompute.SetInt("numRenderedFrames", numRenderedFrames);

            classifyCompute.SetInt("_ScreenWidth",  scaledW);
            classifyCompute.SetInt("_ScreenHeight", scaledH);

            reflectionCompute.SetInt("_ScreenWidth",  scaledW);
            reflectionCompute.SetInt("_ScreenHeight", scaledH);

            //for (int ray = 0; ray < raysPerPixel; ray++)
            //{
                initCompute.SetInt("raysPerPixel", raysPerPixel);

                DispatchCompute(initCompute, pixelCount, "Init");
                
                for (int bounce = 0; bounce < maxBounces; bounce++)
                {
                    activeRayCountBuffer.SetData(zeroOne, 0, REFLECTION_QUEUE, 1);
                    activeRayCountBuffer.SetData(zeroOne, 0, SKYBOX_QUEUE, 1);
                    activeRayCountBuffer.SetData(zeroOne, 0, NEE_QUEUE, 1);
                    DispatchBucketSort();

                    DispatchWavefront(classifyCompute, ACTIVE_RAY_QUEUE, "Propagate");

                    DispatchCompute(resetCountCompute, 1, "reset");
                    reflectionCompute.SetInt("numBounces", bounce);
                    DispatchWavefront(reflectionCompute, REFLECTION_QUEUE, "Reflection");

                    if (useNEE)                         
                        DispatchWavefront(neeCompute,        NEE_QUEUE,         "NEE");
                    //activeRayCountBuffer.GetData(counts);
                    //Debug.Log($"bounce {bounce} — REFLECTION: {counts[REFLECTION_QUEUE]}, NEE: {counts[NEE_QUEUE]}, ACTIVE: {counts[ACTIVE_RAY_QUEUE]}");

                    

                }
                DispatchCompute(resetCountCompute, 1, "reset");
                activeRayCountBuffer.SetData(zeroOne, 0, REFLECTION_QUEUE, 1);
                activeRayCountBuffer.SetData(zeroOne, 0, SKYBOX_QUEUE, 1);
                activeRayCountBuffer.SetData(zeroOne, 0, NEE_QUEUE, 1);
            //}

            activeRayCountBuffer.SetData(zerosNUM_QUEUES);

            accumulateCompute.SetInt("_ScreenWidth",  scaledW);
            accumulateCompute.SetInt("_ScreenHeight", scaledH);
            accumulateCompute.SetInt("raysPerPixel",  raysPerPixel);
            accumulateCompute.SetBuffer(0, "pixelAccum",      pixelAccumBuffer);
            accumulateCompute.SetTexture(0, "_Output",        resultTexture);
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
            if (colorQuantization && colorQuantizationMaterial != null)
            {
                colorQuantizationMaterial.SetInt("numColors", numColors);
                Graphics.Blit(currentFrame, tempBuffer, colorQuantizationMaterial);
                Swap(ref currentFrame, ref tempBuffer);
            }
            if (ditherPostProcess && !ditherBeforeUpscale && ditherMaterial != null)
            {
                ditherMaterial.SetInt("matrixSize", ditherMatrixSize);
                Graphics.Blit(currentFrame, tempBuffer, ditherMaterial);
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
}
