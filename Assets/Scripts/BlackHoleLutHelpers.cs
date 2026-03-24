using UnityEngine;
public static class BlackHoleLutHelpers
{
    
    public static float AnalyticDThetaDs(float r, float sinAlpha, float rs)
    {
        float f = 1f - rs / r;
        if (f <= 0f) return 0f; // static-frame formula only valid outside horizon

        float bend =
            sinAlpha * (
                1f / r +
                ((1.5f * rs) - r) / (r * r * Mathf.Sqrt(f))
            );

        return float.IsFinite(bend) ? bend : 0f;
    }
    
    public static float IndexToRadius(int x, float rs, int radiusResolution, float logEpsilonOverRs, float rMinOverRs, float rMaxOverRs)
    {
        float t = radiusResolution <= 1 ? 0f : x / (float)(radiusResolution - 1);
        float eps = logEpsilonOverRs * rs;
        float rMin = rMinOverRs * rs;
        float rMax = rMaxOverRs * rs;

        float a = Mathf.Log((rMin - rs) + eps);
        float b = Mathf.Log((rMax - rs) + eps);
        float u = Mathf.Lerp(a, b, t);

        return Mathf.Max(rMin, rs + Mathf.Exp(u) - eps);
    }


    public static float IndexToMu(int y, int muResolution)
    {
        float t = muResolution <= 1 ? 0.5f : (y + 0.5f) / muResolution;
        return Mathf.Clamp01(t);
    }

    // Don't delete this, we might use it at some point
    public static float RadiusToU(
        float r,
        float rs,
        float logEpsilonOverRs,
        float rMinOverRs,
        float rMaxOverRs)
    {
        float eps = logEpsilonOverRs * rs;
        float rMin = rMinOverRs * rs;
        float rMax = rMaxOverRs * rs;

        r = Mathf.Clamp(r, rMin, rMax);

        float a = Mathf.Log((rMin - rs) + eps);
        float b = Mathf.Log((rMax - rs) + eps);
        float v = Mathf.Log((r - rs) + eps);

        return Mathf.Clamp01((v - a) / (b - a));
    }
    
    public static float MuToV(float mu, int muResolution)
    {
        float halfTexel = 0.5f / Mathf.Max(1, muResolution);
        return Mathf.Clamp(mu, halfTexel, 1f - halfTexel);
    }

    public static float SampleBlackholeLUT(float u, float v, Texture2D tex)
    {
        return tex.GetPixelBilinear(u, v).g;
    }
}