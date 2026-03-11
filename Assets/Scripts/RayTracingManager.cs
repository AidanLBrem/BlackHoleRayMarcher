using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using Debug = UnityEngine.Debug;
using UnityEngine.Profiling;
using static RaytracerCPURay;
[ExecuteAlways, ImageEffectAllowedInSceneView]
public class RayTracingManager : MonoBehaviour
{
    [SerializeField] bool useShaderInSceneView = true;
    [SerializeField] Shader rayTracingShader;
    [SerializeField] Shader accumulatorShader;
    Material rayTracingMaterial;
    Material accumulatorMaterial;
    public int marchStepsCount;
    public int renderDistance;
    public int raysPerPixel;
    public int maxBounces;
    public bool accumlateInSceneView = true;
    public bool accumulateInGameView = true;
    public float blackHoleSOIStepSize = 0.01f;
    ComputeBuffer sphereBuffer;
    ComputeBuffer blackHoleBuffer;

    ComputeBuffer meshBuffer;
    ComputeBuffer MeshVerticesBuffer;
    ComputeBuffer MeshNormalsBuffer;
    ComputeBuffer MeshIndicesBuffer;
    ComputeBuffer TriangleBuffer;
    ComputeBuffer BVHBuffer;
    RenderTexture resultTexture;
    int numRenderedFrames = 0;

    public int emergencyBreakMaxSteps = 1000;
    public float numFrames = 10000;
    bool stopRendering = false;
    public float accumWeight = 1.0f;

    public bool renderSphere = true;
    public bool renderTriangles = true;
    const float G = 1.975813844e-32f;
    const float C = 0.430467210276f;
    public Texture2D blackHoleBendLUT;

    public float rMinOverRs = 1.02f;
    public float rMaxOverRs = 100f;
    public float logEpsilonOverRs = 0.001f;
    public bool use_lut = true;
    public bool enable_lensing = true;
    public float bendStrength = 1.27f;
    static List<Vector3> tV = new();

    private static List<Vector3> tN = new();

    void ApplyBlackHoleLUT(Material rayTracingMaterial, RayTracedBlackHole blackHole)
    {
        BlackHoleBendLUTGenerator gen = blackHole.transform.GetComponent<BlackHoleBendLUTGenerator>();
        rMinOverRs = gen.rMinOverRs;
        rMaxOverRs = gen.rMaxOverRs;
        logEpsilonOverRs = gen.logEpsilonOverRs;
        rayTracingMaterial.SetTexture("_BlackHoleBendLUT", blackHoleBendLUT);
        rayTracingMaterial.SetFloat("_BHLUT_MuResolution", gen.muResolution);
        rayTracingMaterial.SetFloat("_BHLUT_RadiusResolution", gen.radiusResolution);
        rayTracingMaterial.SetFloat("_BHLUT_RMinOverRs", rMinOverRs);
        rayTracingMaterial.SetFloat("_BHLUT_RMaxOverRs", rMaxOverRs);
        rayTracingMaterial.SetFloat("_BHLUT_LogEpsilonOverRs", logEpsilonOverRs);
        rayTracingMaterial.SetFloat("bendStrength", bendStrength);
    }

    void OnValidate()
    {
        if (rayTracingShader != null)
        {
            ShaderHelper.InitMaterial(rayTracingShader, ref rayTracingMaterial);
        }

        if (accumulatorShader != null)
        {
            ShaderHelper.InitMaterial(accumulatorShader, ref accumulatorMaterial);
        }

        numRenderedFrames = 0;
    }

