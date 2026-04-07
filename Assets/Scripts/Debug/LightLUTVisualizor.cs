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
    // =========================================================================
    //  Inspector
    // =========================================================================

    [Header("━━  Auto Sync  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━")]
    [Tooltip("Pull LUT settings from the source component every frame.")]
    public bool autoSyncFromSource = true;

    [Tooltip("Leave null to auto-discover BlackHoleBending / BlackHoleBendLUTGenerator on this or parent/child GameObjects.")]
    public MonoBehaviour sourceComponent;

    [Tooltip("Log a one-time warning when expected source fields are missing.")]
    public bool warnIfSourceFieldsMissing = true;

    [Space(4)]
    [Header("━━  LUT Textures  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━")]
    [Tooltip("The 4-channel directional geodesic LUT.\n" +
             "R=direct  G=indirect1  B=indirect2  A=indirect3\n" +
             "Channel >= 0 → valid (value = α/π);  -1 → no solution.")]
    public Texture2D lut;

    [Tooltip("Optional bend LUT texture — used only for legacy validation paths.")]
    public Texture2D bendLutTexture;

    [Space(4)]
    [Header("━━  Black Hole  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━")]
    public float rs = 1f;

    [Space(4)]
    [Header("━━  LUT Domain  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━")]
    public int   radiusResolution   = 64;
    public int   sunAngleResolution = 64;
    public float logEpsilonOverRs   = 0.001f;
    public float rMinOverRs         = 1.02f;
    public float rMaxOverRs         = 100f;

    [Space(2)]
    [Tooltip("Dimensions of the optional bend LUT texture.")]
    public int bendRadiusResolution = 256;
    public int bendMuResolution     = 128;

    [Space(4)]
    [Header("━━  Selected Texel  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━")]
    [Tooltip("Column index → observer radius.")]
    public int xIndex = 0;
    [Tooltip("Row index → sun angle (row 0 = θ=π, top row = θ=0).")]
    public int yIndex = 0;

    [Space(4)]
    [Header("━━  Draw  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━")]
    public float drawScale = 1f;
    public int   drawSteps = 8000;

    [Space(2)]
    [Tooltip("Draw the direct geodesic (red).")]
    public bool drawDirect    = true;
    [Tooltip("Draw the 1st-order indirect geodesic — one extra half-orbit (green).")]
    public bool drawIndirect1 = true;
    [Tooltip("Draw the 2nd-order indirect geodesic — two extra half-orbits (cyan).")]
    public bool drawIndirect2 = true;
    [Tooltip("Draw the 3rd-order indirect geodesic — three extra half-orbits (magenta).")]
    public bool drawIndirect3 = true;

    [Space(2)]
    [Tooltip("Draw a naïve ray launched at exactly θ_sun (the unscattered direction) through the gravitational field. " +
             "Shows where a flat-space NEE ray actually ends up after bending — vs where the LUT says the sun is.")]
    public bool drawNaiveRay = true;

    [Space(2)]
    public bool drawSourceDirection  = true;
    public bool drawPhotonSphere     = true;
    public bool drawObserver         = true;
    public bool drawLaunchDirections = false;

    [Space(4)]
    [Header("━━  Validation  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━")]
    [Tooltip("A re-marched ray is considered valid when its angular error is below this threshold.")]
    public float validationToleranceRad = 1e-4f;

    [Space(4)]
    [Header("━━  Debug  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━")]
    [Tooltip("Log decoded LUT values for the selected texel once, then reset.")]
    public bool logDecodedValues = false;

    // =========================================================================
    //  Private state
    // =========================================================================

    private bool _hasWarnedMissingFields;
    private bool _cacheDirty = true;

    private bool                                          _bendLutCacheValid;
    private DirectionalGeodesic2DLutSolver.BendLutData   _cachedBendLut;
    private Texture2D                                     _cachedBendLutTextureRef;

    private int       _cachedXi, _cachedYi;
    private Texture2D _cachedLightLutRef;
    private float     _cachedRs, _cachedLogEps, _cachedRMinOverRs, _cachedRMaxOverRs;
    private int       _cachedDrawSteps;
    private float     _cachedDrawScale, _cachedValidationTolerance;

    private bool  _cachedHasComputedData;
    private float _cachedR0, _cachedThetaSun, _cachedAlphaCrit;


    private readonly float[] _cachedAlpha = new float[4];
    private readonly float[] _cachedErr   = new float[4];
    private readonly bool[]  _cachedValid = new bool[4];
    
    private float _cachedNaiveAlpha;         // launch angle = thetaSun
    private float _cachedNaiveThetaRaw;      // where the naïve ray actually ends up
    private bool  _cachedNaiveEscaped;       // did it escape or get captured?
    private float _cachedNaiveErrDirect;     // |naïve arrival - thetaSun| — how wrong NEE is for direct
    private float _cachedNaiveErrIndirect;   // same vs indirect1 target

    private Vector3 _cachedBhPos, _cachedObserver;

    private readonly List<Vector3>[] _cachedPoints = new[]
    {
        new List<Vector3>(),  // direct
        new List<Vector3>(),  // indirect 1
        new List<Vector3>(),  // indirect 2
        new List<Vector3>(),  // indirect 3
    };

    // Naïve ray marched path
    private readonly List<Vector3> _cachedNaivePoints = new();

    private readonly List<(float r, float phi)> _polarBuf = new();

    private static readonly Color[] BranchColours =
    {
        new Color(1.00f, 0.25f, 0.25f),   // red    — direct
        new Color(0.25f, 1.00f, 0.35f),   // green  — indirect1
        new Color(0.25f, 0.85f, 1.00f),   // cyan   — indirect2
        new Color(1.00f, 0.30f, 1.00f),   // magenta — indirect3
    };

    private static readonly Color ColNaive = new Color(1.00f, 1.00f, 0.20f, 0.55f); // dim yellow

    private static readonly string[] BranchLabels =
        { "direct", "indirect₁", "indirect₂", "indirect₃" };
    
    private void Reset()      { TryAutoAssignSource(); SyncFromSource(); MarkCacheDirty(); }
    private void OnEnable()   { TryAutoAssignSource(); SyncFromSource(); MarkCacheDirty(); }
    private void OnValidate() { TryAutoAssignSource(); SyncFromSource(); MarkCacheDirty(); }

    private void Update()
    {
        if (!autoSyncFromSource) return;

        var   oldLut      = lut;
        var   oldBend     = bendLutTexture;
        float oldRs       = rs;
        int   oldX        = xIndex, oldY = yIndex;
        float oldLog      = logEpsilonOverRs;
        float oldRMin     = rMinOverRs, oldRMax = rMaxOverRs;
        float oldScale    = drawScale;
        int   oldSteps    = drawSteps;
        float oldTol      = validationToleranceRad;
        float oldStepSize = DirectionalGeodesic2DLutSolver.StepSize;
        float oldStepMin  = DirectionalGeodesic2DLutSolver.StepMin;

        SyncFromSource();

        bool dirty =
            oldLut   != lut            || oldBend  != bendLutTexture   ||
            oldX     != xIndex         || oldY     != yIndex            ||
            oldSteps != drawSteps      ||
            !Mathf.Approximately(oldRs,       rs)                       ||
            !Mathf.Approximately(oldLog,      logEpsilonOverRs)         ||
            !Mathf.Approximately(oldRMin,     rMinOverRs)               ||
            !Mathf.Approximately(oldRMax,     rMaxOverRs)               ||
            !Mathf.Approximately(oldScale,    drawScale)                ||
            !Mathf.Approximately(oldTol,      validationToleranceRad)   ||
            !Mathf.Approximately(oldStepSize, DirectionalGeodesic2DLutSolver.StepSize) ||
            !Mathf.Approximately(oldStepMin,  DirectionalGeodesic2DLutSolver.StepMin);

        if (dirty) MarkCacheDirty();
    }

    private void MarkCacheDirty()
    {
        _cacheDirty            = true;
        _cachedHasComputedData = false;
    }

    // =========================================================================
    //  Source auto-sync
    // =========================================================================

    private void TryAutoAssignSource()
    {
        if (sourceComponent != null) return;
        sourceComponent = FindLikelySource();
    }

    private MonoBehaviour FindLikelySource()
    {
        foreach (var mb in GetComponents<MonoBehaviour>())
            if (mb != null && mb != this && IsKnownSourceType(mb)) return mb;
        foreach (var mb in GetComponentsInParent<MonoBehaviour>(true))
            if (mb != null && mb != this && IsKnownSourceType(mb)) return mb;
        foreach (var mb in GetComponentsInChildren<MonoBehaviour>(true))
            if (mb != null && mb != this && IsKnownSourceType(mb)) return mb;
        return null;
    }

    private static bool IsKnownSourceType(MonoBehaviour mb)
    {
        string n = mb.GetType().Name;
        return n == "BlackHoleBending" || n == "BlackHoleBendLUTGenerator";
    }

    private void SyncFromSource()
    {
        if (!autoSyncFromSource) return;
        if (sourceComponent == null) TryAutoAssignSource();
        if (sourceComponent == null) return;

        bool anyMissing = false;

        TryReadAny(sourceComponent, out lut,                 ref anyMissing,
            "LightLUT", "lut", "lightLut", "directionalLut", "directionalGeodesicLut", "_lut");
        TryReadAny(sourceComponent, out bendLutTexture,      ref anyMissing,
            "generatedTexture", "blackHoleBendLUT", "bendLut", "bendLUT", "_bendLut", "_bendLUT");
        TryReadAny(sourceComponent, out rs,                  ref anyMissing,
            "rs", "schwarzschildRadius", "_rs");
        TryReadAny(sourceComponent, out radiusResolution,    ref anyMissing,
            "lightLutRadiusResolution", "radiusResolution", "lutRadiusResolution", "_radiusResolution");
        TryReadAny(sourceComponent, out sunAngleResolution,  ref anyMissing,
            "lightLutSunAngleResolution", "sunAngleResolution", "thetaResolution", "_sunAngleResolution");
        TryReadAny(sourceComponent, out bendRadiusResolution, ref anyMissing,
            "bendRadiusResolution", "radiusResolution", "_radiusResolution");
        TryReadAny(sourceComponent, out bendMuResolution,    ref anyMissing,
            "bendMuResolution", "muResolution", "_muResolution");
        TryReadAny(sourceComponent, out logEpsilonOverRs,    ref anyMissing,
            "logEpsilonOverRs", "_logEpsilonOverRs");
        TryReadAny(sourceComponent, out rMinOverRs,          ref anyMissing,
            "rMinOverRs", "_rMinOverRs");
        TryReadAny(sourceComponent, out rMaxOverRs,          ref anyMissing,
            "rMaxOverRs", "_rMaxOverRs");

        var    rtm        = FindObjectOfType<RayTracingManager>();
        object stepSource = (object)rtm ?? sourceComponent;

        float gpuStepSize = DirectionalGeodesic2DLutSolver.StepSize;
        float gpuMinStep  = DirectionalGeodesic2DLutSolver.StepMin;

        if (TryGetMemberValue(stepSource, out float foundStepSize,
                "stepSize", "blackHoleSOIStepSize", "_stepSize") && foundStepSize > 0f)
            gpuStepSize = foundStepSize;

        if (TryGetMemberValue(stepSource, out float foundMinStep,
                "minStep", "blackHoleSOIMinStep", "_minStep") && foundMinStep > 0f)
            gpuMinStep = foundMinStep;

        DirectionalGeodesic2DLutSolver.StepSize = gpuStepSize;
        DirectionalGeodesic2DLutSolver.StepMin  = gpuMinStep;

        if (warnIfSourceFieldsMissing && anyMissing && !_hasWarnedMissingFields)
        {
            Debug.LogWarning(
                $"[LightLUTVisualizer] Some fields not found on " +
                $"'{sourceComponent.GetType().Name}'. Sync still works for found fields.", this);
            _hasWarnedMissingFields = true;
        }
    }
    private bool EnsureBendLutCache()
    {
        if (bendLutTexture == null) { _bendLutCacheValid = false; return false; }
        if (_bendLutCacheValid && _cachedBendLutTextureRef == bendLutTexture) return true;

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
            Debug.LogWarning($"[LightLUTVisualizer] Bend LUT build failed: {ex.Message}", this);
            return false;
        }
    }
    
    private bool CacheMatchesCurrentInputs() =>
        _cachedHasComputedData                                               &&
        _cachedLightLutRef == lut                                            &&
        _cachedXi == xIndex && _cachedYi == yIndex                          &&
        Mathf.Approximately(_cachedRs,                  rs)                 &&
        Mathf.Approximately(_cachedLogEps,              logEpsilonOverRs)   &&
        Mathf.Approximately(_cachedRMinOverRs,          rMinOverRs)         &&
        Mathf.Approximately(_cachedRMaxOverRs,          rMaxOverRs)         &&
        _cachedDrawSteps == drawSteps                                        &&
        Mathf.Approximately(_cachedDrawScale,           drawScale)          &&
        Mathf.Approximately(_cachedValidationTolerance, validationToleranceRad);

    private void RebuildCacheIfNeeded()
    {
        if (lut == null) return;
        if (!_cacheDirty && CacheMatchesCurrentInputs()) return;

        EnsureBendLutCache();

        foreach (var list in _cachedPoints) list.Clear();
        _cachedHasComputedData = false;

        int texW = lut.width;
        int texH = lut.height;
        int xi   = Mathf.Clamp(xIndex, 0, Mathf.Max(0, texW - 1));
        int yi   = Mathf.Clamp(yIndex, 0, Mathf.Max(0, texH - 1));

        float r0       = BlackHoleLutHelpers.IndexToRadius(xi, rs, texW, logEpsilonOverRs, rMinOverRs, rMaxOverRs);
        float tSun     = texH <= 1 ? 0f : yi / (float)(texH - 1);
        float thetaSun = (1f - tSun) * Mathf.PI;

        Color   s           = lut.GetPixel(xi, yi);
        float[] rawChannels = { s.r, s.g, s.b, s.a };

        float marchRMaxBase = Mathf.Max(22f * rs, rMaxOverRs * rs * 2.5f);
        float rMax          = Mathf.Max(marchRMaxBase, r0 * 2.5f);

        float alphaCrit = DirectionalGeodesic2DLutSolver.FindCriticalAlpha(r0, rs, rMax);

        Vector3 bhPos    = transform.position;
        Vector3 observer = bhPos + new Vector3(r0 * drawScale, 0f, 0f);

        var stub = default(DirectionalGeodesic2DLutSolver.BendLutData);

        // ── Geodesic branches ────────────────────────────────────────────────
        for (int b = 0; b < 4; b++)
        {
            float channelVal = rawChannels[b];
            bool  lutValid   = channelVal >= 0f;
            float alpha      = lutValid ? channelVal * Mathf.PI : float.NaN;
            float target     = thetaSun + b * 2f * Mathf.PI;

            bool  decoded = false;
            float err     = float.PositiveInfinity;

            if (lutValid && float.IsFinite(alpha))
                ValidateDecodedAlpha(r0, alpha, target, rMax, out decoded, out err);

            _cachedAlpha[b] = alpha;
            _cachedErr[b]   = err;
            _cachedValid[b] = lutValid && decoded;

            if (_cachedValid[b])
                BuildRayPoints(_cachedPoints[b], bhPos, r0, alpha, rMax, indirect: b > 0);
        }
        
        _cachedNaiveAlpha = thetaSun;
        _cachedNaivePoints.Clear();

        _cachedNaiveEscaped = DirectionalGeodesic2DLutSolver.MarchWithPoints(
            r0, thetaSun, rs, rMax,
            in stub,
            out _cachedNaiveThetaRaw,
            _polarBuf,
            maxPoints: Mathf.Max(drawSteps, 500),
            maxStepsOverride: DirectionalGeodesic2DLutSolver.MarchMaxSteps);

        foreach (var (nr, nphi) in _polarBuf)
            _cachedNaivePoints.Add(bhPos + new Vector3(
                nr * Mathf.Cos(nphi) * drawScale,
                nr * Mathf.Sin(nphi) * drawScale, 0f));


        if (_cachedNaiveEscaped)
        {
            _cachedNaiveErrDirect   = Mathf.Abs(_cachedNaiveThetaRaw - thetaSun);
            _cachedNaiveErrIndirect = _cachedValid[1]
                ? Mathf.Abs(_cachedNaiveThetaRaw - (thetaSun + 2f * Mathf.PI))
                : float.NaN;
        }
        else
        {
            _cachedNaiveErrDirect   = float.PositiveInfinity;  // captured — NEE completely wrong
            _cachedNaiveErrIndirect = float.PositiveInfinity;
        }

        // Write cache bookkeeping
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
        _cachedAlphaCrit           = alphaCrit;
        _cachedBhPos               = bhPos;
        _cachedObserver            = observer;
        _cachedHasComputedData     = true;
        _cacheDirty                = false;

        if (logDecodedValues)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append($"[LightLUTVisualizer] texel=({xi},{yi})  " +
                      $"r0/rs={r0 / Mathf.Max(rs, 1e-9f):F6}  " +
                      $"θ_sun={thetaSun * Mathf.Rad2Deg:F3}°  " +
                      $"α_crit={alphaCrit * Mathf.Rad2Deg:F3}°\n");
            sb.Append($"  [naïve NEE]  launch={_cachedNaiveAlpha * Mathf.Rad2Deg:F4}°  " +
                      $"escaped={_cachedNaiveEscaped}  " +
                      $"arrives={_cachedNaiveThetaRaw * Mathf.Rad2Deg:F4}°  " +
                      $"NEE_err_direct={_cachedNaiveErrDirect * Mathf.Rad2Deg:F4}°  " +
                      $"NEE_err_indirect1={_cachedNaiveErrIndirect * Mathf.Rad2Deg:F4}°\n");
            for (int b = 0; b < 4; b++)
            {
                sb.Append($"  [{BranchLabels[b]}]  " +
                          $"channel={rawChannels[b]:F5}  " +
                          $"valid={_cachedValid[b]}  " +
                          $"α={_cachedAlpha[b] * Mathf.Rad2Deg:F4}°  " +
                          $"err={_cachedErr[b] * Mathf.Rad2Deg:F5}°\n");
            }
            Debug.Log(sb.ToString(), this);
            logDecodedValues = false;
        }
    }
    private void OnDrawGizmos()
    {
        if (autoSyncFromSource) SyncFromSource();
        RebuildCacheIfNeeded();
        if (!_cachedHasComputedData) return;

        Vector3 bhPos    = _cachedBhPos;
        Vector3 observer = _cachedObserver;

        // Black hole
        Gizmos.color = new Color(0.05f, 0.05f, 0.05f);
        Gizmos.DrawWireSphere(bhPos, rs * drawScale);

        // Photon sphere
        if (drawPhotonSphere)
        {
            Gizmos.color = new Color(1f, 0.55f, 0.05f, 0.7f);
            Gizmos.DrawWireSphere(bhPos, 1.5f * rs * drawScale);
        }

        // Observer
        if (drawObserver)
        {
            Gizmos.color = Color.white;
            Gizmos.DrawSphere(observer, 0.03f * drawScale * rs);
        }

        // Sun direction
        if (drawSourceDirection)
        {
            Vector3 sunDir = new Vector3(Mathf.Cos(_cachedThetaSun), Mathf.Sin(_cachedThetaSun), 0f);
            Gizmos.color = new Color(1f, 0.95f, 0.3f);
            Gizmos.DrawLine(observer, observer + sunDir * 5f * rs * drawScale);
        }

        // ── Naïve ray (bent through field, drawn before geodesics) ─────────
        if (drawNaiveRay)
            DrawPolyline(_cachedNaivePoints, ColNaive);

        // ── Geodesic paths ───────────────────────────────────────────────────
        bool[] drawFlags = { drawDirect, drawIndirect1, drawIndirect2, drawIndirect3 };

        for (int b = 0; b < 4; b++)
        {
            if (!drawFlags[b] || !_cachedValid[b] || !float.IsFinite(_cachedAlpha[b]))
                continue;

            if (drawLaunchDirections)
            {
                Gizmos.color = BranchColours[b];
                Vector3 ld = new Vector3(Mathf.Cos(_cachedAlpha[b]), Mathf.Sin(_cachedAlpha[b]), 0f);
                Gizmos.DrawLine(observer, observer + ld * rs * drawScale);
            }

            DrawPolyline(_cachedPoints[b], BranchColours[b]);
        }

        // ── Debug overlay ────────────────────────────────────────────────────
#if UNITY_EDITOR
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"α_crit  {_cachedAlphaCrit * Mathf.Rad2Deg:F3}°");
        sb.AppendLine($"step    {DirectionalGeodesic2DLutSolver.StepSize:F4}   " +
                      $"min {DirectionalGeodesic2DLutSolver.StepMin:F6}");
        sb.AppendLine();

        // Naïve ray row — shows the NEE angular error
        string naiveStatus = !_cachedNaiveEscaped
            ? "CAPTURED"
            : $"arrives at {_cachedNaiveThetaRaw * Mathf.Rad2Deg:F3}°";
        sb.AppendLine($"{"naïve (NEE)",12}  launch={_cachedNaiveAlpha * Mathf.Rad2Deg:F3}°  {naiveStatus}");

        if (_cachedNaiveEscaped)
        {
            string dDirect = float.IsInfinity(_cachedNaiveErrDirect)
                ? "—"
                : $"{_cachedNaiveErrDirect * Mathf.Rad2Deg:F4}°";
            string dIndir = float.IsNaN(_cachedNaiveErrIndirect) || float.IsInfinity(_cachedNaiveErrIndirect)
                ? "—"
                : $"{_cachedNaiveErrIndirect * Mathf.Rad2Deg:F4}°";
            sb.AppendLine($"{"",12}  NEE err vs direct={dDirect}  vs indirect₁={dIndir}");
        }

        sb.AppendLine();

        for (int b = 0; b < 4; b++)
        {
            string tag = _cachedValid[b]
                ? $"{_cachedAlpha[b] * Mathf.Rad2Deg:F3}°  err {_cachedErr[b] * Mathf.Rad2Deg:F4}°"
                : "—";
            sb.AppendLine($"{BranchLabels[b],12}  {tag}");
        }

        Handles.color = Color.white;
        Handles.Label(observer + Vector3.up * (0.5f * rs * drawScale), sb.ToString());
