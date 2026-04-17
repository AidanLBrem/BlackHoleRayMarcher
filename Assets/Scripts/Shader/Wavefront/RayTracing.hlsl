int   TLASRootIndex;
int   numInstances;
int   numTLASNodes;
int   numMeshes;

AABBHitInfo RayAABB(float3 rayOrigin, float3 rayDirection, float3 inverseDirection, float3 boxMin, float3 boxMax, float distanceToBeat)
{
    float3 invDir = inverseDirection;
    float3 tMin = (boxMin - rayOrigin) * invDir;
    float3 tMax = (boxMax - rayOrigin) * invDir;
    float3 t1 = min(tMin, tMax);
    float3 t2 = max(tMin, tMax);
    float tNear = max(max(t1.x, t1.y), t1.z);
    float tFar = min(min(t2.x, t2.y), t2.z);

    AABBHitInfo hitInfo = (AABBHitInfo)0;
    hitInfo.didHit = (tNear <= tFar) && (tFar >= 0.0) && (tNear <= distanceToBeat);
    hitInfo.distance = max(tNear, 0.0);
    return hitInfo;
}
AABBHitInfo RayHitsBox(
                float3 rayOrigin,
                float3 rayDirection,
                float3 inverseDirection,
                float AABBLeftX,
                float AABBLeftY,
                float AABBLeftZ,
                float AABBRightX,
                float AABBRightY,
                float AABBRightZ,
                float distanceToBeat)
{
    float3 boxMin = float3(AABBLeftX,  AABBLeftY,  AABBLeftZ);
    float3 boxMax = float3(AABBRightX, AABBRightY, AABBRightZ);
    return RayAABB(rayOrigin, rayDirection, inverseDirection, boxMin, boxMax, distanceToBeat);
}

HitInfo rayTriangle(float3 position, float3 direction, Triangle tri)
{
    float3 edgeAB = tri.edgeAB;
    float3 edgeAC = tri.edgeAC;
    float3 geometricNormal = cross(tri.edgeAB, tri.edgeAC);

    float determinant = -dot(direction, geometricNormal);
    if (determinant <=  0)
        return (HitInfo)0;

    float invDet = 1 / determinant;
    uint vertex1 = TriangleIndices[tri.baseIndex];
    float3 ao = position - Vertices[vertex1];
    float dst = dot(ao, geometricNormal) * invDet;
    if (dst <  0)
        return (HitInfo)0;

    float3 dao = cross(ao, direction);
    float u = dot(edgeAC, dao) * invDet;
    if (u <  0)
        return (HitInfo)0;

    float v = -dot(edgeAB, dao) * invDet;
    if (v < 0)
        return (HitInfo)0;

    if (u + v > 1.0)
        return (HitInfo)0;

    HitInfo hitInfo = (HitInfo)0;
    hitInfo.didHit = true;
    hitInfo.distance = dst;
    hitInfo.u = u;
    hitInfo.v = v;
    return hitInfo;
}

float GetSOIRadius(int i)
{
    float rs = blackholes[i].schwarzchild_radius;
    return max(rs * blackholes[i].black_hole_soi_multiplier, 4.0 * rs);
}