    void OnRenderImage(RenderTexture source, RenderTexture target)
    {
        if (Camera.current.name != "SceneCamera" || useShaderInSceneView)
        {
            InitFrame();
            if (stopRendering)
            {
                Graphics.Blit(resultTexture, target);
                return;
            }

            RenderTexture prevFrame =
                RenderTexture.GetTemporary(source.width, source.height, 0, ShaderHelper.RGBA_SFloat);
            Graphics.Blit(resultTexture, prevFrame);

            rayTracingMaterial.SetInt("numRenderedFrames", numRenderedFrames);
            RenderTexture currentFrame =
                RenderTexture.GetTemporary(source.width, source.height, 0, ShaderHelper.RGBA_SFloat);

            Graphics.Blit(null, currentFrame, rayTracingMaterial);

            accumulatorMaterial.SetInt("numRenderedFrames", numRenderedFrames);
            accumulatorMaterial.SetTexture("_MainTexOld", prevFrame);
            Graphics.Blit(currentFrame, resultTexture, accumulatorMaterial);
            Graphics.Blit(resultTexture, target);

            if (Camera.current.name == "SceneCamera" && !accumlateInSceneView)
            {
                Graphics.Blit(currentFrame, target);
            }

            if (Camera.current.name != "SceneCamera" && !accumulateInGameView)
            {
                Graphics.Blit(currentFrame, target);
            }

            if (Camera.current.name != "SceneCamera")
            {
                numRenderedFrames++;
                if (numRenderedFrames % 100 == 0)
                {
                    Debug.Log("Num Rendered Frames: " + numRenderedFrames);
                }

                if (numRenderedFrames > numFrames)
                {
                    // stopRendering = true;
                }
            }

            RenderTexture.ReleaseTemporary(prevFrame);
            RenderTexture.ReleaseTemporary(currentFrame);
        }
        else
        {
            Graphics.Blit(source, target);
        }
    }

    void InitFrame()
    {
        Camera.current.cullingMask = 0;
        ShaderHelper.InitMaterial(accumulatorShader, ref accumulatorMaterial);
        ShaderHelper.InitMaterial(rayTracingShader, ref rayTracingMaterial);
        ShaderHelper.CreateRenderTexture(ref resultTexture, Screen.width, Screen.height, FilterMode.Bilinear,
            ShaderHelper.RGBA_SFloat, "Result");

        var cam = Camera.current;
        UpdateCameraParams(cam);
        UpdateShaderValues();
        allocateMeshBuffer();
        allocateSphereBuffer();
        allocateBlackHoleBuffer();
    }

    void UpdateCameraParams(Camera camera)
    {
        float planeHeight = camera.nearClipPlane * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 2f;
        float planeWidth = planeHeight * camera.aspect;
        rayTracingMaterial.SetVector("ViewParams", new Vector3(planeWidth, planeHeight, camera.nearClipPlane));
        rayTracingMaterial.SetMatrix("CameraLocalToWorldMatrix", camera.transform.localToWorldMatrix);
        rayTracingMaterial.SetFloat("CameraNear", camera.nearClipPlane);
        rayTracingMaterial.SetFloat("CameraFar", renderDistance);
        rayTracingMaterial.SetVector("CameraWorldPos", camera.transform.position);
        rayTracingMaterial.SetInt("RaysPerPixel", raysPerPixel);
        rayTracingMaterial.SetInt("maxBounces", maxBounces);

        if (blackHoleSOIStepSize == 0)
        {
            blackHoleSOIStepSize = 1.0f;
        }

        rayTracingMaterial.SetFloat("stepSize", blackHoleSOIStepSize);
        rayTracingMaterial.SetInt("emergencyBreakMaxSteps", emergencyBreakMaxSteps);
        accumulatorMaterial.SetFloat("accumWeight", accumWeight);

        if (renderSphere) rayTracingMaterial.EnableKeyword("TEST_SPHERE");
        else rayTracingMaterial.DisableKeyword("TEST_SPHERE");

        if (renderTriangles) rayTracingMaterial.EnableKeyword("TEST_TRIANGLE");
        else rayTracingMaterial.DisableKeyword("TEST_TRIANGLE");

        if (use_lut) rayTracingMaterial.EnableKeyword("USE_LUT");
        else rayTracingMaterial.DisableKeyword("USE_LUT");

        if (enable_lensing) rayTracingMaterial.EnableKeyword("ENABLE_LENSING");
        else rayTracingMaterial.DisableKeyword("ENABLE_LENSING");
    }

