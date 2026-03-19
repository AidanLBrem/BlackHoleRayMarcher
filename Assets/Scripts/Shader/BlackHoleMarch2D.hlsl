// BlackHoleMarch2D.hlsl
// Marches null geodesics in the Schwarzschild metric using a 2D polar
// coordinate integrator in the orbital plane, with 3D position reconstructed
// only when needed for collision testing.
//
// Coordinate system:
//   r   = coordinate radius from black hole
//   phi = orbital angle in the plane (accumulated, not wrapped)
//   The orbital plane is spanned by (tangentX, tangentY) with normal orbitNormal.
//   3D position = bhPos + r * (cos(phi)*tangentX + sin(phi)*tangentY)
//   3D direction is derived from (dr/ds, r*dphi/ds) in the plane basis.

// ---------------------------------------------------------------------------
// Analytic Schwarzschild bend rate
// dTheta/ds where theta is the ray's global direction angle in the orbital plane.
// sinAlpha = sin of angle between ray and outward radial (signed).
// ---------------------------------------------------------------------------
float AnalyticBendRate(float r, float sinAlpha, float rs)
{
    float f = 1.0 - rs / r;
    if (f <= 0.0) return 0.0;
    return sinAlpha * (1.0 / r + (1.5 * rs - r) / (r * r * sqrt(f)));
}

// ---------------------------------------------------------------------------
// Reconstruct 3D world position from polar state
// ---------------------------------------------------------------------------
float3 PolarTo3D(float3 bhPos, float r, float phi,
                 float3 tangentX, float3 tangentY)
{
    return bhPos + r * (cos(phi) * tangentX + sin(phi) * tangentY);
}

// ---------------------------------------------------------------------------
// Reconstruct normalized 3D world direction from polar velocity state.
// localAlpha = WrapSigned(theta - phi) = angle from outward radial in orbital plane.
// ---------------------------------------------------------------------------
float3 PolarDirTo3D(float phi, float localAlpha,
                    float3 tangentX, float3 tangentY)
{
    // Outward radial direction in 3D
    float3 radialOut = cos(phi) * tangentX + sin(phi) * tangentY;
    // Tangential direction (90 degrees CCW in orbital plane)
    float3 tangential = -sin(phi) * tangentX + cos(phi) * tangentY;
    // Ray direction = cos(alpha)*radialOut + sin(alpha)*tangential
    return normalize(cos(localAlpha) * radialOut + sin(localAlpha) * tangential);
}

// ---------------------------------------------------------------------------
// WrapSigned: wrap angle to [-pi, pi]
// ---------------------------------------------------------------------------
float WrapSigned2D(float a)
{
    float t = fmod(a + UNITY_PI, 2.0 * UNITY_PI);
    if (t < 0.0) t += 2.0 * UNITY_PI;
    return t - UNITY_PI;
}