bool RayEntersSOI(float3 origin, float3 dir, float tMax, out float tEntry)
{
    tEntry  = tMax;
    for (int i = 0; i < num_black_holes; i++)
    {
        float t = RaySphereEntryDistance(origin, dir, blackholes[i].position, GetSOIRadius(i));
        if (t >= 0 && t < tEntry)
        {
            tEntry  = t;
        }
    }
    return false;
}
HitInfo checkMeshCollisions(ray r, float worldTMax, bool findClosestCollisionOnly)
{
    HitInfo closest = (HitInfo)0;
    if (numMeshes <= 0 || numInstances <= 0 || numTLASNodes <= 0 || TLASRootIndex < 0)
        return closest;

    float bestWorldT = worldTMax;
    float3 inverseDirection = 1.0 / r.direction;

    int tlasStack[8]; int tlasSp = 0;
    int blasStack[32]; int blasSp = 0;

    uint tlasNodeIdx = (uint)TLASRootIndex;

    for (;;)
    {
        BVHNode tlasNode = TLASNodes[tlasNodeIdx];
        bool tlasLeaf = (tlasNode.left == -1) && (tlasNode.right == -1);

        if (tlasLeaf)
        {
            for (uint j = tlasNode.firstIndex; j < tlasNode.firstIndex + tlasNode.count; j++)
            {
                uint instanceIndex = TLASRefs[j];
                Mesh mesh = Instances[instanceIndex];

                float3 localDirUn = mul(mesh.worldToLocalMatrix, float4(r.direction, 0)).xyz;
                float dirScale = length(localDirUn);
                if (dirScale < 1e-12) continue;

                float3 localDir   = localDirUn / dirScale;
                float3 localPos   = mul(mesh.worldToLocalMatrix, float4(r.position, 1)).xyz;
                float3 localInvDir = 1.0 / localDir;
                float bestLocalT  = bestWorldT * dirScale;

                blasSp = 0;
                uint blasNodeIdx = mesh.firstBVHNodeIndex;

                for (;;)
                {
                    BVHNode blasNode = BVHNodes[blasNodeIdx];
                    bool blasLeaf = (blasNode.left == -1) && (blasNode.right == -1);

                    if (blasLeaf)
                    {
                        for (uint k = blasNode.firstIndex; k < blasNode.firstIndex + blasNode.count; k++)
                        {
                            Triangle tri = Triangles[k];
                            HitInfo h = rayTriangle(localPos, localDir, tri);
                            if (!h.didHit) continue;

                            float localT = h.distance;
                            if (localT > bestLocalT) continue;

                            float worldT = localT / dirScale;
                            if (worldT >= bestWorldT) continue;

                            bestLocalT = localT;
                            bestWorldT = worldT;

                            h.triIndex    = k;
                            h.distance    = worldT;
                            h.objectIndex = (int)instanceIndex;

                            uint i0 = TriangleIndices[tri.baseIndex + 0];
                            uint i1 = TriangleIndices[tri.baseIndex + 1];
                            uint i2 = TriangleIndices[tri.baseIndex + 2];
                            float3 localNormal = Normals[i0] * (1 - (h.u + h.v))
                                               + Normals[i1] * h.u
                                               + Normals[i2] * h.v;
                            float3x3 normalMat = transpose((float3x3)mesh.worldToLocalMatrix);
                            h.worldNormal = safeNormalize(mul(normalMat, localNormal));

                            closest = h;
                            if (findClosestCollisionOnly) return closest;
                        }

                        if (blasSp == 0) break;
                        blasNodeIdx = (uint)blasStack[--blasSp];
                        continue;
                    }

                    BVHNode ln = BVHNodes[blasNode.left];
                    BVHNode rn = BVHNodes[blasNode.right];

                    AABBHitInfo lh = RayHitsBox(localPos, localDir, localInvDir,
                        ln.AABBLeftX, ln.AABBLeftY, ln.AABBLeftZ,
                        ln.AABBRightX, ln.AABBRightY, ln.AABBRightZ, bestLocalT);
                    AABBHitInfo rh = RayHitsBox(localPos, localDir, localInvDir,
                        rn.AABBLeftX, rn.AABBLeftY, rn.AABBLeftZ,
                        rn.AABBRightX, rn.AABBRightY, rn.AABBRightZ, bestLocalT);

                    if (!lh.didHit && !rh.didHit)
                    {
                        if (blasSp == 0) break;
                        blasNodeIdx = (uint)blasStack[--blasSp];
                        continue;
                    }

                    bool leftFirst = lh.didHit && (!rh.didHit || lh.distance <= rh.distance);
                    uint nearIdx   = leftFirst ? (uint)blasNode.left  : (uint)blasNode.right;
                    uint farIdx    = leftFirst ? (uint)blasNode.right : (uint)blasNode.left;
                    bool farHit    = leftFirst ? rh.didHit : lh.didHit;

                    if (farHit && blasSp < 32) blasStack[blasSp++] = (int)farIdx;
                    blasNodeIdx = nearIdx;
                }
            }

            if (tlasSp == 0) break;
            tlasNodeIdx = (uint)tlasStack[--tlasSp];
            continue;
        }

        BVHNode tln = TLASNodes[tlasNode.left];
        BVHNode trn = TLASNodes[tlasNode.right];

        AABBHitInfo lh = RayHitsBox(r.position, r.direction, inverseDirection,
            tln.AABBLeftX, tln.AABBLeftY, tln.AABBLeftZ,
            tln.AABBRightX, tln.AABBRightY, tln.AABBRightZ, bestWorldT);
        AABBHitInfo rh = RayHitsBox(r.position, r.direction, inverseDirection,
            trn.AABBLeftX, trn.AABBLeftY, trn.AABBLeftZ,
            trn.AABBRightX, trn.AABBRightY, trn.AABBRightZ, bestWorldT);

        if (!lh.didHit && !rh.didHit)
        {
            if (tlasSp == 0) break;
            tlasNodeIdx = (uint)tlasStack[--tlasSp];
            continue;
        }

        bool leftFirst = lh.didHit && (!rh.didHit || lh.distance <= rh.distance);
        uint nearIdx   = leftFirst ? (uint)tlasNode.left  : (uint)tlasNode.right;
        uint farIdx    = leftFirst ? (uint)tlasNode.right : (uint)tlasNode.left;
        bool farHit    = leftFirst ? rh.didHit : lh.didHit;

        if (farHit && tlasSp < 8) tlasStack[tlasSp++] = (int)farIdx;
        tlasNodeIdx = nearIdx;
    }

    return closest;
}
HitInfo TraceRay(float3 origin, float3 direction, float tMax)
{
    HitInfo result = (HitInfo)0;
    ray r;
    r.position  = origin;
    r.direction = direction;
    HitInfo hit = checkMeshCollisions(r, tMax, false);
    if (hit.didHit)
    {
        result.didHit      = true;
        result.distance    = hit.distance;
        result.objectIndex = (uint)hit.objectIndex;
        result.triIndex    = hit.triIndex;
        result.u           = hit.u;
        result.v           = hit.v;
    }
    return result;
}