
bool PointInsideSphere(float3 p, float3 center, float radius)
{
    float3 d = p - center;
    return dot(d, d) <= radius * radius;
}

bool SegmentCrossesSphere(float3 p0, float3 p1, float3 center, float radius)
{
    if (PointInsideSphere(p0, center, radius) || PointInsideSphere(p1, center, radius))
        return true;

    float3 seg = p1 - p0;
    float segLen = length(seg);
    if (segLen <= 1e-6)
        return false;

    float3 segDir = seg / segLen;
    float t = RaySphereEntryDistance(p0, segDir, center, radius);
    return (t >= 0.0 && t <= segLen);
}

float GetBlackHoleMarchShellRadius(int i)
{
    float rs = BlackHoles[i].SchwartzchildRadius;
    return max(rs * BlackHoles[i].blackHoleSOIMultiplier, 4.0 * rs);
}

float3 ComputeTotalAccel(float3 pos, float3 dir)
{
    float3 total = float3(0, 0, 0);
    //least ridiculous way to write an algebraic formula
    ///(1/r^2 + (1.5rs - r)/r^2*sqrt(1-rs/r))*perpindicular_component
    for (int i = 0; i < numBlackHoles; i++)
    {
        float3 rel = pos - BlackHoles[i].position;
        float  rs  = BlackHoles[i].SchwartzchildRadius;
        float r2 = max(dot(rel, rel), rs * rs * 1.0001);
        float invR = rsqrt(r2);
        float r = r2 * invR;
        float invR2 = invR * invR;

        float f = 1.0 - rs * invR; //schwartzchild factor
        if (f <= 0.0) continue;

        float invSqrtF = rsqrt(f);

        float bendFactor =
            invR +
            (1.5 * rs - r) * invR2 * invSqrtF;
        float3 rel_perp  = rel - dot(rel, dir) * dir; //perpindicular component of vector
    
        total += (-bendFactor * invR) * rel_perp;
    }

    return total;
}
//for redshifting
float ComputeGttMulti(float3 pos)
{
    float potential = 0.0;

    for (int i = 0; i < numBlackHoles; i++)
    {
        float rs = BlackHoles[i].SchwartzchildRadius;
        float r  = length(pos - BlackHoles[i].position);
        potential += rs / max(r, rs * 1.0001);
    }

    return max(1.0 - potential, 0.0001);
}

//True if the point is inside any BH horizon.
bool IsInsideAnyHorizon(float3 pos)
{
    for (int i = 0; i < numBlackHoles; i++)
    {
        float rs = BlackHoles[i].SchwartzchildRadius;
        if (PointInsideSphere(pos, BlackHoles[i].position, rs))
            return true;
    }
    return false;
}

//True if the point is inside any BH march shell.
bool IsInsideAnyMarchShell(float3 pos)
{
    for (int i = 0; i < numBlackHoles; i++)
    {
        float shell = GetBlackHoleMarchShellRadius(i);
        if (PointInsideSphere(pos, BlackHoles[i].position, shell))
            return true;
    }
    return false;
}

//True if the ray from pos along dir will enter any march shell.
bool RayWillEnterAnyMarchShell(float3 pos, float3 dir)
{
    for (int i = 0; i < numBlackHoles; i++)
    {
        float shell = GetBlackHoleMarchShellRadius(i);
        float tEntry = RaySphereEntryDistance(pos, dir, BlackHoles[i].position, shell);
        if (tEntry >= 0.0)
            return true;
    }
    return false;
}

// Stronger geometric exit condition:
// safe to stop marching only when:
// 1) outside every march shell
// 2) forward ray does not intersect any march shell
bool IsSafeToExitMarch(float3 pos, float3 dir)
{
    for (int i = 0; i < numBlackHoles; i++)
    {
        float shell = GetBlackHoleMarchShellRadius(i);

        if (PointInsideSphere(pos, BlackHoles[i].position, shell))
            return false;

        float tEntry = RaySphereEntryDistance(pos, dir, BlackHoles[i].position, shell);
        if (tEntry >= 0.0)
            return false;
    }

    return true;
}

