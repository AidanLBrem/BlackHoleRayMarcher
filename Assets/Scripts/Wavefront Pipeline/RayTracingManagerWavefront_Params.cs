using System;
using System.Collections.Generic;
using UnityEngine;
using Debug = UnityEngine.Debug;
using UnityEngine.Profiling;

public partial class RayTracingManagerWavefront
{
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
    void UpdateAtmosphereParams() { }
    void UpdateCameraParams(Camera camera)
    {
        float planeHeight = camera.nearClipPlane * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 2f;
        float planeWidth  = planeHeight * camera.aspect;

        if (initCompute != null)
        {
            initCompute.SetVector("ViewParams",               new Vector3(planeWidth, planeHeight, camera.nearClipPlane));
            initCompute.SetMatrix("CameraLocalToWorldMatrix", camera.transform.localToWorldMatrix);
            initCompute.SetVector("CameraWorldPos",           camera.transform.position);
            initCompute.SetInt("numRenderedFrames",           numRenderedFrames);
        }

        if (blackHoleSOIStepSize == 0) blackHoleSOIStepSize = 1.0f;
    }

    void UpdateRayTracingParams()
    {
        if (classifyCompute != null)
        {
            if (useTlas) classifyCompute.EnableKeyword("USE_TLAS");
            else         classifyCompute.DisableKeyword("USE_TLAS");
            if (forceSoftwareRaytracing)
            {
                classifyCompute.EnableKeyword("FORCE_SOFTWARE_RT");
                reflectionCompute.EnableKeyword("FORCE_SOFTWARE_RT");
            }
            else
            {
                classifyCompute.DisableKeyword("FORCE_SOFTWARE_RT");
                reflectionCompute.DisableKeyword("FORCE_SOFTWARE_RT");
            }
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
                    position               = cachedBlackHoleObjects[i].transform.position,
                    radius                 = cachedBlackHoleObjects[i].transform.localScale.x * 0.5f,
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
                cachedBlackHoles[i].radius   = cachedBlackHoleObjects[i].transform.localScale.x * 0.5f;
            }
        }

        if (cachedBlackHoleObjects.Length > 0 && classifyCompute != null)
            ApplyBlackHoleLUT(classifyCompute, cachedBlackHoleObjects[0]);

        ShaderHelper.UploadStructuredBuffer(ref blackHoleBuffer, cachedBlackHoles);

        if (classifyCompute != null)
        {
            classifyCompute.SetBuffer(0, "blackholes",  blackHoleBuffer);
            classifyCompute.SetInt("num_black_holes",   cachedBlackHoleObjects.Length);
        }
    }
}