    void allocateMeshBuffer()
    {
        long before = Profiler.GetMonoUsedSizeLong();

        RayTracedMesh[] allMeshes = FindObjectsOfType<RayTracedMesh>();
        List<RayTracedMesh> validMeshes = new List<RayTracedMesh>(allMeshes.Length);

        for (int i = 0; i < allMeshes.Length; i++)
        {
            RayTracedMesh m = allMeshes[i];
            if (m == null || m.mesh == null || m.buildTriangles == null || m.blas == null)
                continue;
            if (m.blas.Nodes == null || m.blas.Nodes.Length == 0)
                continue;
            if (m.blas.PrimitiveRefs == null || m.blas.PrimitiveRefs.Length == 0)
                continue;
            if (m.GPUBVH == null || m.GPUBVH.Count == 0)
                continue;

            validMeshes.Add(m);
        }

        RayTracedMesh[] meshObjects = validMeshes.ToArray();
        MeshStruct[] meshes = new MeshStruct[meshObjects.Length];

        int indexOffset = 0;
        int vertexAndNormalOffset = 0;
        int triangleIndicesOffset = 0;
        int totalVertexCount = 0;

        int totalTris = 0;
        int currentBVHNodeIndex = 0;
        int totalBVHNodes = getTotalBVHNodes(meshObjects);
        int degenerateTriangles = 0;

        GPUBVHNode[] BVHNodes = new GPUBVHNode[totalBVHNodes];
        bool anyMeshMarkedUpdated = false;
        
        foreach(RayTracedMesh mesh in validMeshes)
        {
            Bounds localBounds = mesh.blas.Nodes[mesh.blas.RootIndex].bounds;
            Bounds worldBounds = TransformBoundsToWorld(localBounds, mesh.transform);
        }

        for (int i = 0; i < meshObjects.Length; i++)
        {
            var m = meshObjects[i];
            uint triCount = (uint)m.buildTriangles.Length;
            totalTris += (int)triCount;
            if (m.update) anyMeshMarkedUpdated = true;
            m.update = false;
        }

        bool needMeshes = meshBuffer == null || meshBuffer.count != Mathf.Max(1, meshes.Length) || anyMeshMarkedUpdated;
        bool needTriangles = TriangleBuffer == null || TriangleBuffer.count != Mathf.Max(1, totalTris) || anyMeshMarkedUpdated;
        bool needBVH = BVHBuffer == null
                       || BVHBuffer.count != Mathf.Max(1, totalBVHNodes)
                       || BVHBuffer.stride != ShaderHelper.GetStride<GPUBVHNode>()
                       || anyMeshMarkedUpdated;

        if (needMeshes || needTriangles || needBVH)
        {
            float startTime = Time.realtimeSinceStartup;
            Debug.Log($"Updating mesh buffers: meshes={meshes.Length}, totalTris={totalTris}, totalBVHNodes={totalBVHNodes}");

            Triangle[] triangles = new Triangle[totalTris];
            uint[] triangleIndices = new uint[totalTris * 3];

            for (int i = 0; i < meshObjects.Length; i++)
            {
                totalVertexCount += meshObjects[i].numVertices;
            }

            List<float3> vertices = new List<float3>(totalVertexCount);
            List<float3> normals = new List<float3>(totalVertexCount);

            for (int i = 0; i < meshObjects.Length; i++)
            {
                RayTracedMesh meshObj = meshObjects[i];

                meshObj.mesh.GetVertices(tV);
                meshObj.mesh.GetNormals(tN);

                foreach (Vector3 v in tV)
                    vertices.Add(v);

                foreach (Vector3 n in tN)
                    normals.Add(n);

                int[] meshTriangles = meshObj.mesh.triangles;

                int bvhStart = currentBVHNodeIndex;
                if (meshObj.GPUBVH == null || meshObj.GPUBVH.Count == 0)
                {
                    Debug.LogError("No BVH for mesh " + meshObj.name);
                }

                for (int j = 0; j < meshObj.GPUBVH.Count; j++)
                {
                    GPUBVHNode node = meshObj.GPUBVH[j];

                    if (node.left != -1) node.left += bvhStart;
                    if (node.right != -1) node.right += bvhStart;

                    node.firstIndex += (uint)indexOffset;
                    BVHNodes[currentBVHNodeIndex++] = node;
                }

                int[] order = meshObj.blas.PrimitiveRefs;
                for (int t = 0; t < order.Length; t++)
                {
                    int triId = order[t];
                    ref buildTri bt = ref meshObj.buildTriangles[triId];

                    int baseIndex = bt.triangleIndex;
                    int v1 = meshTriangles[baseIndex + 0];
                    int v2 = meshTriangles[baseIndex + 1];
                    int v3 = meshTriangles[baseIndex + 2];

                    Vector3 edgeAB = bt.posB - bt.posA;
                    Vector3 edgeAC = bt.posC - bt.posA;

                    int triIndexBase = triangleIndicesOffset + t * 3;
                    Vector3 normal = Vector3.Cross(edgeAB, edgeAC);

                    if (normal.magnitude <= 1e-8f)
                    {
                        Debug.Log("ERROR, DEGENERATE TRIANGLE: " + tV[v1] + " " + tV[v2] + " " + tV[v3] + " " + bt.posA + " " + bt.posB + " " + bt.posC + " " + normal.magnitude);
                        degenerateTriangles++;
                    }

                    triangles[indexOffset] = new Triangle()
                    {
                        baseIndex = (uint)triIndexBase,
                        edgeAB = edgeAB,
                        edgeAC = edgeAC,
                        normal = normal
                    };

                    triangleIndices[triIndexBase + 0] = (uint)(vertexAndNormalOffset + v1);
                    triangleIndices[triIndexBase + 1] = (uint)(vertexAndNormalOffset + v2);
                    triangleIndices[triIndexBase + 2] = (uint)(vertexAndNormalOffset + v3);

                    indexOffset++;
                }

                vertexAndNormalOffset += tV.Count;
                triangleIndicesOffset += order.Length * 3;
                tV.Clear();
                tN.Clear();
            }

            float endTime = Time.realtimeSinceStartup;
            Debug.Log("Time taken to assemble buffers: " + (endTime - startTime) + " ms");

            PerfTimer.Time("StructuredVertexBufferCreation", () => ShaderHelper.CreateStructuredBuffer(ref MeshVerticesBuffer, vertices));
            ShaderHelper.CreateStructuredBuffer(ref MeshNormalsBuffer, normals);
            ShaderHelper.CreateStructuredBuffer(ref meshBuffer, meshes);
            PerfTimer.Time("StructuredMeshBufferCreation: ", () => ShaderHelper.CreateStructuredBuffer(ref TriangleBuffer, triangles));
            ShaderHelper.CreateStructuredBuffer(ref BVHBuffer, BVHNodes);
            ShaderHelper.CreateStructuredBuffer(ref MeshIndicesBuffer, triangleIndices);

            rayTracingMaterial.SetBuffer("Triangles", TriangleBuffer);
            rayTracingMaterial.SetBuffer("BVHNodes", BVHBuffer);
            rayTracingMaterial.SetBuffer("Vertices", MeshVerticesBuffer);
            rayTracingMaterial.SetBuffer("Normals", MeshNormalsBuffer);
            rayTracingMaterial.SetBuffer("TriangleIndices", MeshIndicesBuffer);
            Debug.Log("Warning: " + degenerateTriangles + " degenerate triangles detected");
        }

        if (meshBuffer != null && meshObjects.Length > 0)
        {
            MeshStruct[] updatedMeshes = new MeshStruct[meshObjects.Length];
            int triOffset = 0;
            int bvhOffset = 0;

            for (int i = 0; i < meshObjects.Length; i++)
            {
                var meshObj = meshObjects[i];
                var blas = meshObj.blas;

                int rootIndex = blas.RootIndex;
                Bounds localRoot = (rootIndex >= 0 && rootIndex < blas.Nodes.Length)
                    ? blas.Nodes[rootIndex].bounds
                    : meshObj.ModelBounds;

                updatedMeshes[i] = new MeshStruct()
                {
                    indexOffset = triOffset,
                    triangleCount = meshObj.buildTriangles.Length,
                    material = meshObj.material,

                    AABBLeftX = localRoot.min.x,
                    AABBLeftY = localRoot.min.y,
                    AABBLeftZ = localRoot.min.z,
                    AABBRightX = localRoot.max.x,
                    AABBRightY = localRoot.max.y,
                    AABBRightZ = localRoot.max.z,

                    firstBVHNodeIndex = bvhOffset,
                    largestAxis = meshObj.largestAxis,
                    localToWorld = meshObj.transform.localToWorldMatrix,
                    worldToLocal = meshObj.transform.worldToLocalMatrix,
                };

                triOffset += meshObj.buildTriangles.Length;
                bvhOffset += meshObj.GPUBVH.Count;
            }

            meshBuffer.SetData(updatedMeshes);
        }

        rayTracingMaterial.SetBuffer("Meshes", meshBuffer);
        rayTracingMaterial.SetInt("numMeshes", meshObjects.Length);
    }

