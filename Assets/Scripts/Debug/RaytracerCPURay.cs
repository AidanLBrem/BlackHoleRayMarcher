using System;
using UnityEngine;
using TMPro;
using System.Collections.Generic;
using Random = System.Random;

public struct BoundsHitData
{
    public Bounds bounds;   // stored in world space for debug drawing
    public bool isLeaf;
    public int nodeIndex;
}

[System.Serializable]
public struct CPUHitInfo
{
    public float hitDistance;              // WORLD distance
    public Vector3 hitPoint;               // WORLD space
    public Vector3 shadingNormal;          // WORLD space
    public Vector3 geometricNormal;        // WORLD space
    public List<BoundsHitData> boundsHitData;
    public List<buildTri> triangleTests;   // WORLD space debug tris
    public int BVHNodesSearched;
    public int BLASLeafNodesSearched;
    public int TLASLeafNodesSearched;
    public int BLASNodesSearched;
    public int TLASNodesSearched;
    public float time;
    public int hitNodeIndex;
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

public struct MeshCollisionStruct
{
    public float distance;
    public int meshIndex;
    public Ray localRay;
}

[ExecuteAlways]
public class RaytracerCPURay : MonoBehaviour
{
    [SerializeField] private TextMeshProUGUI GUIElement;
    [SerializeField] private Transform tracerProperties;
    [SerializeField] private RayPathStruct path;

    const float DET_EPS = 1e-10f;
    const float T_EPS = 1e-6f;
    const float BARY_EPS = 1e-6f;

    const float BOUNDS_PAD = 1e-3f;

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

        if (path.hitInfos == null) return;