// Checks whether the current integration segment crosses any event horizon.
bool SegmentHitsAnyHorizon(float3 p0, float3 p1)
{
    for (int i = 0; i < numBlackHoles; i++)
    {
        float rs = BlackHoles[i].SchwartzchildRadius;
        if (SegmentCrossesSphere(p0, p1, BlackHoles[i].position, rs))
            return true;
    }

    return false;
}
void IntegrateLeapfrog(float3 pos, float3 dir, float stepLen, out float3 newPos, out float3 newDir)
{
    // half step kick
    float3 accel = ComputeTotalAccel(pos, dir);
    float3 halfDir = (dir + 0.5 * stepLen * accel);
    
    // full step drift
    newPos = pos + stepLen * halfDir;
    
    // half step kick at new position
    float3 accel2 = ComputeTotalAccel(newPos, halfDir);
    newDir = normalize(halfDir + 0.5 * stepLen * accel2);
}
// ----------------------------------------------------------------------------
// RK4 integrator for:
//   dpos/ds = dir
//   ddir/ds = ComputeTotalAccel(pos, dir)
// ----------------------------------------------------------------------------
void IntegrateRK4(float3 pos, float3 dir, float stepLen, out float3 newPos, out float3 newDir)
{
    dir = normalize(dir);

    // k1
    float3 k1_pos = dir;
    float3 k1_dir = ComputeTotalAccel(pos, dir);

    // k2
    float3 pos2 = pos + 0.5 * stepLen * k1_pos;
    float3 dir2 = (dir + 0.5 * stepLen * k1_dir);
    float3 k2_pos = dir2;
    float3 k2_dir = ComputeTotalAccel(pos2, dir2);

    // k3
    float3 pos3 = pos + 0.5 * stepLen * k2_pos;
    float3 dir3 = (dir + 0.5 * stepLen * k2_dir);
    float3 k3_pos = dir3;
    float3 k3_dir = ComputeTotalAccel(pos3, dir3);

    // k4
    float3 pos4 = pos + stepLen * k3_pos;
    float3 dir4 = (dir + stepLen * k3_dir);
    float3 k4_pos = dir4;
    float3 k4_dir = ComputeTotalAccel(pos4, dir4);

    newPos = pos + (stepLen / 6.0) * (k1_pos + 2.0 * k2_pos + 2.0 * k3_pos + k4_pos);
    newDir = dir + (stepLen / 6.0) * (k1_dir + 2.0 * k2_dir + 2.0 * k3_dir + k4_dir);
    newDir = normalize(newDir);
}
//Parent march loop
//upon completion, we should either be outside the SOI, in the event horizon, or reached maxBounces
PixelMarcher marchAllBlackHoles(PixelMarcher ray, inout uint rngState)
{
    if (numBlackHoles == 0)
        return ray;

    int emergencyBreak = 0;

    float3 pos = ray.ray.position;
    float3 dir = normalize(ray.ray.direction);

    //Immediate capture if somehow already inside a horizon.
    if (IsInsideAnyHorizon(pos))
    {
        ray.hitBlackHole = true;
        return ray;
    }

    // Only enter the marcher if we are already in a shell or is about to enter one
    if (!IsInsideAnyMarchShell(pos) && !RayWillEnterAnyMarchShell(pos, dir))
        return ray;
    #ifdef MARCH_CHORD_COLLISION_LIMIT
    int stepsSinceCollisionTest = 0;
    float3 chordStart = ray.ray.position;
    #endif
    while (true)
    {
        #ifdef MARCH_CHORD_COLLISION_LIMIT
        stepsSinceCollisionTest++;
        #endif
        //calculate adaptive step size - closer to the horizon = more bending = more steps needed
        float minDistFromHorizon = 3.402823e+38;

        for (int i = 0; i < numBlackHoles; i++)
        {
            float rs = BlackHoles[i].SchwartzchildRadius;
            float r  = length(pos - BlackHoles[i].position);
            minDistFromHorizon = min(minDistFromHorizon, max(r - rs, 0.0));
        }

        float adaptiveStep = minDistFromHorizon * stepSize;
        float minStep = stepSize / 100.0;
        float stepLen = sqrt(adaptiveStep * adaptiveStep + minStep * minStep);
        
        //RK4
        float3 newPos;
        IntegrateRK4(pos, dir, stepLen, newPos, dir);
        //ntegrateLeapfrog(pos, dir, stepLen, newPos, newDir);
        //We need to check to see if we intersect the black hole at any point along the chord
        //TODO: Merge this with queryCollisions
        if (SegmentHitsAnyHorizon(pos, newPos))
        {
            ray.hitBlackHole = true;
            return ray;
        }

        //See if we collide with anything along this chord, we use testRay because we don't want to write to the actual ray until we confirm where we are going
        //TODO: It might be faster to use the ray itself. 
        HitInfo h = (HitInfo)0;

        #ifdef MARCH_CHORD_COLLISION_LIMIT
        float t = saturate(minDistFromHorizon / (GetBlackHoleMarchShellRadius(0) - BlackHoles[0].SchwartzchildRadius));
        int dynamicSteps = (int)lerp(1, u_StepsPerCollisionTest, t);
        if (stepsSinceCollisionTest > dynamicSteps)
        {
            Ray testRay;
            testRay.position = chordStart;
            testRay.direction = normalize(newPos - chordStart);
            h = queryCollisions(testRay, length(newPos - chordStart), false);
            stepsSinceCollisionTest = 0;
        }

        #endif
        #ifndef MARCH_CHORD_COLLISION_LIMIT
        Ray testRay;
        testRay.position  = pos;
        float actualStepDist = length(newPos - pos);
        testRay.direction = (newPos - pos)/actualStepDist;


        h = queryCollisions(testRay, actualStepDist, false);
        #endif

        //We did collide with something. Move to the collision site and reflect
        if (h.didHit)
        {
            #ifdef MARCH_CHORD_COLLISION_LIMIT
            ray.ray.position  = chordStart;
            ray.ray.direction = normalize(newPos - chordStart); //this is the ray we intersect along
            #else
            ray.ray.position  = pos;
            ray.ray.direction = normalize(newPos - pos);
            #endif

            ray = handleReflection(ray, rngState, h);
            #ifdef MARCH_CHORD_COLLISION_LIMIT
            chordStart = ray.ray.position; //go to new hit position
            #endif
            //Make sure to redshift!
            #ifdef USE_REDSHIFTING
            float gtt_old = ComputeGttMulti(pos);
            float gtt_new = ComputeGttMulti(newPos);
            ray.energy *= sqrt(gtt_old / gtt_new);
            #endif
            //if we are at max reflections or russian roulette killed, return. classifier will handle it from there
            if (ray.rayEarlyKill || ray.numBounces >= maxBounces)
                return ray;

            pos = ray.ray.position;
            dir = normalize(ray.ray.direction);
            //should never happen, but eh
            if (IsInsideAnyHorizon(pos))
            {
                ray.hitBlackHole = true;
                return ray;
            }


            continue;
        }
        #ifdef MARCH_CHORD_COLLISION_LIMIT
        if (stepsSinceCollisionTest == 0)
            chordStart = newPos;
        #endif
        //no hit, we march instead
        #ifdef ENABLE_LENSING
        #ifdef USE_REDSHIFTING
        float gtt_new = ComputeGttMulti(newPos);
        float gtt_old = ComputeGttMulti(pos);
        ray.energy *= sqrt(gtt_old / gtt_new);
        #endif
        #endif

        pos = newPos;

        ray.ray.position  = pos;
        ray.ray.direction = dir;

        //check to see if we have exited SOI
        if (IsSafeToExitMarch(pos, dir))
            return ray;
        
        //emergency break to stop weird photon sphere crashes
        emergencyBreak++;
        if (emergencyBreak > emergencyBreakMaxSteps)
        {
            ray.hitBlackHole = true;
            return ray;
        }
    }

    return ray;
}