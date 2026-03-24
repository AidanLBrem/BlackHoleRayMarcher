using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

public static class DirectionalGeodesic2DLutSolver
{

    public static float StepSize        = 1.0f;          // matches blackHoleSOIStepSize
    public static float StepRadialFrac  = 1.0f;          // GPU multiplies by 1.0
    public static float StepMin         = 0.01f;         // GPU hardcoded 0.01, absolute
    public static float StepMax         = float.MaxValue; // GPU has no cap

    // Keep old names as aliases so external callers don't break.
    public static float MarchStepMax        { get => StepMax;        set => StepMax = value; }
    public static float MarchStepMin        { get => StepMin;        set => StepMin = value; }
    public static float MarchStepRadialFrac { get => StepRadialFrac; set => StepRadialFrac = value; }

    public static int MarchMaxSteps = 50_000;
    
    public static int MarchMaxStepsIndirect = 500_000;

    public static int BisectIterations = 48;

    // -------------------------------------------------------------------------
    // Bend LUT data — kept for API compatibility, no longer used by marcher
    // -------------------------------------------------------------------------

    public readonly struct BendLutData
    {
        public readonly int   RadiusResolution;
        public readonly int   MuResolution;
        public readonly float Rs;
        public readonly float RMinOverRs;
        public readonly float RMaxOverRs;
        public readonly float LogEpsilonOverRs;
        public readonly float[] BendRates;

        public BendLutData(
            int   radiusResolution,
            int   muResolution,
            float rs,
            float rMinOverRs,
            float rMaxOverRs,
            float logEpsilonOverRs,
            float[] bendRates)
        {
            RadiusResolution  = radiusResolution;
            MuResolution      = muResolution;
            Rs                = rs;
            RMinOverRs        = rMinOverRs;
            RMaxOverRs        = rMaxOverRs;
            LogEpsilonOverRs  = logEpsilonOverRs;
            BendRates         = bendRates;
        }

        public bool IsValid =>
            BendRates != null &&
            BendRates.Length == RadiusResolution * MuResolution &&
            RadiusResolution > 1 &&
            MuResolution     > 1 &&
            Rs > 0f;
    }

