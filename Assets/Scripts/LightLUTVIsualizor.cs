using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

[ExecuteAlways]
public class LightLUTVisualizer : MonoBehaviour
{
    [Header("Auto Sync")]
    [Tooltip("If enabled, the visualizer will pull LUT/settings from the source component every frame in editor/runtime.")]
    public bool autoSyncFromSource = true;

    [Tooltip("Optional explicit source. If left null, the visualizer will try to find a BlackHoleBending on this GameObject, then in parents, then in children.")]
    public MonoBehaviour sourceComponent;

    [Tooltip("Log a one-time warning if some source fields could not be found.")]
    public bool warnIfSourceFieldsMissing = true;

    [Header("Light LUT (Directional LUT Being Visualized)")]
    public Texture2D lut;

    [Header("Bend LUT (Used For March / Validation / Drawing)")]
    public Texture2D bendLutTexture;

    [Header("Black Hole")]
    public float rs = 1f;

    [Header("LUT Domain")]
    public int radiusResolution = 64;
    public int sunAngleResolution = 64;
    public float logEpsilonOverRs = 0.001f;
    public float rMinOverRs = 1.02f;
    public float rMaxOverRs = 100f;

    [Header("Bend LUT Domain")]
    public int bendRadiusResolution = 256;
    public int bendMuResolution = 128;

    [Header("Selected Texel")]
    public int xIndex = 0;
    public int yIndex = 0;

    [Header("Draw")]
    public int drawSteps = 8000;
    public float drawScale = 1f;
    public bool drawDirectBranch = true;
    public bool drawIndirectBranch = true;
    public bool drawSourceDirection = true;
    public bool drawPhotonSphere = true;
    public bool drawObserver = true;
    public bool drawLaunchDirection = false;

    [Header("Validation")]
    [Tooltip("Angular tolerance used by the visualizer when checking that a decoded branch really lands on the texel target.")]
    public float validationToleranceRad = 1e-4f;

    [Header("Debug")]
    public bool logDecodedValues = false;

    private bool _hasWarnedMissingFields;

    // ---------------------------------------------------------------------
    // Cache state
    // ---------------------------------------------------------------------

    private bool _cacheDirty = true;

    // BendLut cache is kept for callers that still need it, but the marcher
    // and ray-drawing paths now use the analytic formula, so BendRates being
    // null no longer causes an NRE.
    private bool _bendLutCacheValid;
    private DirectionalGeodesic2DLutSolver.BendLutData _cachedBendLut;
    private Texture2D _cachedBendLutTextureRef;

    private int _cachedXi = -1;
    private int _cachedYi = -1;
    private Texture2D _cachedLightLutRef;
    private float _cachedRs;
    private float _cachedLogEps;
    private float _cachedRMinOverRs;
    private float _cachedRMaxOverRs;
    private int _cachedDrawSteps;
    private float _cachedDrawScale;
    private float _cachedValidationTolerance;

    private float _cachedR0;
    private float _cachedThetaSun;
    private int _cachedMask;
    private float _cachedAlphaDirect;
    private float _cachedAlphaIndirect;
    private float _cachedAlphaCrit;
    private float _cachedDirectErr;
    private float _cachedIndirectErr;
    private bool _cachedDirectValid;
    private bool _cachedIndirectValid;
    private bool _cachedHasComputedData;

    private Vector3 _cachedBhPos;
    private Vector3 _cachedObserver;

    private readonly List<Vector3> _cachedDirectPoints   = new();
    private readonly List<Vector3> _cachedIndirectPoints = new();

    private void Reset()      { TryAutoAssignSource(); SyncFromSource(); MarkCacheDirty(); }
    private void OnEnable()   { TryAutoAssignSource(); SyncFromSource(); MarkCacheDirty(); }
    private void OnValidate() { TryAutoAssignSource(); SyncFromSource(); MarkCacheDirty(); }

