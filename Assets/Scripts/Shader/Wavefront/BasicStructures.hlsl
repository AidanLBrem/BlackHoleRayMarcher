
struct HitInfo 
{
    bool  didHit;
    float distance;
    float u;
    float v;
    uint  triIndex;
    int   objectIndex;
    float3 worldNormal;  // hardware RT path only
};
            
struct AABBHitInfo {
    bool didHit;
    float distance;
};

struct Ray
{
    float3 position;
    float3 direction; 
};
struct Triangle
{
    uint baseIndex;
    float3 edgeAB;
    float3 edgeAC;
    //float3 geometricNormal;
};
struct RayTracingMaterial
{
    float4 color;
    float4 emissiveColor;
    float4 specularColor;
    float emissionStrength;
    float roughness;
    float metallicity;
};
struct Sphere
{
    float3 position;
    float radius;
    RayTracingMaterial material;
};

struct Mesh
{
    float4x4 localToWorldMatrix;
    float4x4 worldToLocalMatrix;
    RayTracingMaterial material;

    uint firstBVHNodeIndex;

    float AABBLeftX;
    float AABBLeftY;
    float AABBLeftZ;
    float AABBRightX;
    float AABBRightY;
    float AABBRightZ;
    
    uint triangleOffset;
};

struct BVHNode
{
    int left;
    int right;
    uint firstIndex;
    uint count;

    float AABBLeftX;
    float AABBLeftY;
    float AABBLeftZ;
    float AABBRightX;
    float AABBRightY;
    float AABBRightZ;
};
struct color_info
{
    float3 rayColor;
    float3 incomingLight;
};
struct control   { uint flags; uint rngState; };
struct ray       { float3 position; float3 direction; };
struct blackhole
{
    float3 position;
    float  schwarzchild_radius;
    float  black_hole_soi_multiplier;
};
StructuredBuffer<Mesh>     Instances;
StructuredBuffer<Triangle> Triangles;
StructuredBuffer<uint>     TriangleIndices;
StructuredBuffer<float3>   Normals;
StructuredBuffer<float3>   Vertices;
StructuredBuffer<BVHNode>  BVHNodes;
StructuredBuffer<BVHNode>  TLASNodes;
StructuredBuffer<uint>     TLASRefs;
StructuredBuffer<blackhole>      blackholes;
uint num_black_holes;

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

HitInfo rayTriangle(Ray ray, Triangle tri)
{
    float3 edgeAB = tri.edgeAB;
    float3 edgeAC = tri.edgeAC;
    float3 geometricNormal = cross(tri.edgeAB, tri.edgeAC);

    float determinant = -dot(ray.direction, geometricNormal);
    if (determinant <=  0)
        return (HitInfo)0;

    float invDet = 1 / determinant;
    uint vertex1 = TriangleIndices[tri.baseIndex];
    float3 ao = ray.position - Vertices[vertex1];
    float dst = dot(ao, geometricNormal) * invDet;
    if (dst <  0)
        return (HitInfo)0;

    float3 dao = cross(ao, ray.direction);
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