// ---------------------------------------------------------------------------
// Main 2D march
// ---------------------------------------------------------------------------
PixelMarcher marchNearBlackHole(PixelMarcher ray, BlackHole blackHole, inout uint rngState)
{
    int   emergencyBreak   = 0;
    float rs               = blackHole.SchwartzchildRadius;
    float marchShellRadius = max(rs * blackHole.blackHoleSOIMultiplier, 4.0 * rs);
    // Add a small entry tolerance so rays that start just outside don't immediately exit
    float exitThreshold = marchShellRadius * 1.001;

    // ── Build orbital plane basis ────────────────────────────────────────────
    // The geodesic stays in the plane spanned by rel and rayDir at entry.
    float3 rel0    = ray.ray.position - blackHole.position;
    float3 dir0    = ray.ray.direction;

    float3 orbitNormal = cross(rel0, dir0);
    float  onLen       = length(orbitNormal);

    if (onLen < 1e-6)
    {
        // Purely radial ray — no bending, march straight through
        float3 arbitrary = abs(rel0.x) < 0.9 ? float3(1, 0, 0) : float3(0, 1, 0);
        orbitNormal = normalize(cross(normalize(rel0), arbitrary));
    }
    else
    {
        orbitNormal /= onLen;
    }

    // tangentX = outward radial at entry, tangentY = 90 deg CCW in orbital plane
    float3 tangentX = normalize(rel0 - dot(rel0, orbitNormal) * orbitNormal);
    float3 tangentY = cross(orbitNormal, tangentX); // already unit length

    // ── Initial polar state ──────────────────────────────────────────────────
    float r   = length(rel0);
    float phi = 0.0; // tangentX is our phi=0 reference

    // theta: global angle of ray direction in orbital plane, measured from tangentX
    // Project dir0 onto orbital plane basis to get initial theta
    float dirX = dot(dir0, tangentX);
    float dirY = dot(dir0, tangentY);
    float theta = atan2(dirY, dirX);
    float phaseOffset = randomValue(rngState);
    // ── March loop ───────────────────────────────────────────────────────────
    while (true)
    {
        
        if (r < rs)
        {
            ray.hitBlackHole = true;
            return ray;
        }

        if (r > exitThreshold)
        {
            float localAlpha = WrapSigned2D(theta - phi);
            float cosA = cos(localAlpha);
            if (cosA > 0.0)
                break;
        }

        float distFromHorizon = max(r - rs, 0.0);
        float adaptiveStep = distFromHorizon * stepSize;
        float minStep = 0.01;
        float stepLen = sqrt(adaptiveStep * adaptiveStep + minStep * minStep);
        if (emergencyBreak == 0)
        {
            float localAlpha = WrapSigned2D(theta - phi);
            float cosA = cos(localAlpha);
            float sinA = sin(localAlpha);
            r   += phaseOffset * stepLen * cosA;
            phi += phaseOffset * stepLen * sinA / max(r, 1e-6);
            theta += phaseOffset * stepLen * AnalyticBendRate(r, sinA, rs);
        }
        float3 prevPos3D = ray.ray.position;

        float gtt_old = ComputeGtt(r, rs);

        // ── RK2 in polar coords ──────────────────────────────────────────────
        #ifdef ENABLE_LENSING
        {
            float localAlpha = WrapSigned2D(theta - phi);
            float cosA = cos(localAlpha);
            float sinA = sin(localAlpha);

            // k1
            float bend0 = AnalyticBendRate(r, sinA, rs);
            float dr0   = cosA;
            float dphi0 = sinA / max(r, 1e-6);

            // Midpoint state
            float rM     = r     + 0.5 * stepLen * dr0;
            float phiM   = phi   + 0.5 * stepLen * dphi0;
            float thetaM = theta + 0.5 * stepLen * bend0;

            float aM    = WrapSigned2D(thetaM - phiM);
            float cosAM = cos(aM);
            float sinAM = sin(aM);

            // k2
            float bendM = AnalyticBendRate(rM, sinAM, rs);
            float drM   = cosAM;
            float dphiM = sinAM / max(rM, 1e-6);

            // Full step
            r     += stepLen * drM;
            phi   += stepLen * dphiM;
            theta += stepLen * bendM;
        }
        #endif

        // ── Reconstruct 3D position and direction for collision testing ───────
        float3 pos3D = PolarTo3D(blackHole.position, r, phi, tangentX, tangentY);

        float localAlphaFinal = WrapSigned2D(theta - phi);
        float3 dir3D = PolarDirTo3D(phi, localAlphaFinal, tangentX, tangentY);

        // Update ray position and direction for collision query
        Ray testRay;
        float3 stepDir = normalize(pos3D - prevPos3D);
        float actualStepDist = length(pos3D - prevPos3D);
        testRay.position  = prevPos3D;
        testRay.direction = stepDir;
        testRay.energy    = ray.ray.energy;

        float newR   = length(pos3D - blackHole.position);
        float gtt_new = ComputeGtt(newR, rs);
        HitInfo h = queryCollisions(testRay, actualStepDist);

        emergencyBreak++;
        if (emergencyBreak > emergencyBreakMaxSteps)
        {
            ray.hitBlackHole = true;
            return ray;
        }

        if (h.didHit)
        {
            // Commit 3D state before handling reflection
            ray.ray.position  = pos3D;
            ray.ray.direction = dir3D;
            ray = handleReflection(ray, rngState, h);
            if (ray.rayEarlyKill)
                return ray;

            // Re-initialise polar state from the post-reflection 3D ray
            float3 relNew    = ray.ray.position - blackHole.position;
            float3 dirNew    = ray.ray.direction;

            // Recompute orbital plane for the new ray direction after bounce
            orbitNormal = cross(relNew, dirNew);
            onLen       = length(orbitNormal);
            if (onLen < 1e-6)
            {
                float3 arbitrary = abs(relNew.x) < 0.9 ? float3(1, 0, 0) : float3(0, 1, 0);
                orbitNormal = normalize(cross(normalize(relNew), arbitrary));
            }
            else
            {
                orbitNormal /= onLen;
            }

            tangentX = normalize(relNew - dot(relNew, orbitNormal) * orbitNormal);
            tangentY = cross(orbitNormal, tangentX);

            r     = length(relNew);
            phi   = 0.0;
            float dxNew = dot(dirNew, tangentX);
            float dyNew = dot(dirNew, tangentY);
            theta = atan2(dyNew, dxNew);
        }
        else
        {
            // Commit 3D state
            ray.ray.position  = pos3D;
            ray.ray.direction = dir3D;
        }

        #ifdef ENABLE_LENSING
            #ifdef USE_REDSHIFTING
                ray.ray.energy *= sqrt(gtt_old / gtt_new);
            #endif
        #endif
    }

    // Commit final 3D state on exit
    float localAlphaExit = WrapSigned2D(theta - phi);
    ray.ray.position  = PolarTo3D(blackHole.position, r, phi, tangentX, tangentY);
    ray.ray.direction = PolarDirTo3D(phi, localAlphaExit, tangentX, tangentY);

    return ray;
}
