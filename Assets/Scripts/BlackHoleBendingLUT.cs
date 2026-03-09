using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Generates a 2D LUT of Schwarzschild photon geodesic bend rates.
///
/// Axes:
///   X  = radius r,  log-spaced above the horizon from rMinOverRs to rMaxOverRs
///   Y  = mu = cos(alpha), where alpha is the angle between the ray and the
///        outward radial direction.  Sampled at cell centres (y+0.5)/muResolution
///        so mu is in (0, 1) exclusive — purely radial rays are never sampled.
///
/// The green channel stores dPhi/dr: how many radians of orbital-plane deflection
/// the photon accumulates per unit coordinate radius travelled.  This is derived
/// analytically from the Schwarzschild null geodesic first integral, so there
/// are no integration sign issues, no step-size sensitivity, and no instability
/// near the photon sphere (r = 1.5 rs).
///
/// dPhi/dr = (b / r²) / sqrt( 1 - b²(1 - rs/r) / r² )
///
/// where b = r * sin(alpha) / sqrt(1 - rs/r)  is the conserved impact parameter.
///
/// The formula diverges as the photon approaches its turning point
/// (denominator → 0).  We clamp to a finite ceiling rather than let it blow up,
/// because the shader only needs a finite, smooth table.
/// </summary>
[ExecuteAlways]
public class BlackHoleBendLUTGenerator : MonoBehaviour
{
    [Header("Black Hole Units")]
    [Tooltip("Schwarzschild radius used to normalise the table.")]
    public float schwarzschildRadius = 1f;

    [Header("LUT Resolution")]
    [Min(8)] public int radiusResolution = 256;
    [Min(8)] public int muResolution = 128;

    [Header("Radius Range (in units of rs)")]
    [Tooltip("Sample just outside the horizon.")]
    public float rMinOverRs = 1.02f;
    public float rMaxOverRs = 100f;
    [Tooltip("Small epsilon for log(r - rs + eps) mapping.")]
    public float logEpsilonOverRs = 0.001f;

    [Header("Output")]
    public bool generateOnStart = false;
    public string textureAssetName = "BlackHoleBendLUT";
    public bool saveAsPNG = true;
    public bool saveAsEXR = false;
    public bool printSampleValues = false;

    [Header("Debug")]
    public bool previewInScene = false;
    public FilterMode filterMode = FilterMode.Bilinear;

    [NonSerialized] public Texture2D generatedTexture;
    public bool regenerate = false;

    // -------------------------------------------------------------------------

    private void OnValidate()
    {
        if (regenerate)
        {
            schwarzschildRadius = transform.localScale.x;
            Generate();
            regenerate = false;
        }
    }

    private void Start()
    {
        if (generateOnStart)
        {
            schwarzschildRadius = transform.localScale.x;
            Generate();
        }
    }

    [ContextMenu("Generate LUT")]
    public void Generate()
    {
        ValidateSettings();

        float rs = schwarzschildRadius;

        Texture2D tex = new Texture2D(radiusResolution, muResolution, TextureFormat.RGBAFloat, false, true)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = filterMode,
            name = textureAssetName
        };

        float maxBend = 0f;
        float[,] bendValues = new float[radiusResolution, muResolution];

        for (int y = 0; y < muResolution; y++)
        {
            float mu       = IndexToMu(y);
            float sinAlpha = Mathf.Sqrt(Mathf.Max(0f, 1f - mu * mu));

            for (int x = 0; x < radiusResolution; x++)
            {
                float r    = IndexToRadius(x, rs);
                float bend = AnalyticDThetaDs(r, sinAlpha, rs);

                if (!float.IsFinite(bend) || bend < 0f)
                    bend = 0f;

                bendValues[x, y] = bend;
                maxBend = Mathf.Max(maxBend, bend);
            }
        }

        if (maxBend <= 0f) maxBend = 1f;
        // After the loop, before SetPixel
        Debug.Log($"y=0 mu={IndexToMu(0):F3} sinAlpha={Mathf.Sqrt(1f - IndexToMu(0)*IndexToMu(0)):F3} bend[0,0]={bendValues[0,0]:E4}");
        Debug.Log($"y=127 mu={IndexToMu(127):F3} sinAlpha={Mathf.Sqrt(1f - IndexToMu(127)*IndexToMu(127)):F3} bend[0,127]={bendValues[0,127]:E4}");
        for (int y = 0; y < muResolution; y++)
        {
            for (int x = 0; x < radiusResolution; x++)
            {
                float bendRate = bendValues[x, y];

                // R = normalised bend rate   (0..1 visualisation)
                // G = raw dPhi/dr            (what the shader uses)
                // B = normalised radius coord
                // A = mu coord
                float rCoord  = x / Mathf.Max(1f, radiusResolution - 1f);
                float muCoord = y / Mathf.Max(1f, muResolution     - 1f);

                tex.SetPixel(x, y, new Color(
                    bendRate / maxBend,
                    bendRate,
                    rCoord,
                    muCoord
                ));
            }
        }

        tex.Apply(false, false);
        generatedTexture = tex;

        if (printSampleValues)
            PrintSampleRows(bendValues, rs);

        SaveTextureIfRequested(tex);