    private void Update()
    {
        if (!autoSyncFromSource) return;

        var oldLut   = lut;
        var oldBend  = bendLutTexture;
        var oldRs    = rs;
        var oldX     = xIndex;
        var oldY     = yIndex;
        var oldLog   = logEpsilonOverRs;
        var oldRMin  = rMinOverRs;
        var oldRMax  = rMaxOverRs;
        var oldScale = drawScale;
        var oldSteps = drawSteps;
        var oldTol      = validationToleranceRad;
        var oldStepSize = DirectionalGeodesic2DLutSolver.StepSize;
        var oldStepMin  = DirectionalGeodesic2DLutSolver.StepMin;

        SyncFromSource();

        if (!Mathf.Approximately(oldStepSize, DirectionalGeodesic2DLutSolver.StepSize) ||
            !Mathf.Approximately(oldStepMin,  DirectionalGeodesic2DLutSolver.StepMin))
        {
            MarkCacheDirty();
        }

        if (oldLut  != lut              ||
            oldBend != bendLutTexture   ||
            !Mathf.Approximately(oldRs,    rs)               ||
            oldX    != xIndex           ||
            oldY    != yIndex           ||
            !Mathf.Approximately(oldLog,   logEpsilonOverRs) ||
            !Mathf.Approximately(oldRMin,  rMinOverRs)       ||
            !Mathf.Approximately(oldRMax,  rMaxOverRs)       ||
            !Mathf.Approximately(oldScale, drawScale)        ||
            oldSteps != drawSteps       ||
            !Mathf.Approximately(oldTol,   validationToleranceRad))
        {
            MarkCacheDirty();
        }
    }

    private void MarkCacheDirty()
    {
        _cacheDirty = true;
        _cachedHasComputedData = false;
    }

    // ---------------------------------------------------------------------
    // Source auto-sync
    // ---------------------------------------------------------------------

    private void TryAutoAssignSource()
    {
        if (sourceComponent != null) return;
        sourceComponent = FindLikelySource();
    }

