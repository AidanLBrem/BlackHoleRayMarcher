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

    public Texture2D generatedTexture;
    public bool regenerate = false;

    // -------------------------------------------------------------------------
    public Texture2D blackHoleBendLUT;
    private void OnValidate()
    {
        if (regenerate)
        {
            schwarzschildRadius = transform.localScale.x;
            Generate();
            regenerate = false;
            Texture2D tex = new Texture2D(radiusResolution, radiusResolution, TextureFormat.RGBAFloat, false, true);
            GeoDiscSolver.SolveGeodisc(schwarzschildRadius, 10, 10, rMinOverRs, rMinOverRs, logEpsilonOverRs, ref tex);
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
            float mu       = BlackHoleLutHelpers.IndexToMu(y, muResolution);
            float sinAlpha = Mathf.Sqrt(Mathf.Max(0f, 1f - mu * mu));

            for (int x = 0; x < radiusResolution; x++)
            {
                float r    = BlackHoleLutHelpers.IndexToRadius(x, rs, radiusResolution, logEpsilonOverRs, rMinOverRs, rMaxOverRs);
                float bend = BlackHoleLutHelpers.AnalyticDThetaDs(r, sinAlpha, rs);

                if (!float.IsFinite(bend) || bend < 0f)
                    bend = 0f;

                bendValues[x, y] = bend;
                maxBend = Mathf.Max(maxBend, bend);
            }
        }

        if (maxBend <= 0f) maxBend = 1f;
        // After the loop, before SetPixel
        Debug.Log($"y=0 mu={BlackHoleLutHelpers.IndexToMu(0, muResolution):F3} sinAlpha={Mathf.Sqrt(1f - BlackHoleLutHelpers.IndexToMu(0, muResolution)*BlackHoleLutHelpers.IndexToMu(0, muResolution)):F3} bend[0,0]={bendValues[0,0]:E4}");
        Debug.Log($"y=127 mu={BlackHoleLutHelpers.IndexToMu(127, muResolution):F3} sinAlpha={Mathf.Sqrt(1f - BlackHoleLutHelpers.IndexToMu(127, muResolution)*BlackHoleLutHelpers.IndexToMu(127, muResolution)):F3} bend[0,127]={bendValues[0,127]:E4}");
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


    private void ValidateSettings()
    {
        schwarzschildRadius = Mathf.Max(1e-12f, schwarzschildRadius);
        radiusResolution    = Mathf.Max(8,     radiusResolution);
        muResolution        = Mathf.Max(8,     muResolution);
        rMinOverRs          = Mathf.Max(1.0001f, rMinOverRs);
        rMaxOverRs          = Mathf.Max(rMinOverRs + 0.001f, rMaxOverRs);
        logEpsilonOverRs    = Mathf.Max(1e-12f, logEpsilonOverRs);
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
            float mu  = BlackHoleLutHelpers.IndexToMu(yy, muResolution);
            string line = $"mu={mu:F3} : ";

            foreach (int x in sampleX)
            {
                int   xx   = Mathf.Clamp(x, 0, radiusResolution - 1);
                float r    = BlackHoleLutHelpers.IndexToRadius(xx, rs, radiusResolution, logEpsilonOverRs, rMinOverRs, rMaxOverRs);
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