#endif
    }

    private void ValidateDecodedAlpha(
        float r0, float alpha, float target, float rMax,
        out bool valid, out float errorRad)
    {
        valid    = false;
        errorRad = float.PositiveInfinity;

        if (!float.IsFinite(alpha)) return;

        bool escaped = DirectionalGeodesic2DLutSolver.March(r0, alpha, rs, rMax, out float thetaRaw);
        if (!escaped) return;

        errorRad = Mathf.Abs(thetaRaw - target);
        valid    = errorRad <= Mathf.Max(1e-7f, validationToleranceRad);
    }

    private void BuildRayPoints(
        List<Vector3> points, Vector3 bhPos,
        float r0, float alpha0, float rMax, bool indirect)
    {
        points.Clear();

        int maxSteps = indirect
            ? DirectionalGeodesic2DLutSolver.MarchMaxStepsIndirect
            : DirectionalGeodesic2DLutSolver.MarchMaxSteps;

        var naiveStub = default(DirectionalGeodesic2DLutSolver.BendLutData);
        DirectionalGeodesic2DLutSolver.MarchWithPoints(
            r0, alpha0, rs, rMax,
            in naiveStub, out _,
            _polarBuf,
            maxPoints: Mathf.Max(drawSteps, 500),
            maxStepsOverride: maxSteps);

        foreach (var (r, phi) in _polarBuf)
        {
            points.Add(bhPos + new Vector3(
                r * Mathf.Cos(phi) * drawScale,
                r * Mathf.Sin(phi) * drawScale,
                0f));
        }
    }

    private static void DrawPolyline(List<Vector3> points, Color color)
    {
        if (points == null || points.Count < 2) return;

        Gizmos.color = color;
        int maxSegments = 500;
        int stride      = Mathf.Max(1, (points.Count - 1) / maxSegments);

        for (int i = stride; i < points.Count; i += stride)
            Gizmos.DrawLine(points[i - stride], points[i]);

        int last = points.Count - 1;
        if (last > 0 && (last % stride) != 0)
            Gizmos.DrawLine(points[last - 1], points[last]);
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

    private static void TryReadAny<T>(
        object source, out T target, ref bool anyMissing, params string[] names)
    {
        if (TryGetMemberValue(source, out target, names)) return;
        anyMissing = true;
        target     = default;
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
        const BindingFlags flags =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

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
}