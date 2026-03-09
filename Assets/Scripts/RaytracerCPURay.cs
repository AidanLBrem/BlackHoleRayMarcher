using System;
using UnityEngine;
using TMPro;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine.InputSystem.HID;
using Random = System.Random;

public struct BoundsHitData
{
    public Bounds bounds;
    public bool isLeaf;
    public BVHNode node;
}
[System.Serializable]
public struct CPUHitInfo
{
    public float hitDistance;
    public Vector3 hitPoint;
    public Vector3 shadingNormal;
    public Vector3 geometricNormal;
    public List<BoundsHitData> boundsHitData;
    public int triangleTests;
    public int BVHNodesSearched;
    public float time;
    public BVHNode hitNode;
    public RayTracingMaterial material;
    public bool debugInvalidBelowSurface;
    public bool debugInvalidBRDF;
}
[System.Serializable]
public struct RayPathStruct
{
    [SerializeField] public List<CPUHitInfo> hitInfos;
    public float time;
    public int debugInvalidBelowSurfaceCount;
    public int debugInvalidBRDFCount;
}
[ExecuteAlways]
public class RaytracerCPURay : MonoBehaviour
{
    private Vector3 rayStartPosition;
    [SerializeField] private TextMeshProUGUI GUIElement;
    [SerializeField] private Transform tracerProperties;
    [SerializeField] private RayPathStruct path;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    
    public static float randomValueNormalDistribution()
    {
        float theta = 2 * 3.1415926f * UnityEngine.Random.value;
        float rho = Mathf.Sqrt(-2 * Mathf.Log(Mathf.Max(UnityEngine.Random.value, 1e-6f)));
        return rho * Mathf.Cos(theta);
    }
    public static Vector3 randomDirection()
    {
        float x = randomValueNormalDistribution();
        float y = randomValueNormalDistribution();
        float z = randomValueNormalDistribution();
        return Vector3.Normalize(new Vector3(x,y,z));
    }

    // Update is called once per frame
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        DrawRayStartingPosition();
        if (transform.hasChanged)
        {
            float time = Time.realtimeSinceStartup;
            path = DrawRayPath();
            float timeAfter = Time.realtimeSinceStartup;
            path.time = (timeAfter - time);
            transform.hasChanged = false;
        }
        Vector3 currentPosition = transform.position;
        foreach (CPUHitInfo intersect in path.hitInfos)
        {
            if (intersect.hitDistance < float.MaxValue)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawLine(currentPosition, intersect.hitPoint);
                GUIElement.text = "Triangle Test: " + intersect.triangleTests + "\n Node Tests: " +
                                  intersect.BVHNodesSearched + "\nTime taken: " + intersect.time * 1000f;

                int leafCount = 0;
                int nonLeafCount = 0;
                for (int i = 0; i < intersect.boundsHitData.Count; i++)
                {
                    BoundsHitData data =  intersect.boundsHitData[i];
                    if (intersect.hitNode == data.node)
                    {
                        //Gizmos.color = new Color(0f, 1f, 0f, 1f);
                        //leafCount++;
                        //Gizmos.DrawCube(data.bounds.center, data.bounds.size);
                    }
                    /*else if (data.isLeaf)
                    {
                        Gizmos.color = new Color(1f, 0f, 0f, 1f / (++leafCount));
                    }

                    else
                    {
                        Gizmos.color = new Color(0f, 0f, 1f, 0.5f / (++nonLeafCount));
                    }*/
                    
                }

                currentPosition = intersect.hitPoint;
            }   

