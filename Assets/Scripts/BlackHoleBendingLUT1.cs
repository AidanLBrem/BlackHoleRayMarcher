using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Attempts to connect two points with a geodisc
/// </summary>
public struct Geodisc
{
    public bool hit;
    public float totalAngle;
    public float exitAngle;
    public float r0;
    public float r;
    public float alpha0;
    public float alpha;
    public float phi;

}
public static class GeoDiscSolver
{

    public static float analyticStepSize = 0.01f;

    //solve the geodisc for an infinitely far away direction (directional light)
    public static Texture2D SolveGeodisc(float rs, int radiusResolution, int muResolution, float rsMin, float rsMax, float logEpsilonOverRs, ref Texture2D LUT)
    {
        float radiusStep = (rsMax - rsMin) / radiusResolution;
        float muStep = Mathf.PI / muResolution;
        for (int x = 0; x < radiusResolution; x++)
        {
            //set starting r0
            float r0 = Mathf.Lerp(rsMin, rsMax, x / (float)(radiusResolution - 1));
            for (int y = 0; y < muResolution; y++)
            {
                //set starting angle
                float alpha0 = Mathf.Lerp(0f, Mathf.PI, y / (float)(muResolution - 1));
                Geodisc geodisc = MarchRay(r0, alpha0, rs, logEpsilonOverRs, rsMin, rsMax, muResolution, ref LUT);
                PrintGeodisc(geodisc);
            }
        }

        return null;
    }

    public static Geodisc MarchRay(
        float r0,
        float alpha0,
        float rs,
        float logEpsilonOverRs,
        float rsMin,
        float rsMax,
        int muResolution,
        ref Texture2D LUT)
    {
        Geodisc result = new Geodisc
        {
            hit = false,
            totalAngle = 0f,
            exitAngle = 0f,
            r0 = r0,
            r = r0,
            alpha0 = alpha0,
            alpha = alpha0,
            phi = 0f,
        };
        Debug.Log("Beginning step with analytic stepsize: " + analyticStepSize);
        for (int step = 0; step < 2048; step++)
        {
            // Hit BH
            if (result.r <= rs)
            {
                result.hit = true;
                result.exitAngle = result.phi + result.alpha;
                return result;
            }

            // Escaped
            if (result.r >= rsMax)
            {
                result.exitAngle = result.phi + result.alpha;
                return result;
            }

            float r = result.r;
            float alpha = result.alpha;

            // LUT uses folded angle coordinate
            float mu = Mathf.Abs(Mathf.Cos(alpha));

            float u = BlackHoleLutHelpers.RadiusToU(r, rs, logEpsilonOverRs, rsMin, rsMax);
            float v = BlackHoleLutHelpers.MuToV(mu, muResolution);
            float bendRate = BlackHoleLutHelpers.SampleBlackholeLUT(u, v, LUT);

            if (!float.IsFinite(bendRate))
                bendRate = 0f;

            // Euler step from old state
            float dr = Mathf.Cos(alpha) * analyticStepSize;
            float dphi = (Mathf.Sin(alpha) / Mathf.Max(r, 1e-6f)) * analyticStepSize;

            // Important: this assumes bendRate is d(alpha)/ds
            float dalpha = bendRate * analyticStepSize;

            result.r += dr;
            result.phi += dphi;
            result.alpha += dalpha;
            result.totalAngle += dalpha;

            // Optional: keep alpha in a manageable range
            result.alpha = WrapAnglePi(result.alpha);
        }

        return result;
    }

    private static float WrapAnglePi(float a)
    {
        float twoPi = 2f * Mathf.PI;
        a = (a % twoPi + twoPi) % twoPi; // [0, 2pi)

        if (a > Mathf.PI)
            a = twoPi - a; // fold into [0, pi]

        return a;
    }

    public static void PrintGeodisc(Geodisc geodisc)
    {
        Debug.Log("Geodisc " +
                  "(r0: " + geodisc.r0 + 
                  ", alpha0: " + geodisc.alpha0 + 
                  "): hit: " +  geodisc.hit +
                  ", r: " + geodisc.r +
                  ", alpha: " + geodisc.alpha +
                  ", phi: " + geodisc.phi);
    }

}