    void UpdateShaderValues()
    {
        rayTracingMaterial.SetInt("MarchStepsCount", marchStepsCount);
    }

    void allocateSphereBuffer()
    {
        RayTracedSphere[] sphereObjects = FindObjectsOfType<RayTracedSphere>();
        Sphere[] spheres = new Sphere[sphereObjects.Length];

        for (int i = 0; i < sphereObjects.Length; i++)
        {
            spheres[i] = new Sphere()
            {
                position = sphereObjects[i].transform.position,
                radius = sphereObjects[i].transform.localScale.x * 0.5f,
                material = sphereObjects[i].material
            };
        }

        ShaderHelper.CreateStructuredBuffer(ref sphereBuffer, spheres);
        rayTracingMaterial.SetBuffer("Spheres", sphereBuffer);
        rayTracingMaterial.SetInt("numSpheres", sphereObjects.Length);
    }

    void allocateBlackHoleBuffer()
    {
        RayTracedBlackHole[] blackHoleObjects = FindObjectsOfType<RayTracedBlackHole>();
        BlackHole[] blackHoles = new BlackHole[blackHoleObjects.Length];

        for (int i = 0; i < blackHoleObjects.Length; i++)
        {
            blackHoles[i] = new BlackHole()
            {
                position = blackHoleObjects[i].transform.position,
                radius = blackHoleObjects[i].transform.localScale.x * 0.5f,
                blackHoleSOIMultiplier = blackHoleObjects[i].blackHoleSOIMultiplier,
                blackHoleMass = blackHoleObjects[i].transform.localScale.x * 0.5f,
            };

            if (blackHoles[i].blackHoleSOIMultiplier <= 0)
            {
                Debug.LogError("BlackHoleSOIMultiplier is less than or equal to 0 for " + blackHoleObjects[i].name);
                blackHoles[i].blackHoleSOIMultiplier = 1.0f;
            }

            if (blackHoles[i].blackHoleMass == 0)
            {
                Debug.LogError("BlackHoleMass is 0 for " + blackHoleObjects[i].name);
            }
        }

        if (blackHoleObjects.Length > 0)
        {
            ApplyBlackHoleLUT(rayTracingMaterial, blackHoleObjects[0]);
        }

        ShaderHelper.CreateStructuredBuffer(ref blackHoleBuffer, blackHoles);
        rayTracingMaterial.SetBuffer("BlackHoles", blackHoleBuffer);
        rayTracingMaterial.SetInt("numBlackHoles", blackHoleObjects.Length);
    }

    int getTotalBVHNodes(RayTracedMesh[] meshObjects)
    {
        int totalBVHNodes = 0;
        for (int i = 0; i < meshObjects.Length; i++)
        {
            if (meshObjects[i] != null && meshObjects[i].GPUBVH != null)
                totalBVHNodes += meshObjects[i].GPUBVH.Count;
        }
        return totalBVHNodes;
    }

    void OnEnable()
    {
        Graphics.Blit(Texture2D.blackTexture, resultTexture);
        numRenderedFrames = 0;
    }
}