    /// <summary>
    /// Copies the green channel of the supplied bend LUT texture into a plain
    /// float array so it can be sampled safely from worker threads.
    /// The returned struct is only needed if external code still uses
    /// SampleBendRate; the marcher itself now uses the analytic formula.
    /// </summary>
    public static BendLutData CreateBendLutData(
        Texture2D bendLutTexture,
        float rs,
        int   radiusResolution,
        int   muResolution,
        float logEpsilonOverRs,
        float rMinOverRs,
        float rMaxOverRs)
    {
        if (bendLutTexture == null)
            throw new ArgumentNullException(nameof(bendLutTexture));

        if (bendLutTexture.width != radiusResolution || bendLutTexture.height != muResolution)
            throw new ArgumentException(
                $"Bend LUT texture size mismatch. Expected {radiusResolution}x{muResolution}, " +
                $"got {bendLutTexture.width}x{bendLutTexture.height}.");

        Color[] pixels    = bendLutTexture.GetPixels();
        float[] bendRates = new float[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
            bendRates[i] = pixels[i].g;

        return new BendLutData(
            radiusResolution, muResolution,
            rs, rMinOverRs, rMaxOverRs, logEpsilonOverRs,
            bendRates);
    }

    // -------------------------------------------------------------------------
    // Public API
    // -------------------------------------------------------------------------

    public static Texture2D BakeLut(
        BendLutData bendLut,          // kept for signature compat; not used by marcher
        int   radiusResolution,
        int   sunAngleResolution,
        float logEpsilonOverRs,
        float rMinOverRs,
        float rMaxOverRs,
        float rs = 1f)                // Schwarzschild radius — explicit, not derived from bendLut
    {
        Color[] pixels = BakeLutPixelsParallel(
            bendLut,
            radiusResolution,
            sunAngleResolution,
            logEpsilonOverRs,
            rMinOverRs,
            rMaxOverRs,
            rs);

        var tex = new Texture2D(
            radiusResolution,
            sunAngleResolution,
            TextureFormat.RGBAFloat,
            false)
        {
            wrapMode   = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name       = "DirectionalGeodesicLUT_2D"
        };

        tex.SetPixels(pixels);
        tex.Apply(false, false);
        return tex;
    }

    public static Color[] BakeLutPixelsParallel(
        BendLutData bendLut,
        int   radiusResolution,
        int   sunAngleResolution,
        float logEpsilonOverRs,
        float rMinOverRs,
        float rMaxOverRs,
        float rs = 1f)
    {
        if (radiusResolution    <= 0) throw new ArgumentOutOfRangeException(nameof(radiusResolution));
        if (sunAngleResolution  <= 0) throw new ArgumentOutOfRangeException(nameof(sunAngleResolution));
        if (rs <= 0f)                  throw new ArgumentOutOfRangeException(nameof(rs), "rs must be > 0");
        
        float rMaxObserver = rMaxOverRs * rs;
        float marchRMax    = Mathf.Max(22f * rs, rMaxObserver * 2.5f);

        Color[] pixels = new Color[radiusResolution * sunAngleResolution];

        int completedColumns = 0;

        using var cts = new CancellationTokenSource();

        Task worker = Task.Run(() =>
        {
            Parallel.For(
                0,
                radiusResolution,
                new ParallelOptions
                {
                    CancellationToken      = cts.Token,
                    MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount)
                },
                xi =>
                {
                    cts.Token.ThrowIfCancellationRequested();

                    float r0 = BlackHoleLutHelpers.IndexToRadius(
                        xi, rs, radiusResolution,
                        logEpsilonOverRs, rMinOverRs, rMaxOverRs);

                    // Per-cell march boundary: at least 2.5x the observer radius,
                    // matching the JS visualizer's dynamic rMax calculation.
                    float rMax = Mathf.Max(marchRMax, r0 * 2.5f);

                    // alphaCrit computed once per column, reused for every row.
                    // Analytic march — no bend LUT dependency.
                    float alphaCrit = FindCriticalAlpha(r0, rs, rMax);

                    for (int yi = 0; yi < sunAngleResolution; yi++)
                    {
                        float tNorm = sunAngleResolution <= 1
                            ? 0f
                            : (float)yi / (sunAngleResolution - 1);

                        float thetaSun = (1f - tNorm) * Mathf.PI;

                        bool dv = SolveDirect(
                            r0, rs, rMax, thetaSun, alphaCrit,
                            out float ad, out _);

                        bool iv = SolveIndirect(
                            r0, rs, rMax, thetaSun, alphaCrit,
                            out float ai, out _);

                        int mask = (dv ? 1 : 0) | (iv ? 2 : 0);

                        pixels[yi * radiusResolution + xi] = new Color(
                            dv ? ad / Mathf.PI : 0f,
                            iv ? ai / Mathf.PI : 0f,
                            mask / 3f,
                            1f);
                    }

                    Interlocked.Increment(ref completedColumns);
                });
        });

#if UNITY_EDITOR
        try
        {
            while (!worker.IsCompleted)
            {
                float progress = radiusResolution > 0
                    ? Mathf.Clamp01(completedColumns / (float)radiusResolution)
                    : 1f;

                bool cancel = EditorUtility.DisplayCancelableProgressBar(
                    "Baking Directional Geodesic LUT",
                    $"Computing columns {completedColumns}/{radiusResolution}",
                    progress);

                if (cancel)
                {
                    cts.Cancel();
                    break;
                }

                Thread.Sleep(33);
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
#else
        while (!worker.IsCompleted)
            Thread.Sleep(50);
#endif

        worker.Wait();

        if (cts.IsCancellationRequested)
            throw new OperationCanceledException("Directional geodesic LUT bake was canceled.");

        return pixels;
    }
    
    public static bool March(
        float r0, float alpha0,
        float rs, float rMax,
        out float thetaRaw,
        int maxStepsOverride = 0)
    {
        float r     = r0;
        float phi   = 0f;
        float theta = alpha0;
        
        float stepCapMax = StepMax < float.MaxValue ? StepMax : float.MaxValue;
        float stepMinAbs = StepMin;

        int maxSteps = maxStepsOverride > 0 ? maxStepsOverride : MarchMaxSteps;

        for (int i = 0; i < maxSteps; i++)
        {
            if (r <= rs)
            {
                thetaRaw = theta;
                return false;   // crossed horizon
            }

            if (r >= rMax)
            {
                thetaRaw = theta;
                return true;    // escaped
            }

            float localAlpha = WrapSigned(theta - phi);
            float cosA       = Mathf.Cos(localAlpha);

            if (r < rs * 1.05f && cosA < 0f)
            {
                thetaRaw = theta;
                return false;   // definitely captured
            }

            float distFromHorizon = r - rs;
            float adaptiveStep    = distFromHorizon * StepSize * StepRadialFrac;
            float h = stepCapMax < float.MaxValue
                ? Mathf.Min(stepCapMax, Mathf.Sqrt(adaptiveStep * adaptiveStep + stepMinAbs * stepMinAbs))
                : Mathf.Sqrt(adaptiveStep * adaptiveStep + stepMinAbs * stepMinAbs);

            float sinA = Mathf.Sin(localAlpha);

            // RK2 midpoint
            float bend0 = AnalyticBendRate(r, sinA, rs);

            float rM  = r     + 0.5f * h * cosA;
            float phM = phi   + 0.5f * h * sinA / Mathf.Max(r, 1e-6f);
            float thM = theta + 0.5f * h * bend0;

            float aM    = WrapSigned(thM - phM);
            float cosAM = Mathf.Cos(aM);
            float sinAM = Mathf.Sin(aM);
            float bendM = AnalyticBendRate(rM, sinAM, rs);

            r     += cosAM * h;
            phi   += sinAM / Mathf.Max(rM, 1e-6f) * h;
            theta += bendM * h;
        }

        thetaRaw = theta;
        return false;
    }

    public static bool March(
        float r0, float alpha0,
        float rs, float rMax,
        in BendLutData bendLut,
        out float thetaRaw,
        int maxStepsOverride = 0)
    {
        if (!bendLut.IsValid)
            return March(r0, alpha0, rs, rMax, out thetaRaw, maxStepsOverride);

        float r     = r0;
        float phi   = 0f;
        float theta = alpha0;

        float stepCapMax = StepMax < float.MaxValue ? StepMax : float.MaxValue;
        float stepMinAbs = StepMin; // absolute, not rs-relative

        int maxSteps = maxStepsOverride > 0 ? maxStepsOverride : MarchMaxSteps;

        for (int i = 0; i < maxSteps; i++)
        {
            if (r <= rs)   { thetaRaw = theta; return false; }
            if (r >= rMax) { thetaRaw = theta; return true;  }

            float localAlpha = WrapSigned(theta - phi);
            float cosA       = Mathf.Cos(localAlpha);

            if (r < rs * 1.05f && cosA < 0f) { thetaRaw = theta; return false; }

            float distFromHorizon = r - rs;
            float adaptiveStep    = distFromHorizon * StepSize * StepRadialFrac;
            float h = stepCapMax < float.MaxValue
                ? Mathf.Min(stepCapMax, Mathf.Sqrt(adaptiveStep * adaptiveStep + stepMinAbs * stepMinAbs))
                : Mathf.Sqrt(adaptiveStep * adaptiveStep + stepMinAbs * stepMinAbs);

            float sinA  = Mathf.Sin(localAlpha);
            float muAbs = Mathf.Abs(cosA);

            float bend0 = SampleBendRate(bendLut, r, muAbs) * Mathf.Sign(sinA);

            float rM  = r     + 0.5f * h * cosA;
            float phM = phi   + 0.5f * h * sinA / Mathf.Max(r, 1e-6f);
            float thM = theta + 0.5f * h * bend0;

            float aM    = WrapSigned(thM - phM);
            float cosAM = Mathf.Cos(aM);
            float sinAM = Mathf.Sin(aM);
            float bendM = SampleBendRate(bendLut, rM, Mathf.Abs(cosAM)) * Mathf.Sign(sinAM);

            r     += cosAM * h;
            phi   += sinAM / Mathf.Max(rM, 1e-6f) * h;
            theta += bendM * h;
        }

        thetaRaw = theta;
        return false;
    }
    
    public static bool MarchWithPoints(
        float r0, float alpha0,
        float rs, float rMax,
        out float thetaRaw,
        System.Collections.Generic.List<(float r, float phi)> points,
        int maxPoints = 4000,
        int maxStepsOverride = 0)
    {
        var stub = default(BendLutData);
        return MarchWithPoints(r0, alpha0, rs, rMax, in stub,
            out thetaRaw, points, maxPoints, maxStepsOverride);
    }

    public static bool MarchWithPoints(
        float r0, float alpha0,
        float rs, float rMax,
        in BendLutData bendLut,
        out float thetaRaw,
        System.Collections.Generic.List<(float r, float phi)> points,
        int maxPoints = 4000,
        int maxStepsOverride = 0)
    {
        points.Clear();

        float r     = r0;
        float phi   = 0f;
        float theta = alpha0;

        float stepCapMax = StepMax < float.MaxValue ? StepMax : float.MaxValue;
        float stepMinAbs = StepMin; // absolute, not rs-relative

        int maxSteps    = maxStepsOverride > 0 ? maxStepsOverride : MarchMaxSteps;
        int recordEvery = Mathf.Max(1, maxSteps / Mathf.Max(1, maxPoints));
        int stepCount   = 0;

        points.Add((r, phi));

        for (int i = 0; i < maxSteps; i++)
        {
            if (r <= rs)   { thetaRaw = theta; points.Add((r, phi)); return false; }
            if (r >= rMax) { thetaRaw = theta; points.Add((r, phi)); return true;  }

            float localAlpha = WrapSigned(theta - phi);
            float cosA       = Mathf.Cos(localAlpha);

            if (r < rs * 1.05f && cosA < 0f) { thetaRaw = theta; points.Add((r, phi)); return false; }

            float distFromHorizon = r - rs;
            float adaptiveStep    = distFromHorizon * StepSize * StepRadialFrac;
            float h = stepCapMax < float.MaxValue
                ? Mathf.Min(stepCapMax, Mathf.Sqrt(adaptiveStep * adaptiveStep + stepMinAbs * stepMinAbs))
                : Mathf.Sqrt(adaptiveStep * adaptiveStep + stepMinAbs * stepMinAbs);

            float sinA = Mathf.Sin(localAlpha);
            float bend0, bendM;

            if (bendLut.IsValid)
            {
                bend0 = SampleBendRate(bendLut, r,  Mathf.Abs(cosA)) * Mathf.Sign(sinA);
            }
            else
            {
                bend0 = AnalyticBendRate(r, sinA, rs);
            }

            float rM  = r     + 0.5f * h * cosA;
            float phM = phi   + 0.5f * h * sinA / Mathf.Max(r, 1e-6f);
            float thM = theta + 0.5f * h * bend0;

            float aM    = WrapSigned(thM - phM);
            float cosAM = Mathf.Cos(aM);
            float sinAM = Mathf.Sin(aM);

            if (bendLut.IsValid)
                bendM = SampleBendRate(bendLut, rM, Mathf.Abs(cosAM)) * Mathf.Sign(sinAM);
            else
                bendM = AnalyticBendRate(rM, sinAM, rs);

            r     += cosAM * h;
            phi   += sinAM / Mathf.Max(rM, 1e-6f) * h;
            theta += bendM * h;

            stepCount++;
            if (stepCount % recordEvery == 0)
                points.Add((r, phi));
        }

        thetaRaw = theta;
        points.Add((r, phi));
        return false;
    }

    // -------------------------------------------------------------------------
    // FIX 3: Analytic bend rate (exact Schwarzschild formula)
    // -------------------------------------------------------------------------

    /// <summary>
    /// dθ/ds for a photon in the Schwarzschild metric.
    /// Equivalent to the JS:
    ///   sa * (1/r + (1.5*rs - r) / (r*r*sqrt(1 - rs/r)))
    /// </summary>
    private static float AnalyticBendRate(float r, float sinAlpha, float rs)
    {
        float f = 1f - rs / r;
        if (f <= 0f) return 0f;
        return sinAlpha * (1f / r + (1.5f * rs - r) / (r * r * Mathf.Sqrt(f)));
    }
    
    public static float FindCriticalAlpha(float r0, float rs, float rMax)
    {
        var stub = default(BendLutData);
        return FindCriticalAlpha(r0, rs, rMax, in stub);
    }

    public static float FindCriticalAlpha(float r0, float rs, float rMax, in BendLutData bendLut)
    {
        float lo = r0 > 1.5f * rs ? Mathf.PI * 0.5f : 0.001f;
        float hi = Mathf.PI * 0.9999f;

        if (March(r0, hi, rs, rMax, in bendLut, out _))
            return hi;

        for (int i = 0; i < BisectIterations; i++)
        {
            float mid = 0.5f * (lo + hi);
            if (March(r0, mid, rs, rMax, in bendLut, out _)) lo = mid;
            else                                               hi = mid;
        }

        return 0.5f * (lo + hi);
    }
    
    public static bool SolveDirect(
        float r0, float rs, float rMax,
        float thetaSun, float alphaCrit,
        out float alphaDirect, out float errorRad)
    {
        var stub = default(BendLutData);
        return SolveDirect(r0, rs, rMax, thetaSun, alphaCrit, in stub, out alphaDirect, out errorRad);
    }

    public static bool SolveDirect(
        float r0, float rs, float rMax,
        float thetaSun, float alphaCrit,
        in BendLutData bendLut,
        out float alphaDirect, out float errorRad)
    {
        if (thetaSun < 1e-9f)
        {
            alphaDirect = 0f;
            errorRad    = 0f;
            return true;
        }

        bool ec = March(r0, alphaCrit * 0.9999f, rs, rMax, in bendLut, out float thC);
        if (!ec || thetaSun > thC)
        {
            alphaDirect = float.NaN;
            errorRad    = float.PositiveInfinity;
            return false;
        }

        return BisectRaw(
            r0, rs, rMax,
            1e-9f, alphaCrit * 0.9999f, thetaSun, in bendLut,
            out alphaDirect, out errorRad);
    }
    
    public static bool SolveIndirect(
        float r0, float rs, float rMax,
        float thetaSun, float alphaCrit,
        out float alphaIndirect, out float errorRad)
    {
        var stub = default(BendLutData);
        return SolveIndirect(r0, rs, rMax, thetaSun, alphaCrit, in stub, out alphaIndirect, out errorRad);
    }

    public static bool SolveIndirect(
        float r0, float rs, float rMax,
        float thetaSun, float alphaCrit,
        in BendLutData bendLut,
        out float alphaIndirect, out float errorRad)
    {
        float target      = thetaSun + 2f * Mathf.PI;
        int indirectSteps = MarchMaxStepsIndirect;

        bool eH = March(r0, alphaCrit * 0.99999f, rs, rMax, in bendLut, out float thH, indirectSteps);
        if (!eH || thH < target)
        {
            alphaIndirect = float.NaN;
            errorRad      = float.PositiveInfinity;
            return false;
        }

        float loA = float.NaN;
        float hiA = float.NaN;

        for (int ex10 = 5; ex10 < 90; ex10 += 2)
        {
            float alpha = alphaCrit * (1f - Mathf.Pow(10f, -ex10 / 10f));
            if (alpha <= 0f) break;

            bool esc = March(r0, alpha, rs, rMax, in bendLut, out float th, indirectSteps);
            if (!esc) continue;

            if (th < target)      loA = alpha;
            else if (!float.IsNaN(loA)) { hiA = alpha; break; }
        }

        if (float.IsNaN(loA) || float.IsNaN(hiA))
        {
            alphaIndirect = float.NaN;
            errorRad      = float.PositiveInfinity;
            return false;
        }

        return BisectRaw(
            r0, rs, rMax,
            loA, hiA, target, in bendLut,
            out alphaIndirect, out errorRad,
            indirectSteps);
    }
    
    private static bool BisectRaw(
        float r0, float rs, float rMax,
        float lo, float hi, float target,
        in BendLutData bendLut,
        out float alpha, out float errorRad,
        int maxStepsOverride = 0)
    {
        bool escLo = March(r0, lo, rs, rMax, in bendLut, out float thLo, maxStepsOverride);
        if (!escLo)
        {
            alpha    = float.NaN;
            errorRad = float.PositiveInfinity;
            return false;
        }

        float fa = thLo - target;

        for (int i = 0; i < BisectIterations; i++)
        {
            float mid = 0.5f * (lo + hi);
            bool  esc = March(r0, mid, rs, rMax, in bendLut, out float thM, maxStepsOverride);

            if (!esc) { hi = mid; continue; }

            float fm = thM - target;
            if (Mathf.Sign(fa) == Mathf.Sign(fm)) { lo = mid; fa = fm; }
            else                                     hi = mid;
        }

        alpha = 0.5f * (lo + hi);
        bool ok = March(r0, alpha, rs, rMax, in bendLut, out float thF, maxStepsOverride);
        errorRad = ok ? Mathf.Abs(thF - target) : float.PositiveInfinity;
        return ok;
    }
    
    public static float SampleBendRate(in BendLutData lut, float r, float muAbs)
    {
        float x = RadiusToPixelX(
            r, lut.Rs, lut.RadiusResolution,
            lut.LogEpsilonOverRs, lut.RMinOverRs, lut.RMaxOverRs);

        float muClamped = Mathf.Clamp(muAbs, 1e-6f, 1f - 1e-6f);
        float y         = muClamped * lut.MuResolution - 0.5f;

        int x0 = Mathf.Clamp(Mathf.FloorToInt(x), 0, lut.RadiusResolution - 1);
        int x1 = Mathf.Clamp(x0 + 1,              0, lut.RadiusResolution - 1);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(y), 0, lut.MuResolution - 1);
        int y1 = Mathf.Clamp(y0 + 1,              0, lut.MuResolution - 1);

        float tx = Mathf.Clamp01(x - x0);
        float ty = Mathf.Clamp01(y - y0);

        float v00 = lut.BendRates[y0 * lut.RadiusResolution + x0];
        float v10 = lut.BendRates[y0 * lut.RadiusResolution + x1];
        float v01 = lut.BendRates[y1 * lut.RadiusResolution + x0];
        float v11 = lut.BendRates[y1 * lut.RadiusResolution + x1];

        return Mathf.Lerp(
            Mathf.Lerp(v00, v10, tx),
            Mathf.Lerp(v01, v11, tx),
            ty);
    }

    private static float RadiusToPixelX(
        float r, float rs, int radiusResolution,
        float logEpsilonOverRs, float rMinOverRs, float rMaxOverRs)
    {
        float eps  = logEpsilonOverRs * rs;
        float rMin = rMinOverRs * rs;
        float rMax = rMaxOverRs * rs;

        float a = Mathf.Log(Mathf.Max(1e-20f, (rMin - rs) + eps));
        float b = Mathf.Log(Mathf.Max(1e-20f, (rMax - rs) + eps));
        float v = Mathf.Log(Mathf.Max(1e-20f, (Mathf.Clamp(r, rMin, rMax) - rs) + eps));

        return Mathf.InverseLerp(a, b, v) * (radiusResolution - 1);
    }

    // -------------------------------------------------------------------------
    // Utility
    // -------------------------------------------------------------------------

    private static float WrapSigned(float a)
    {
        float t = (a + Mathf.PI) % (2f * Mathf.PI);
        if (t < 0f) t += 2f * Mathf.PI;
        return t - Mathf.PI;
    }
}