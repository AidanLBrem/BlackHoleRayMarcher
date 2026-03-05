using UnityEngine;
using TMPro;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine.InputSystem.HID;

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
    public List<BoundsHitData> boundsHitData;
    public int triangleTests;
    public int BVHNodesSearched;
    public float time;
    public BVHNode hitNode;
}
[ExecuteAlways]
public class RaytracerCPURay : MonoBehaviour
{
    private Vector3 rayStartPosition;
    [SerializeField] private TextMeshProUGUI GUIElement;
    [SerializeField] private Transform tracerProperties;

    [SerializeField] private CPUHitInfo intersect;
    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        
    }

    // Update is called once per frame
    void OnDrawGizmosSelected()
    {
        Gizmos.color = Color.red;
        DrawRayStartingPosition();
        if (transform.hasChanged)
        {
            float time = Time.realtimeSinceStartup;
            intersect = DrawRayPath();
            float timeAfter = Time.realtimeSinceStartup;
            intersect.time = (timeAfter - time);
            transform.hasChanged = false;
        }
        
        if (intersect.hitDistance < float.MaxValue)
        {
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * intersect.hitDistance);
            GUIElement.text = "Triangle Test: " + intersect.triangleTests + "\n Node Tests: " +
                              intersect.BVHNodesSearched + "\nTime taken: " + intersect.time * 1000f;

            int leafCount = 0;
            int nonLeafCount = 0;
            for (int i = 0; i < intersect.boundsHitData.Count; i++)
            {
                BoundsHitData data =  intersect.boundsHitData[i];
                if (intersect.hitNode == data.node)
                {
                    Gizmos.color = new Color(0f, 1f, 0f, 1f);
                    leafCount++;
                }
                else if (data.isLeaf)
                {
                    Gizmos.color = new Color(1f, 0f, 0f, 1f / (++leafCount));
                }

                else
                {
                    Gizmos.color = new Color(0f, 0f, 1f, 0.5f / (++nonLeafCount));
                }
                
                Gizmos.DrawCube(data.bounds.center, data.bounds.size);
            }
        }   

        else
        {
            Debug.Log("No collision");
            Gizmos.DrawLine(transform.position, transform.position + transform.forward * 1000f);
            GUIElement.text = "No collision";
        }

    }

    void DrawRayStartingPosition()
    {

        Gizmos.DrawSphere(transform.position, 0.25f);
    }

    CPUHitInfo DrawRayPath()
    {
        Vector3 rayPosition = transform.position;
        Vector3 rayDirection = transform.forward;
        
        RayTracingManager manager = tracerProperties.GetComponent<RayTracingManager>();
        int maxBounces = manager.maxBounces;
        int bounces = 0;
        Ray ray = new Ray(transform.position, rayDirection);
        CPUHitInfo toReturn = queryRayCollisions(ref ray);
        return toReturn;
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