            else
            {
                Debug.Log("No collision");
                Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1000f);
                GUIElement.text = "No collision";
            }
        }
        
        

    }

    void DrawRayStartingPosition()
    {

        Gizmos.DrawSphere(transform.position, 0.25f);
    }

    RayPathStruct DrawRayPath()
    {
        RayPathStruct toReturn = new RayPathStruct();
        toReturn.hitInfos = new List<CPUHitInfo>();
        Vector3 rayPosition = transform.position;
        Vector3 rayDirection = transform.forward;
        
        RayTracingManager manager = tracerProperties.GetComponent<RayTracingManager>();
        int maxBounces = manager.maxBounces;
        Ray ray = new Ray(transform.position, rayDirection);
        for (int i = 0; i < maxBounces; i++)
        {
            CPUHitInfo intersect = queryRayCollisions(ref ray);

            if (intersect.hitDistance == float.MaxValue)
            {
                return toReturn;
            }
            HandleReflection(ref ray, ref intersect);
            
            if (intersect.debugInvalidBelowSurface)
            {
                toReturn.debugInvalidBelowSurfaceCount++;
            }

            if (intersect.debugInvalidBRDF)
            {
                toReturn.debugInvalidBRDFCount++;
            }
            
            toReturn.hitInfos.Add(intersect);
        }
        
        return toReturn;
    }

    public static Vector3 SafeNormalize(Vector3 v)
    {
        float len2 = Vector3.Dot(v, v);
        return (len2 > 1e-20f) ? v / Mathf.Sqrt(len2) : Vector3.up;
    }

    public static void BuildOrthonormalBasis(Vector3 N, out Vector3 T, out Vector3 B)
    {
        Vector3 up = Mathf.Abs(N.z) < 0.999f ? new Vector3(0f, 0f, 1f) : new Vector3(1f, 0f, 0f);
        T = SafeNormalize(Vector3.Cross(up, N));
        B = Vector3.Cross(N, T);
    }

    public static Vector3 SampleCosineHemisphere(Vector2 xi)
    {
        float r = Mathf.Sqrt(xi.x);
        float phi = 2f * Mathf.PI * xi.y;

        float x = r * Mathf.Cos(phi);
        float y = r * Mathf.Sin(phi);
        float z = Mathf.Sqrt(Mathf.Max(0f, 1f - x * x - y * y));

        return new Vector3(x, y, z);
    }

    public static Vector3 ToWorld(Vector3 localDir, Vector3 N)
    {
        BuildOrthonormalBasis(N, out Vector3 T, out Vector3 B);
        return SafeNormalize(T * localDir.x + B * localDir.y + N * localDir.z);
    }

    public static Vector3 SampleGGX_H(Vector2 xi, float roughness, Vector3 N)
    {
        float a = Mathf.Max(0.001f, roughness * roughness);
        float a2 = a * a;

        float phi = 2f * Mathf.PI * xi.x;
        float cosTheta = Mathf.Sqrt((1f - xi.y) / (1f + (a2 - 1f) * xi.y));
        float sinTheta = Mathf.Sqrt(Mathf.Max(0f, 1f - cosTheta * cosTheta));

        Vector3 hLocal = new Vector3(
            sinTheta * Mathf.Cos(phi),
            sinTheta * Mathf.Sin(phi),
            cosTheta
        );

        return ToWorld(hLocal, N);
    }

    public static float D_GGX(float NdotH, float roughness)
    {
        float a = Mathf.Max(0.001f, roughness * roughness);
        float a2 = a * a;
        float denom = (NdotH * NdotH) * (a2 - 1f) + 1f;
        return a2 / Mathf.Max(Mathf.PI * denom * denom, 1e-8f);
    }

    public static float G1_SmithGGX(float NdotX, float roughness)
    {
        float a = Mathf.Max(0.001f, roughness * roughness);
        float a2 = a * a;
        float denom = NdotX + Mathf.Sqrt(a2 + (1f - a2) * NdotX * NdotX);
        return (2f * NdotX) / Mathf.Max(denom, 1e-8f);
    }

    public static float G_SmithGGX(float NdotV, float NdotL, float roughness)
    {
        return G1_SmithGGX(NdotV, roughness) * G1_SmithGGX(NdotL, roughness);
    }

    public static Vector3 FresnelSchlick(float cosTheta, Vector3 F0)
    {
        float t = Mathf.Pow(1f - cosTheta, 5f);
        return F0 + (Vector3.one - F0) * t;
    }

    public static float Luminance(Vector3 c)
    {
        return Vector3.Dot(c, new Vector3(0.2126f, 0.7152f, 0.0722f));
    }

    public static float Saturate(float x) => Mathf.Clamp01(x);

    public static float RandomValue(System.Random rng)
    {
        return (float)rng.NextDouble();
    }

    public static void HandleReflection(ref Ray ray, ref CPUHitInfo hitInfo)
    {

        RayTracingMaterial material = hitInfo.material;

        Vector3 N = SafeNormalize(hitInfo.shadingNormal);
        Vector3 Ng = SafeNormalize(hitInfo.geometricNormal);
        Vector3 V = SafeNormalize(-ray.direction);
        

        Vector3 baseColor = new Vector3(material.color.r, material.color.g, material.color.b);
        float metallic = Saturate(material.metallicity);
        float roughness = Saturate(material.roughness);

        Vector3 dielectricF0 = new Vector3(0.04f, 0.04f, 0.04f);
        Vector3 F0 = Vector3.Lerp(dielectricF0, baseColor, metallic);

        Vector3 diffuseColor = baseColor * (1f - metallic);

        float specularWeight = Saturate(Luminance(F0));
        float diffuseWeight = Luminance(diffuseColor);

        float totalWeight = specularWeight + diffuseWeight;

        float specularChance = specularWeight / totalWeight;
        specularChance = Mathf.Clamp(specularChance, 0.001f, 0.999f);

        float choose = UnityEngine.Random.value;

        if (choose < specularChance)
        {
            Vector2 xi = new Vector2(UnityEngine.Random.value, UnityEngine.Random.value);
            Vector3 H = SampleGGX_H(xi, roughness, N);
            Vector3 L = Vector3.Reflect(-V, H);
            L = SafeNormalize(L);

            float NdotL = Saturate(Vector3.Dot(N, L));
            float NdotV = Saturate(Vector3.Dot(N, V));
            float NdotH = Saturate(Vector3.Dot(N, H));
            float VdotH = Saturate(Vector3.Dot(V, H));

            if (Vector3.Dot(L, Ng) <= 0f)
            {
                hitInfo.debugInvalidBelowSurface = true;
                return;
            }
            if (NdotL <= 1e-6f || NdotV <= 1e-6f || VdotH <= 1e-6f)
            {
                hitInfo.debugInvalidBRDF = true;
                return;
            }

            ray.direction = L;
        }
        else
        {
            Vector2 xi = new Vector2(UnityEngine.Random.value, UnityEngine.Random.value);
            Vector3 L = ToWorld(SampleCosineHemisphere(xi), N);
            if (Vector3.Dot(L, Ng) <= 0f)
            {
                hitInfo.debugInvalidBelowSurface = true;
                return;
            }

            ray.direction = L;
        }

        ray.direction = SafeNormalize(ray.direction);

        ray.origin = hitInfo.hitPoint + Ng * (1e-4f);
    }

    CPUHitInfo queryRayCollisions(ref Ray ray)
    {
        RayTracedMesh[] meshObjects = FindObjectsOfType<RayTracedMesh>();
        float closestIntersectedDistance = float.MaxValue;
        CPUHitInfo bestHit = new CPUHitInfo { hitDistance = float.MaxValue, boundsHitData = new List<BoundsHitData>() };
        foreach (RayTracedMesh mesh in meshObjects)
        {
            BVHNode root = mesh.BVH.root;
            float dist = float.MaxValue;
            if (TransformBounds(root.bounds, mesh.transform).IntersectRay(ray, out dist))
            {
                if (dist < bestHit.hitDistance)
                {
                    CheckTriangleCollisions(root, ref bestHit, ref ray, mesh);
                }
            }
            
        }

        return bestHit;

    }

    void CheckTriangleCollisions(BVHNode node, ref CPUHitInfo best, ref Ray ray, RayTracedMesh mesh)
    {
        if (node == null)
        {
            return;
        }
        best.BVHNodesSearched++;
        Bounds BVHBounds = TransformBounds(node.bounds, mesh.transform);
        BoundsHitData data = new BoundsHitData();
        data.bounds = BVHBounds;
        data.isLeaf = false;
        data.node = node;
        if (node.left == null && node.right == null)
        {
            data.isLeaf = true;
            performTriangleTest(ref best, ref ray, node, mesh);
        }

        best.boundsHitData.Add(data);
        float leftDist = float.MaxValue;
        bool leftHit = false;
        if (node.left != null)
        {
            leftHit = TransformBounds(node.left.bounds, mesh.transform).IntersectRay(ray, out leftDist);
        }
        float rightDist = float.MaxValue;
        bool rightHit = false;
        if (node.right != null)
        {
            rightHit = TransformBounds(node.right.bounds, mesh.transform).IntersectRay(ray, out rightDist);
        }

        if (leftDist < rightDist && leftHit && leftDist < best.hitDistance)
        {
            CheckTriangleCollisions(node.left, ref best, ref ray, mesh);
            if (rightHit && rightDist < best.hitDistance)
            {
                CheckTriangleCollisions(node.right, ref best, ref ray, mesh);
            }
        }
        
        else if (rightDist < leftDist && rightHit && rightDist < best.hitDistance)
        {
            CheckTriangleCollisions(node.right, ref best, ref ray, mesh);
            if (leftHit && leftDist < best.hitDistance)
            {
                CheckTriangleCollisions(node.left, ref best, ref ray, mesh);
            }
        }
        
        else if (leftHit && leftDist < best.hitDistance)
        {
            CheckTriangleCollisions(node.left, ref best, ref ray, mesh);
        }
        
        else if (rightHit && rightDist < best.hitDistance)
        {
            CheckTriangleCollisions(node.right, ref best, ref ray, mesh);
        }
    }

    void performTriangleTest(ref CPUHitInfo hit, ref Ray ray, BVHNode node, RayTracedMesh mesh)
    {
        int triangleStartIndex = node.firstTriangleIndex;
        int triangleCount = node.triangleCount;
        int[] triIndices = mesh.BVH.triIndexArray;
        buildTri[] triangles = mesh.buildTriangles;
        int end = triangleStartIndex + triangleCount;
        for (int i = triangleStartIndex; i < end; i++)
        {
            hit.triangleTests++;
            buildTri tri = triangles[triIndices[i]];
            int baseIndex = tri.triangleIndex;
            int v1 = mesh.mesh.triangles[baseIndex];
            int v2 = mesh.mesh.triangles[baseIndex + 1];
            int v3 = mesh.mesh.triangles[baseIndex + 2];
            Vector3 edgeAB = mesh.transform.TransformVector(mesh.mesh.vertices[v2] - mesh.mesh.vertices[v1]);
            Vector3 edgeAC = mesh.transform.TransformVector(mesh.mesh.vertices[v3] - mesh.mesh.vertices[v1]);
            Vector3 vertex1 = mesh.transform.TransformPoint(mesh.mesh.vertices[v1]);
            Vector3 n1 = mesh.transform.TransformDirection(mesh.mesh.normals[v1]);
            Vector3 n2 = mesh.transform.TransformDirection(mesh.mesh.normals[v2]);
            Vector3 n3 = mesh.transform.TransformDirection(mesh.mesh.normals[v3]);

            Vector3 ao = ray.origin - vertex1;

            Vector3 normalVector = Vector3.Cross(edgeAB, edgeAC);
            Vector3 geomNormal = SafeNormalize(normalVector);
            if (Vector3.Dot(geomNormal, ray.direction) > 0f)
            {
                geomNormal = -geomNormal;
            }

            float det = -Vector3.Dot(ray.direction, normalVector);

            if (Mathf.Abs(det) <= 1e-8f)
            {
                continue;
            }

            float invDet = 1f / det;

            Vector3 dao = Vector3.Cross(ao, ray.direction);
            float dst = Vector3.Dot(ao, normalVector) * invDet;

            if (dst < 0.0f)
            {
                continue;
            }

            float u = Vector3.Dot(edgeAC, dao) * invDet;
            if (u < 0.0f)
            {
                continue;
            }

            float v = -Vector3.Dot(edgeAB, dao) * invDet;
            if (v < 0.0f)
            {
                continue;
            }

            float w = 1f - u - v;
            if (w < 0.0f)
            {
                continue;
            }

            if (dst < hit.hitDistance)
            {
                hit.hitDistance = dst;
                hit.hitNode = node;
                hit.hitPoint = ray.origin + ray.direction * dst;
                hit.material = mesh.material;
                hit.geometricNormal = geomNormal;
                Vector3 shadingNormal = Vector3.Normalize(n1 * w + n2 * u + n3 * v);
                if (Vector3.Dot(shadingNormal, geomNormal) < 0f)
                {
                    shadingNormal = -shadingNormal;
                }
                hit.shadingNormal = shadingNormal;
            }
        }
    }
    
    public static Bounds TransformBounds(Bounds localBounds, Transform t)
    {
        Matrix4x4 m = t.localToWorldMatrix;

        Vector3 center = m.MultiplyPoint3x4(localBounds.center);
        Vector3 extents = localBounds.extents;

        Vector3 axisX = m.MultiplyVector(new Vector3(extents.x, 0, 0));
        Vector3 axisY = m.MultiplyVector(new Vector3(0, extents.y, 0));
        Vector3 axisZ = m.MultiplyVector(new Vector3(0, 0, extents.z));

        Vector3 worldExtents = new Vector3(
            Mathf.Abs(axisX.x) + Mathf.Abs(axisY.x) + Mathf.Abs(axisZ.x),
            Mathf.Abs(axisX.y) + Mathf.Abs(axisY.y) + Mathf.Abs(axisZ.y),
            Mathf.Abs(axisX.z) + Mathf.Abs(axisY.z) + Mathf.Abs(axisZ.z)
        );

        return new Bounds(center, worldExtents * 2f);
    }
    
}