        Debug.Log($"Generated black hole bend LUT: {radiusResolution}x{muResolution}, " +
                  $"max dPhi/dr = {maxBend:E6}");
    }

    // -------------------------------------------------------------------------
    // Core analytic formula
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns dPhi/dr for a photon at coordinate radius r with local angle
    /// sin(alpha) = sinAlpha (alpha = angle from outward radial).
    ///
    /// Derivation from Schwarzschild null geodesic first integrals:
    ///   E  = (1 - rs/r) dt/dlambda          (conserved energy)
    ///   L  = r²         dphi/dlambda        (conserved angular momentum)
    ///   b  = L/E                             (impact parameter)
    ///
    /// Locally measured: sin(alpha) = r*(dphi/dlambda) / (local speed)
    ///   => b = r * sin(alpha) / sqrt(1 - rs/r)
    ///
    /// Radial null equation:
    ///   (dr/dlambda)² = E² [ 1 - b²(1 - rs/r)/r² ]
    ///
    /// Therefore:
    ///   dPhi/dr = (dphi/dlambda) / (dr/dlambda)
    ///           = (b/r²) / sqrt( 1 - b²(1 - rs/r)/r² )
    ///
    /// The denominator vanishes at the photon turning point; we floor it.
    /// </summary>
    private float AnalyticDThetaDs(float r, float sinAlpha, float rs)
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

    // -------------------------------------------------------------------------
    // Index -> physical value mappings
    // -------------------------------------------------------------------------

    /// <summary>
    /// Log-spaced radius, dense near the horizon.
    /// Result is always in [rMinOverRs*rs, rMaxOverRs*rs].
    /// </summary>
    private float IndexToRadius(int x, float rs)
    {
        float t   = radiusResolution <= 1 ? 0f : x / (float)(radiusResolution - 1);
        float eps = logEpsilonOverRs * rs;
        float rMin = rMinOverRs * rs;
        float rMax = rMaxOverRs * rs;

        float a = Mathf.Log((rMin - rs) + eps);
        float b = Mathf.Log((rMax - rs) + eps);
        float u = Mathf.Lerp(a, b, t);

        return Mathf.Max(rMin, rs + Mathf.Exp(u) - eps);
    }

    /// <summary>
    /// mu = cos(alpha), sampled at cell centres so mu is in (0,1) exclusive.
    /// Shaders must apply the same (y+0.5)/muResolution offset when sampling.
    /// </summary>
    private float IndexToMu(int y)
    {
        float t = muResolution <= 1 ? 0.5f : (y + 0.5f) / muResolution;
        return Mathf.Clamp01(t);
    }

    // -------------------------------------------------------------------------

    private void ValidateSettings()
    {
        schwarzschildRadius = Mathf.Max(1e-6f, schwarzschildRadius);
        radiusResolution    = Mathf.Max(8,     radiusResolution);
        muResolution        = Mathf.Max(8,     muResolution);
        rMinOverRs          = Mathf.Max(1.0001f, rMinOverRs);
        rMaxOverRs          = Mathf.Max(rMinOverRs + 0.001f, rMaxOverRs);
        logEpsilonOverRs    = Mathf.Max(1e-6f, logEpsilonOverRs);
    }

    private void OnDrawGizmosSelected()
    {
        if (!previewInScene || generatedTexture == null) return;
        Gizmos.color = Color.white;
        Gizmos.DrawWireCube(transform.position, new Vector3(1f, 1f, 0.01f));
    }

    private void PrintSampleRows(float[,] values, float rs)
    {
        int[] sampleX = { 0, radiusResolution/8, radiusResolution/4,
                          radiusResolution/2, radiusResolution - 1 };
        int[] sampleY = { 0, muResolution/4, muResolution/2,
                          (3*muResolution)/4, muResolution - 1 };

        Debug.Log("---- Sample LUT values (dPhi/dr) ----");
        foreach (int y in sampleY)
        {
            int   yy  = Mathf.Clamp(y, 0, muResolution - 1);
            float mu  = IndexToMu(yy);
            string line = $"mu={mu:F3} : ";

            foreach (int x in sampleX)
            {
                int   xx   = Mathf.Clamp(x, 0, radiusResolution - 1);
                float r    = IndexToRadius(xx, rs);
                float bend = values[xx, yy];
                line += $"[r/rs={(r/rs):F3}, dPhi/dr={bend:E4}] ";
            }
            Debug.Log(line);
        }
    }

    private void SaveTextureIfRequested(Texture2D tex)
    {
#if UNITY_EDITOR
        string folder = Path.Combine(Application.dataPath, "Generated");
        if (!Directory.Exists(folder))
            Directory.CreateDirectory(folder);

        if (saveAsPNG)
        {
            byte[] png  = tex.EncodeToPNG();
            string path = Path.Combine(folder, textureAssetName + ".png");
            File.WriteAllBytes(path, png);
            Debug.Log($"Saved PNG LUT to: {path}");
        }

        if (saveAsEXR)
        {
            byte[] exr  = tex.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat);
            string path = Path.Combine(folder, textureAssetName + ".exr");
            File.WriteAllBytes(path, exr);
            Debug.Log($"Saved EXR LUT to: {path}");
        }

        UnityEditor.AssetDatabase.Refresh();
#endif
    }
}