        foreach (CPUHitInfo intersect in path.hitInfos)
        {
            if (intersect.hitDistance < float.MaxValue)
            {
                Gizmos.color = Color.red;
                Gizmos.DrawLine(currentPosition, intersect.hitPoint);

                if (GUIElement != null)
                {
                    GUIElement.text =
                        "Triangle Test: " + intersect.triangleTests.Count +
                        "\nNode Tests: " + intersect.BVHNodesSearched +
                        "\nTime taken: " + intersect.time * 1000f;
                }

                Gizmos.color = new Color(0f, 1f, 0f, 0.5f);
                int leafCount = 0;
                for (int i = 0; i < intersect.boundsHitData.Count; i++)
                {
                    BoundsHitData data = intersect.boundsHitData[i];
                    if (intersect.hitNodeIndex == data.nodeIndex)
                    {
                        Gizmos.DrawCube(data.bounds.center, data.bounds.size);
                    }
                    /*else if (data.isLeaf)
                    {
                        Gizmos.color = new Color(0, 0, 1, ((float)++leafCount / 10f));
                        Gizmos.DrawCube(data.bounds.center, data.bounds.size);
                    }*/
                }

                Gizmos.color = Color.green;
                foreach (buildTri tri in intersect.triangleTests)
                {
                    Gizmos.DrawLine(tri.posA, tri.posB);
                    Gizmos.DrawLine(tri.posB, tri.posC);
                    Gizmos.DrawLine(tri.posA, tri.posC);
                    Gizmos.DrawSphere(tri.centroid, 0.01f);
                }

                currentPosition = intersect.hitPoint;
            }
            else
            {
                Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1000f);
                if (GUIElement != null) GUIElement.text = "No collision";
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

        RayTracingManager manager = tracerProperties.GetComponent<RayTracingManager>();
        int maxBounces = manager.maxBounces;

        Ray ray = new Ray(transform.position, transform.forward);

        for (int i = 0; i < maxBounces; i++)
        {
            CPUHitInfo intersect = QueryRayCollisionsWorld(ref ray);

            if (intersect.hitDistance == float.MaxValue)
                return toReturn;

            HandleReflection(ref ray, ref intersect);

            if (intersect.debugInvalidBelowSurface)
                toReturn.debugInvalidBelowSurfaceCount++;

            if (intersect.debugInvalidBRDF)
                toReturn.debugInvalidBRDFCount++;

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
        float totalWeight = Mathf.Max(specularWeight + diffuseWeight, 1e-8f);

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
        ray.origin = hitInfo.hitPoint + Ng * 1e-4f;
    }

    CPUHitInfo QueryRayCollisionsWorld(ref Ray worldRay)
    {
        UnityEngine.Random.InitState(0);
        RayTracingManager manager = tracerProperties.GetComponent<RayTracingManager>();

        CPUHitInfo bestHit = new CPUHitInfo
        {
            hitDistance = float.MaxValue,
            hitNodeIndex = -1,
            boundsHitData = new List<BoundsHitData>(),
            triangleTests = new List<buildTri>()
        };

        RayTracedMesh[] meshObjects = FindObjectsOfType<RayTracedMesh>();

        if (manager != null && manager.useTlas)
        {
            QueryRayCollisionsWithTLAS(ref bestHit, ref worldRay, meshObjects, manager);
        }
        else
        {
            QueryRayCollisionsWithoutTLAS(ref bestHit, ref worldRay, meshObjects);
        }

        return bestHit;
    }

    void QueryRayCollisionsWithoutTLAS(ref CPUHitInfo bestHit, ref Ray worldRay, RayTracedMesh[] meshObjects)
    {
        MeshCollisionStruct[] collisions = new MeshCollisionStruct[meshObjects.Length];
        int collisionIndex = 0;

        for (int i = 0; i < meshObjects.Length; i++)
        {
            RayTracedMesh mesh = meshObjects[i];
            if (mesh == null ||mesh.sharedMesh.blas == null || mesh.sharedMesh.blas.Nodes == null || mesh.sharedMesh.blas.Nodes.Length == 0)
                continue;

            int rootIndex = mesh.sharedMesh.blas.RootIndex;
            if (rootIndex < 0)
                continue;

            BvhNode root = mesh.sharedMesh.blas.Nodes[rootIndex];

            Vector3 localOrigin = mesh.transform.InverseTransformPoint(worldRay.origin);
            Vector3 localDirection = mesh.transform.InverseTransformDirection(worldRay.direction);

            Ray localRay = new Ray(localOrigin, localDirection);

            Bounds rootBounds = ExpandLocalBounds(root.bounds, BOUNDS_PAD);
            if (IntersectAABB(localRay, rootBounds, out float rootEnter, out float _))
            {
                MeshCollisionStruct collision = new MeshCollisionStruct();
                float worldDistance = rootEnter;
                collision.distance = worldDistance;
                collision.meshIndex = i;
                collision.localRay = localRay;
                collisions[collisionIndex++] = collision;
            }
        }

        Array.Sort(collisions, 0, collisionIndex, Comparer<MeshCollisionStruct>.Create(
            (a, b) => a.distance.CompareTo(b.distance)
        ));

        for (int i = 0; i < collisionIndex; i++)
        {
            ref MeshCollisionStruct collision = ref collisions[i];
            if (collision.distance > bestHit.hitDistance)
                return;

            RayTracedMesh mesh = meshObjects[collision.meshIndex];
            CheckTriangleCollisionsLocal(mesh.sharedMesh.blas.RootIndex, ref bestHit, ref collision.localRay, ref worldRay, mesh);
        }
    }

    void QueryRayCollisionsWithTLAS(ref CPUHitInfo bestHit, ref Ray worldRay, RayTracedMesh[] meshObjects, RayTracingManager manager)
    {
        BvhInstance[] instances = BuildCPUInstances(meshObjects);
        if (instances.Length == 0)
            return;

        TLASBuilder tlasBuilder = new TLASBuilder();
        BvhBuildSettings settings = new BvhBuildSettings
        {
            maxLeafSize = manager.tlasMaxLeafSize,
            maxDepth = manager.tlasMaxDepth,
            numBins = manager.tlasNumBins
        };

        tlasBuilder.Build(instances, settings);

        if (tlasBuilder.Nodes == null || tlasBuilder.Nodes.Length == 0 || tlasBuilder.RootIndex < 0)
            return;

        TraverseTLASNode(
            tlasBuilder.RootIndex,
            tlasBuilder,
            instances,
            meshObjects,
            ref bestHit,
            ref worldRay
        );
    }

    BvhInstance[] BuildCPUInstances(RayTracedMesh[] meshObjects)
    {
        List<BvhInstance> instances = new List<BvhInstance>(meshObjects.Length);

        for (int i = 0; i < meshObjects.Length; i++)
        {
            RayTracedMesh mesh = meshObjects[i];
            if (mesh == null || mesh.sharedMesh.blas == null || mesh.sharedMesh.blas.Nodes == null || mesh.sharedMesh.blas.Nodes.Length == 0)
                continue;

            int localRootIndex = mesh.sharedMesh.blas.RootIndex;
            if (localRootIndex < 0)
                continue;

            Bounds localRootBounds = mesh.sharedMesh.blas.Nodes[localRootIndex].bounds;
            Bounds worldBounds = TransformBoundsToWorld(localRootBounds, mesh.transform);

            instances.Add(new BvhInstance
            {
                blasIndex = i,
                blasRootIndex = localRootIndex,
                localToWorld = mesh.transform.localToWorldMatrix,
                worldToLocal = mesh.transform.worldToLocalMatrix,
                localBounds = localRootBounds,
                worldBounds = worldBounds,
                materialIndex = i
            });
        }

        return instances.ToArray();
    }

    void TraverseTLASNode(
        int nodeIndex,
        TLASBuilder tlasBuilder,
        BvhInstance[] instances,
        RayTracedMesh[] meshObjects,
        ref CPUHitInfo bestHit,
        ref Ray worldRay)
    {
        if (nodeIndex < 0 || nodeIndex >= tlasBuilder.Nodes.Length)
            return;

        BvhNode node = tlasBuilder.Nodes[nodeIndex];

        Bounds expandedNodeBounds = node.bounds;
        expandedNodeBounds.Expand(BOUNDS_PAD);

        if (!IntersectAABB(worldRay, expandedNodeBounds, out float nodeEnter, out float _))
            return;

        if (bestHit.hitDistance < float.MaxValue && nodeEnter > bestHit.hitDistance)
            return;

        bestHit.BVHNodesSearched++;
        bestHit.TLASNodesSearched++;

        bool isLeaf = node.IsLeaf;

        bestHit.boundsHitData.Add(new BoundsHitData
        {
            bounds = expandedNodeBounds,
            isLeaf = isLeaf,
            nodeIndex = nodeIndex
        });

        if (isLeaf)
        {
            bestHit.TLASLeafNodesSearched++;
            TraverseTLASLeaf(node, tlasBuilder, instances, meshObjects, ref bestHit, ref worldRay);
            return;
        }

        int leftIndex = node.leftChild;
        int rightIndex = node.rightChild;

        bool leftHit = false;
        bool rightHit = false;
        float leftEnter = float.MaxValue, rightEnter = float.MaxValue;

        if (leftIndex >= 0)
        {
            Bounds leftBounds = tlasBuilder.Nodes[leftIndex].bounds;
            leftBounds.Expand(BOUNDS_PAD);
            leftHit = IntersectAABB(worldRay, leftBounds, out leftEnter, out _);
        }

        if (rightIndex >= 0)
        {
            Bounds rightBounds = tlasBuilder.Nodes[rightIndex].bounds;
            rightBounds.Expand(BOUNDS_PAD);
            rightHit = IntersectAABB(worldRay, rightBounds, out rightEnter, out _);
        }

        if (leftHit && bestHit.hitDistance < float.MaxValue && leftEnter > bestHit.hitDistance)
            leftHit = false;

        if (rightHit && bestHit.hitDistance < float.MaxValue && rightEnter > bestHit.hitDistance)
            rightHit = false;

        if (leftHit && rightHit)
        {
            if (leftEnter <= rightEnter)
            {
                TraverseTLASNode(leftIndex, tlasBuilder, instances, meshObjects, ref bestHit, ref worldRay);
                TraverseTLASNode(rightIndex, tlasBuilder, instances, meshObjects, ref bestHit, ref worldRay);
            }
            else
            {
                TraverseTLASNode(rightIndex, tlasBuilder, instances, meshObjects, ref bestHit, ref worldRay);
                TraverseTLASNode(leftIndex, tlasBuilder, instances, meshObjects, ref bestHit, ref worldRay);
            }
        }
        else if (leftHit)
        {
            TraverseTLASNode(leftIndex, tlasBuilder, instances, meshObjects, ref bestHit, ref worldRay);
        }
        else if (rightHit)
        {
            TraverseTLASNode(rightIndex, tlasBuilder, instances, meshObjects, ref bestHit, ref worldRay);
        }
    }

    void TraverseTLASLeaf(
    BvhNode leafNode,
    TLASBuilder tlasBuilder,
    BvhInstance[] instances,
    RayTracedMesh[] meshObjects,
    ref CPUHitInfo bestHit,
    ref Ray worldRay)
    {
        int start = leafNode.start;
        int count = leafNode.count;

        TLASCandidate[] candidates = new TLASCandidate[count];
        int candidateCount = 0;

        for (int i = start; i < start + count; i++)
        {
            int instanceRef = tlasBuilder.PrimitiveRefs[i];
            if (instanceRef < 0 || instanceRef >= instances.Length)
                continue;

            BvhInstance instance = instances[instanceRef];
            int meshIndex = instance.blasIndex;

            if (meshIndex < 0 || meshIndex >= meshObjects.Length)
                continue;

            RayTracedMesh mesh = meshObjects[meshIndex];
            if (mesh == null || mesh.sharedMesh.blas == null || mesh.sharedMesh.blas.Nodes == null || mesh.sharedMesh.blas.Nodes.Length == 0)
                continue;

            // Coarse world-space TLAS instance test
            Bounds worldBounds = instance.worldBounds;
            worldBounds.Expand(BOUNDS_PAD);

            if (!IntersectAABB(worldRay, worldBounds, out float coarseEnter, out float _))
                continue;

            if (bestHit.hitDistance < float.MaxValue && coarseEnter > bestHit.hitDistance)
                continue;

            // Tight local-space BLAS root test
            Vector3 localOrigin = mesh.transform.InverseTransformPoint(worldRay.origin);
            Vector3 localDirection = mesh.transform.InverseTransformDirection(worldRay.direction);
            Ray localRay = new Ray(localOrigin, localDirection);

            Bounds localRootBounds = ExpandLocalBounds(mesh.sharedMesh.blas.Nodes[instance.blasRootIndex].bounds, BOUNDS_PAD);
            if (!IntersectAABB(localRay, localRootBounds, out float rootEnter, out float _))
                continue;

            if (bestHit.hitDistance < float.MaxValue && rootEnter > bestHit.hitDistance)
                continue;

            candidates[candidateCount++] = new TLASCandidate
            {
                enter = rootEnter,
                instanceRef = instanceRef
            };
        }

        Array.Sort(
            candidates,
            0,
            candidateCount,
            Comparer<TLASCandidate>.Create((a, b) => a.enter.CompareTo(b.enter))
        );

        for (int i = 0; i < candidateCount; i++)
        {
            TLASCandidate candidate = candidates[i];
            if (candidate.enter > bestHit.hitDistance)
                break;

            BvhInstance instance = instances[candidate.instanceRef];
            int meshIndex = instance.blasIndex;

            if (meshIndex < 0 || meshIndex >= meshObjects.Length)
                continue;

            RayTracedMesh mesh = meshObjects[meshIndex];
            if (mesh == null || mesh.sharedMesh.blas == null || mesh.sharedMesh.blas.Nodes == null || mesh.sharedMesh.blas.Nodes.Length == 0)
                continue;

            Vector3 localOrigin = mesh.transform.InverseTransformPoint(worldRay.origin);
            Vector3 localDirection = mesh.transform.InverseTransformDirection(worldRay.direction);
            Ray localRay = new Ray(localOrigin, localDirection);

            CheckTriangleCollisionsLocal(instance.blasRootIndex, ref bestHit, ref localRay, ref worldRay, mesh);
        }
    }

    void CheckTriangleCollisionsLocal(int nodeIndex, ref CPUHitInfo best, ref Ray localRay, ref Ray worldRay, RayTracedMesh mesh)
    {
        if (mesh.sharedMesh.blas == null || mesh.sharedMesh.blas.Nodes == null)
            return;

        if (nodeIndex < 0 || nodeIndex >= mesh.sharedMesh.blas.Nodes.Length)
            return;

        BvhNode node = mesh.sharedMesh.blas.Nodes[nodeIndex];

        Bounds expandedNodeBounds = ExpandLocalBounds(node.bounds, BOUNDS_PAD);

        if (!IntersectAABB(localRay, expandedNodeBounds, out float nodeEnter, out float _))
            return;

        if (best.hitDistance < float.MaxValue && nodeEnter > best.hitDistance)
            return;

        best.BVHNodesSearched++;
        best.BLASNodesSearched++;
        bool isLeaf = node.rightChild == node.leftChild;

        best.boundsHitData.Add(new BoundsHitData
        {
            bounds = TransformBoundsToWorld(node.bounds, mesh.transform, BOUNDS_PAD),
            isLeaf = isLeaf,
            nodeIndex = nodeIndex
        });

        if (isLeaf)
        {
            best.BLASLeafNodesSearched++;
            PerformTriangleTestLocal(ref best, ref localRay, ref worldRay, nodeIndex, mesh);
            return;
        }

        int leftIndex = node.leftChild;
        int rightIndex = node.rightChild;

        bool leftHit = false;
        bool rightHit = false;
        float leftEnter = float.MaxValue, rightEnter = float.MaxValue;

        if (leftIndex >= 0)
        {
            Bounds leftBounds = ExpandLocalBounds(mesh.sharedMesh.blas.Nodes[leftIndex].bounds, BOUNDS_PAD);
            leftHit = IntersectAABB(localRay, leftBounds, out leftEnter, out _);
        }

        if (rightIndex >= 0)
        {
            Bounds rightBounds = ExpandLocalBounds(mesh.sharedMesh.blas.Nodes[rightIndex].bounds, BOUNDS_PAD);
            rightHit = IntersectAABB(localRay, rightBounds, out rightEnter, out _);
        }

        if (leftHit && best.hitDistance < float.MaxValue && leftEnter > best.hitDistance)
            leftHit = false;

        if (rightHit && best.hitDistance < float.MaxValue && rightEnter > best.hitDistance)
            rightHit = false;

        if (leftHit && rightHit)
        {
            if (leftEnter <= rightEnter)
            {
                CheckTriangleCollisionsLocal(leftIndex, ref best, ref localRay, ref worldRay, mesh);
                CheckTriangleCollisionsLocal(rightIndex, ref best, ref localRay, ref worldRay, mesh);
            }
            else
            {
                CheckTriangleCollisionsLocal(rightIndex, ref best, ref localRay, ref worldRay, mesh);
                CheckTriangleCollisionsLocal(leftIndex, ref best, ref localRay, ref worldRay, mesh);
            }
        }
        else if (leftHit)
        {
            CheckTriangleCollisionsLocal(leftIndex, ref best, ref localRay, ref worldRay, mesh);
        }
        else if (rightHit)
        {
            CheckTriangleCollisionsLocal(rightIndex, ref best, ref localRay, ref worldRay, mesh);
        }
    }

    void PerformTriangleTestLocal(ref CPUHitInfo hit, ref Ray localRay, ref Ray worldRay, int nodeIndex, RayTracedMesh mesh)
    {
        BvhNode node = mesh.sharedMesh.blas.Nodes[nodeIndex];

        int triangleStartIndex = node.start;
        int triangleCount = node.count;
        int[] triIndices = mesh.sharedMesh.blas.PrimitiveRefs;
        buildTri[] triangles = mesh.sharedMesh.buildTriangles;

        int end = triangleStartIndex + triangleCount;

        Matrix4x4 localToWorld = mesh.transform.localToWorldMatrix;
        Matrix4x4 normalMatrix = localToWorld.inverse.transpose;

        for (int i = triangleStartIndex; i < end; i++)
        {
            buildTri tri = triangles[triIndices[i]];

            buildTri temp = new buildTri();
            temp.posA = mesh.transform.TransformPoint(tri.posA);
            temp.posB = mesh.transform.TransformPoint(tri.posB);
            temp.posC = mesh.transform.TransformPoint(tri.posC);
            temp.centroid = mesh.transform.TransformPoint(tri.centroid);
            hit.triangleTests.Add(temp);

            Vector3 edgeAB = tri.posB - tri.posA;
            Vector3 edgeAC = tri.posC - tri.posA;
            Vector3 vertex1 = tri.posA;

            Vector3 normalVector = Vector3.Cross(edgeAB, edgeAC);
            float normalLenSq = Vector3.Dot(normalVector, normalVector);
            if (normalLenSq < 1e-16f)
                continue;

            Vector3 ao = localRay.origin - vertex1;

            float det = -Vector3.Dot(localRay.direction, normalVector);
            if (Mathf.Abs(det) < DET_EPS)
                continue;

            float invDet = 1f / det;

            float dst = Vector3.Dot(ao, normalVector) * invDet;
            if (dst < T_EPS)
                continue;

            if (dst > hit.hitDistance)
                continue;

            Vector3 dao = Vector3.Cross(ao, localRay.direction);

            float u = Vector3.Dot(edgeAC, dao) * invDet;
            if (u < -BARY_EPS)
                continue;

            float v = -Vector3.Dot(edgeAB, dao) * invDet;
            if (v < -BARY_EPS)
                continue;

            float w = 1f - u - v;
            if (w < -BARY_EPS)
                continue;

            Vector3 localHitPoint = localRay.origin + localRay.direction * dst;
            Vector3 worldHitPoint = mesh.transform.TransformPoint(localHitPoint);
            float worldDistance = dst;

            if (worldDistance > hit.hitDistance)
                continue;

            Vector3 geomNormalLocal = SafeNormalize(normalVector);
            if (Vector3.Dot(geomNormalLocal, localRay.direction) > 0f)
                geomNormalLocal = -geomNormalLocal;

            Vector3 shadingNormalLocal = SafeNormalize(tri.n1 * w + tri.n2 * u + tri.n3 * v);
            if (Vector3.Dot(shadingNormalLocal, geomNormalLocal) < 0f)
                shadingNormalLocal = -shadingNormalLocal;

            Vector3 worldGeomNormal = SafeNormalize(normalMatrix.MultiplyVector(geomNormalLocal));
            Vector3 worldShadingNormal = SafeNormalize(normalMatrix.MultiplyVector(shadingNormalLocal));
            
            hit.hitDistance = worldDistance;
            hit.hitNodeIndex = nodeIndex;
            hit.hitPoint = worldHitPoint;
            hit.material = mesh.material;
            hit.geometricNormal = worldGeomNormal;
            hit.shadingNormal = worldShadingNormal;
        }
    }

    static Bounds ExpandLocalBounds(Bounds b, float pad)
    {
        b.Expand(pad);
        return b;
    }

    public static Bounds TransformBoundsToWorld(Bounds localBounds, Transform t, float pad = 0f)
    {
        localBounds.Expand(pad);

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

    static bool IntersectAABB(Ray ray, Bounds b, out float tEnter, out float tExit)
    {
        Vector3 min = b.min;
        Vector3 max = b.max;

        tEnter = 0f;
        tExit = float.PositiveInfinity;

        for (int axis = 0; axis < 3; axis++)
        {
            float origin = ray.origin[axis];
            float dir = ray.direction[axis];

            if (Mathf.Abs(dir) < 1e-12f)
            {
                if (origin < min[axis] || origin > max[axis])
                    return false;
                continue;
            }

            float invDir = 1f / dir;
            float t0 = (min[axis] - origin) * invDir;
            float t1 = (max[axis] - origin) * invDir;

            if (t0 > t1)
            {
                float tmp = t0;
                t0 = t1;
                t1 = tmp;
            }

            tEnter = Mathf.Max(tEnter, t0);
            tExit = Mathf.Min(tExit, t1);

            if (tExit < tEnter)
                return false;
        }

        return tExit >= Mathf.Max(tEnter, 0f);
    }

    struct TLASCandidate
    {
        public float enter;
        public int instanceRef;
    }
}