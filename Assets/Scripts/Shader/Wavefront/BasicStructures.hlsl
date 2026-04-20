
#define ACTIVE_RAY_QUEUE 0
#define LINEAR_RAY_QUEUEA 1
#define GEODISC_RAY_QUEUEA 2
#define LINEAR_RAY_QUEUEB 3
#define GEODISC_RAY_QUEUEB 4
#define REFLECTION_QUEUE 5
#define NEE_QUEUE 6
#define SCATTER_QUEUE 7
#define SKYBOX_QUEUE 8
struct ray       { float3 position; float3 direction; };
struct HitInfo 
{
    //bool  didHit;
    float distance;
    float u;
    float v;
    uint  triIndex;
    int   objectIndex;
    //float3 worldNormal;  // hardware RT path only
};
            
struct AABBHitInfo {
    bool didHit;
    float distance;
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
struct control   { uint rngState; uint pixelIndex;};

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