    private MonoBehaviour FindLikelySource()
    {
        foreach (var mb in GetComponents<MonoBehaviour>())
        {
            if (mb == null || mb == this) continue;
            if (mb.GetType().Name == "BlackHoleBending" ||
                mb.GetType().Name == "BlackHoleBendLUTGenerator")
                return mb;
        }
        foreach (var mb in GetComponentsInParent<MonoBehaviour>(true))
        {
            if (mb == null || mb == this) continue;
            if (mb.GetType().Name == "BlackHoleBending" ||
                mb.GetType().Name == "BlackHoleBendLUTGenerator")
                return mb;
        }
        foreach (var mb in GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null || mb == this) continue;
            if (mb.GetType().Name == "BlackHoleBending" ||
                mb.GetType().Name == "BlackHoleBendLUTGenerator")
                return mb;
        }
        return null;
    }

    private void SyncFromSource()
    {
        if (!autoSyncFromSource) return;
        if (sourceComponent == null) TryAutoAssignSource();
        if (sourceComponent == null) return;

        bool anyMissing = false;

        TryReadAny(sourceComponent, out lut,            ref anyMissing,
            "LightLUT", "lut", "lightLut", "directionalLut", "directionalGeodesicLut", "_lut");
        TryReadAny(sourceComponent, out bendLutTexture, ref anyMissing,
            "generatedTexture", "blackHoleBendLUT", "bendLut", "bendLUT", "_bendLut", "_bendLUT");
        TryReadAny(sourceComponent, out rs,             ref anyMissing,
            "rs", "schwarzschildRadius", "_rs");
        TryReadAny(sourceComponent, out radiusResolution,    ref anyMissing,
            "lightLutRadiusResolution", "radiusResolution", "lutRadiusResolution", "_radiusResolution");
        TryReadAny(sourceComponent, out sunAngleResolution,  ref anyMissing,
            "lightLutSunAngleResolution", "sunAngleResolution", "thetaResolution", "_sunAngleResolution");
        TryReadAny(sourceComponent, out bendRadiusResolution, ref anyMissing,
            "bendRadiusResolution", "radiusResolution", "_radiusResolution");
        TryReadAny(sourceComponent, out bendMuResolution,    ref anyMissing,
            "bendMuResolution", "muResolution", "_muResolution");
        TryReadAny(sourceComponent, out logEpsilonOverRs, ref anyMissing,
            "logEpsilonOverRs", "_logEpsilonOverRs");
        TryReadAny(sourceComponent, out rMinOverRs, ref anyMissing,
            "rMinOverRs", "_rMinOverRs");
        TryReadAny(sourceComponent, out rMaxOverRs, ref anyMissing,
            "rMaxOverRs", "_rMaxOverRs");

        // Always sync step params from RayTracingManager — that's where the GPU
        // step size lives. Fall back to source component if RTM not found.
        var rtm = UnityEngine.Object.FindObjectOfType<RayTracingManager>();
        object stepSource = (object)rtm ?? sourceComponent;

        float gpuStepSize = DirectionalGeodesic2DLutSolver.StepSize;
        float gpuMinStep  = DirectionalGeodesic2DLutSolver.StepMin;

        if (TryGetMemberValue(stepSource, out float foundStepSize,
                "blackHoleSOIStepSize", "stepSize", "_stepSize") && foundStepSize > 0f)
            gpuStepSize = foundStepSize;

        if (TryGetMemberValue(stepSource, out float foundMinStep,
                "blackHoleSOIMinStep", "minStep", "_minStep") && foundMinStep > 0f)
            gpuMinStep = foundMinStep;

        DirectionalGeodesic2DLutSolver.StepSize = gpuStepSize;
        DirectionalGeodesic2DLutSolver.StepMin  = gpuMinStep;

        if (warnIfSourceFieldsMissing && anyMissing && !_hasWarnedMissingFields)
        {
            Debug.LogWarning(
                $"[LightLUTVisualizer] Some expected fields were not found on '{sourceComponent.GetType().Name}'. " +
                $"Auto-sync still works for fields that were found.", this);
            _hasWarnedMissingFields = true;
        }
    }

    // ---------------------------------------------------------------------
    // Bend LUT cache (optional — used for validation display only)
    // ---------------------------------------------------------------------

    private bool EnsureBendLutCache()
    {
        if (bendLutTexture == null)
        {
            _bendLutCacheValid = false;
            return false;
        }

        if (_bendLutCacheValid && _cachedBendLutTextureRef == bendLutTexture)
            return true;

        try
        {
            _cachedBendLut = DirectionalGeodesic2DLutSolver.CreateBendLutData(
                bendLutTexture, rs,
                bendLutTexture.width, bendLutTexture.height,
                logEpsilonOverRs, rMinOverRs, rMaxOverRs);

            _cachedBendLutTextureRef = bendLutTexture;
            _bendLutCacheValid       = _cachedBendLut.IsValid;
            return _bendLutCacheValid;
        }
        catch (Exception ex)
        {
            _bendLutCacheValid = false;
            Debug.LogWarning($"[LightLUTVisualizer] Failed to build bend LUT data: {ex.Message}", this);
            return false;
        }
    }

    private bool CacheMatchesCurrentInputs()
    {
        return _cachedHasComputedData                    &&
               _cachedLightLutRef  == lut                &&
               _cachedXi           == xIndex             &&
               _cachedYi           == yIndex             &&
               Mathf.Approximately(_cachedRs,                rs)               &&
               Mathf.Approximately(_cachedLogEps,            logEpsilonOverRs) &&
               Mathf.Approximately(_cachedRMinOverRs,        rMinOverRs)       &&
               Mathf.Approximately(_cachedRMaxOverRs,        rMaxOverRs)       &&
               _cachedDrawSteps    == drawSteps                                 &&
               Mathf.Approximately(_cachedDrawScale,         drawScale)        &&
               Mathf.Approximately(_cachedValidationTolerance, validationToleranceRad);
    }

    private void RebuildCacheIfNeeded()
    {
        if (lut == null) return;
        if (!_cacheDirty && CacheMatchesCurrentInputs()) return;

        // Bend LUT is optional — if present it's used for SampleBendRate calls,
        // but the march itself now uses the analytic formula so drawing works
        // without it.
        EnsureBendLutCache();

        _cachedDirectPoints.Clear();
        _cachedIndirectPoints.Clear();
        _cachedHasComputedData = false;

        int texWidth  = lut.width;
        int texHeight = lut.height;

        int xi = Mathf.Clamp(xIndex, 0, Mathf.Max(0, texWidth  - 1));
        int yi = Mathf.Clamp(yIndex, 0, Mathf.Max(0, texHeight - 1));

        float r0 = BlackHoleLutHelpers.IndexToRadius(
            xi, rs, texWidth, logEpsilonOverRs, rMinOverRs, rMaxOverRs);

        float tSun    = texHeight <= 1 ? 0f : yi / (float)(texHeight - 1);
        float thetaSun = (1f - tSun) * Mathf.PI;

        Color s = lut.GetPixel(xi, yi);

        int  mask             = Mathf.RoundToInt(s.b * 3f);
        bool directValidMask  = (mask & 1) != 0;
        bool indirectValidMask = (mask & 2) != 0;

        float alphaDirect   = s.r * Mathf.PI;
        float alphaIndirect = s.g * Mathf.PI;

        // March boundary: same formula as the solver, must be > r0.
        // rMax must match what the bake used for this specific r0,
        // otherwise re-marching produces different thetaRaw values and
        // validation errors are meaningless.
        float marchRMaxBase = Mathf.Max(22f * rs, rMaxOverRs * rs * 2.5f);
        float rMax          = Mathf.Max(marchRMaxBase, r0 * 2.5f);

        float alphaCrit = DirectionalGeodesic2DLutSolver.FindCriticalAlpha(r0, rs, rMax);

        ValidateDecodedAlpha(r0, alphaDirect,    thetaSun,                 rMax, out bool directValidDecoded,   out float directErr);
        ValidateDecodedAlpha(r0, alphaIndirect,  thetaSun + 2f * Mathf.PI, rMax, out bool indirectValidDecoded, out float indirectErr);

        bool directValid   = directValidMask   && directValidDecoded;
        bool indirectValid = indirectValidMask && indirectValidDecoded;

        Vector3 bhPos    = transform.position;
        Vector3 observer = bhPos + new Vector3(r0 * drawScale, 0f, 0f);

        if (directValid   && float.IsFinite(alphaDirect))
            BuildRayPoints(_cachedDirectPoints,   bhPos, r0, alphaDirect,   rMax, false);

        if (indirectValid && float.IsFinite(alphaIndirect))
            BuildRayPoints(_cachedIndirectPoints, bhPos, r0, alphaIndirect, rMax, true);

        // Write cache
        _cachedXi                  = xi;
        _cachedYi                  = yi;
        _cachedLightLutRef         = lut;
        _cachedRs                  = rs;
        _cachedLogEps              = logEpsilonOverRs;
        _cachedRMinOverRs          = rMinOverRs;
        _cachedRMaxOverRs          = rMaxOverRs;
        _cachedDrawSteps           = drawSteps;
        _cachedDrawScale           = drawScale;
        _cachedValidationTolerance = validationToleranceRad;
        _cachedR0                  = r0;
        _cachedThetaSun            = thetaSun;
        _cachedMask                = mask;
        _cachedAlphaDirect         = alphaDirect;
        _cachedAlphaIndirect       = alphaIndirect;
        _cachedAlphaCrit           = alphaCrit;
        _cachedDirectErr           = directErr;
        _cachedIndirectErr         = indirectErr;
        _cachedDirectValid         = directValid;
        _cachedIndirectValid       = indirectValid;
        _cachedBhPos               = bhPos;
        _cachedObserver            = observer;
        _cachedHasComputedData     = true;
        _cacheDirty                = false;

        if (logDecodedValues)
        {
            Debug.Log(
                $"[LightLUTVisualizer] " +
                $"source={(sourceComponent ? sourceComponent.GetType().Name : "None")} " +
                $"texel=({xi},{yi}) " +
                $"r0/rs={r0 / Mathf.Max(rs, 1e-9f):F6} " +
                $"thetaSunDeg={thetaSun * Mathf.Rad2Deg:F4} " +
                $"alphaCritDeg={alphaCrit * Mathf.Rad2Deg:F4} " +
                $"mask={mask} " +
                $"directValid={directValid} alphaDirectDeg={alphaDirect * Mathf.Rad2Deg:F6} errDeg={directErr * Mathf.Rad2Deg:F6} " +
                $"indirectValid={indirectValid} alphaIndirectDeg={alphaIndirect * Mathf.Rad2Deg:F6} errDeg={indirectErr * Mathf.Rad2Deg:F6}",
                this);

            logDecodedValues = false;
        }
    }

    // ---------------------------------------------------------------------
    // Gizmos
    // ---------------------------------------------------------------------

    private void OnDrawGizmos()
    {
        if (autoSyncFromSource) SyncFromSource();
        RebuildCacheIfNeeded();
        if (!_cachedHasComputedData) return;

        Gizmos.color = Color.black;
        Gizmos.DrawWireSphere(_cachedBhPos, rs * drawScale);

        if (drawPhotonSphere)
        {
            Gizmos.color = new Color(1f, 0.5f, 0f, 1f);
            Gizmos.DrawWireSphere(_cachedBhPos, 1.5f * rs * drawScale);
        }

        if (drawObserver)
        {
            Gizmos.color = Color.white;
            Gizmos.DrawSphere(_cachedObserver, 0.03f * drawScale * rs);
        }

        if (drawSourceDirection)
        {
            Vector3 sunDir = new Vector3(Mathf.Cos(_cachedThetaSun), Mathf.Sin(_cachedThetaSun), 0f);
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(_cachedObserver, _cachedObserver + sunDir * 5f * rs * drawScale);
        }

#if UNITY_EDITOR
        Handles.color = Color.cyan;
        Handles.Label(
            _cachedObserver + Vector3.up * (0.4f * rs * drawScale),
            $"mask={_cachedMask}\n" +
            $"acrit={_cachedAlphaCrit * Mathf.Rad2Deg:F3} deg\n" +
            $"direct err={_cachedDirectErr * Mathf.Rad2Deg:F4} deg\n" +
            $"indirect err={_cachedIndirectErr * Mathf.Rad2Deg:F4} deg\n" +
            $"stepSize={DirectionalGeodesic2DLutSolver.StepSize:F4}\n" +
            $"minStep={DirectionalGeodesic2DLutSolver.StepMin:F6}");
#endif

        if (drawDirectBranch && _cachedDirectValid && float.IsFinite(_cachedAlphaDirect))
        {
            if (drawLaunchDirection)
            {
                Gizmos.color = Color.red;
                Vector3 ld = new Vector3(Mathf.Cos(_cachedAlphaDirect), Mathf.Sin(_cachedAlphaDirect), 0f);
                Gizmos.DrawLine(_cachedObserver, _cachedObserver + ld * rs * drawScale);
            }
            DrawCachedPolyline(_cachedDirectPoints, Color.red);
        }

        if (drawIndirectBranch && _cachedIndirectValid && float.IsFinite(_cachedAlphaIndirect))
        {
            if (drawLaunchDirection)
            {
                Gizmos.color = Color.green;
                Vector3 ld = new Vector3(Mathf.Cos(_cachedAlphaIndirect), Mathf.Sin(_cachedAlphaIndirect), 0f);
                Gizmos.DrawLine(_cachedObserver, _cachedObserver + ld * rs * drawScale);
            }
            DrawCachedPolyline(_cachedIndirectPoints, Color.green);
        }
    }

    // ---------------------------------------------------------------------
    // Validation — uses analytic march, no BendLutData needed
    // ---------------------------------------------------------------------

    private void ValidateDecodedAlpha(
        float r0, float alpha, float target, float rMax,
        out bool valid, out float errorRad)
    {
        valid    = false;
        errorRad = float.PositiveInfinity;

        if (!float.IsFinite(alpha)) return;

        // Use analytic March — matches the GPU's analytic bending formula.
        bool escaped = DirectionalGeodesic2DLutSolver.March(r0, alpha, rs, rMax, out float thetaRaw);
        if (!escaped) return;

        errorRad = Mathf.Abs(thetaRaw - target);
        valid    = errorRad <= Mathf.Max(1e-7f, validationToleranceRad);
    }

    // ---------------------------------------------------------------------
    // Ray drawing — uses analytic formula, no BendLutData needed
    // ---------------------------------------------------------------------

    // Reusable polar point buffer — avoids allocating a new list every rebuild.
    private readonly List<(float r, float phi)> _polarPointBuffer = new();

    private void BuildRayPoints(
        List<Vector3> points, Vector3 bhPos,
        float r0, float alpha0, float rMax, bool indirect)
    {
        points.Clear();

        int maxSteps = indirect
            ? DirectionalGeodesic2DLutSolver.MarchMaxStepsIndirect
            : DirectionalGeodesic2DLutSolver.MarchMaxSteps;

        // Use the same March that ValidateDecodedAlpha uses — guarantees the
        // drawn path is identical to the one that was validated and baked.
        // Use analytic march — matches the GPU's ApplyBlackHoleBending which
        // now uses the analytic formula rather than the bend LUT.
        var stub = default(DirectionalGeodesic2DLutSolver.BendLutData);
        DirectionalGeodesic2DLutSolver.MarchWithPoints(
            r0, alpha0, rs, rMax,
            in stub,
            out _,
            _polarPointBuffer,
            maxPoints: Mathf.Max(drawSteps, 500),
            maxStepsOverride: maxSteps);

        foreach (var (r, phi) in _polarPointBuffer)
        {
            points.Add(bhPos + new Vector3(
                r * Mathf.Cos(phi) * drawScale,
                r * Mathf.Sin(phi) * drawScale,
                0f));
        }
    }

    // ---------------------------------------------------------------------
    // DrawCachedPolyline — fixed step/loop logic
    // ---------------------------------------------------------------------

    private void DrawCachedPolyline(List<Vector3> points, Color color)
    {
        if (points == null || points.Count < 2) return;

        Gizmos.color = color;

        // Draw at most 500 segments, evenly spaced through the point list.
        // The old code had: step = count/100; for i < count/100; i+=step
        // which drew only 1 segment when count < 100, and looped forever
        // when step==0. Now we just stride through the full list.
        int maxSegments = 500;
        int stride      = Mathf.Max(1, (points.Count - 1) / maxSegments);

        for (int i = stride; i < points.Count; i += stride)
            Gizmos.DrawLine(points[i - stride], points[i]);

        // Always draw the final segment to avoid a gap at the end.
        int last = points.Count - 1;
        if (last > 0 && (last % stride) != 0)
            Gizmos.DrawLine(points[last - 1], points[last]);
    }

    // ---------------------------------------------------------------------
    // Bend LUT sampling (mirrors the GPU shader path)
    // ---------------------------------------------------------------------

    private static float SampleBendRate(
        in DirectionalGeodesic2DLutSolver.BendLutData lut,
        float r,
        float muAbs)
    {
        // Guard: if BendRates is null the cache check should have prevented
        // reaching here, but protect against any remaining edge cases.
        if (lut.BendRates == null) return 0f;

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

    // ---------------------------------------------------------------------
    // Reflection helpers
    // ---------------------------------------------------------------------

    private static void TryReadAny<T>(object source, out T target, ref bool anyMissing, params string[] names)
    {
        if (TryGetMemberValue(source, out target, names)) return;
        anyMissing = true;
        target = default;
    }

    public static bool TryGetMemberValue<T>(object source, out T value, params string[] names)
    {
        value = default;
        foreach (string name in names)
        {
            if (!TryGetRawMemberValue(source, name, out object raw)) continue;
            if (raw == null) continue;
            try
            {
                if (raw is T exact) { value = exact; return true; }
                if (typeof(T).IsEnum)
                {
                    if (raw.GetType().IsEnum) { value = (T)Enum.ToObject(typeof(T), Convert.ToInt32(raw)); return true; }
                    if (raw is string s)      { value = (T)Enum.Parse(typeof(T), s, true); return true; }
                }
                value = (T)Convert.ChangeType(raw, typeof(T));
                return true;
            }
            catch { }
        }
        return false;
    }

    private static bool TryGetRawMemberValue(object source, string memberName, out object value)
    {
        value = null;
        if (source == null || string.IsNullOrWhiteSpace(memberName)) return false;

        Type  type  = source.GetType();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        FieldInfo field = type.GetField(memberName, flags);
        if (field != null) { value = field.GetValue(source); return true; }

        PropertyInfo prop = type.GetProperty(memberName, flags);
        if (prop != null && prop.CanRead && prop.GetIndexParameters().Length == 0)
        {
            value = prop.GetValue(source);
            return true;
        }

        return false;
    }

    private static float WrapSigned(float a)
    {
        float t = (a + Mathf.PI) % (2f * Mathf.PI);
        if (t < 0f) t += 2f * Mathf.PI;
        return t - Mathf.PI;
    }
}