using System.Collections.Generic;
using UnityEngine;
using Unity.Mathematics;
using Debug = UnityEngine.Debug;
using UnityEngine.Profiling;

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


    void OnValidate()
    {
        // This method is called when the shader is changed in the inspector
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

        //Debug.Log("Working");

        if (Camera.current.name != "SceneCamera" || useShaderInSceneView)
        {
            InitFrame();
            if (stopRendering)
            {
                Graphics.Blit(resultTexture, target);
                return;
            }

            //Store the previous frame
            RenderTexture prevFrame =
                RenderTexture.GetTemporary(source.width, source.height, 0, ShaderHelper.RGBA_SFloat);
            Graphics.Blit(resultTexture, prevFrame); //the previous frame is stored in resultTexuture

            //Render the current frame without averaging
            rayTracingMaterial.SetInt("numRenderedFrames", numRenderedFrames);
            RenderTexture currentFrame =
                RenderTexture.GetTemporary(source.width, source.height, 0, ShaderHelper.RGBA_SFloat);

            Graphics.Blit(null, currentFrame, rayTracingMaterial);

            //Accumulate step
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
                    //stopRendering = true;
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
        PerfTimer.Time("AllocateMeshBuffer", () => allocateMeshBuffer());
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

        if (renderSphere)
        {
            rayTracingMaterial.EnableKeyword("TEST_SPHERE");
        }
        else
        {
            rayTracingMaterial.DisableKeyword("TEST_SPHERE");
        }

        if (renderTriangles)
        {
            rayTracingMaterial.EnableKeyword("TEST_TRIANGLE");
        }
        else
        {
            rayTracingMaterial.DisableKeyword("TEST_TRIANGLE");
        }
    }

    void allocateMeshBuffer()
    {
        long Before = Profiler.GetMonoUsedSizeLong();
        RayTracedMesh[] meshObjects = FindObjectsOfType<RayTracedMesh>();
        MeshStruct[] meshes = new MeshStruct[meshObjects.Length];
        int indexOffset = 0;
        int vertexAndNormalOffset = 0;
        int totalVertexCount = 0;

        int totalTris = 0;
        int currentBVHNodeIndex = 0;
        int totalBVHNodes = getTotalBVHNodes(meshObjects);
        GPUBVHNode[] BVHNodes = new GPUBVHNode[totalBVHNodes];
        bool anyMeshMarkedUpdated = false;
        for (int i = 0; i < meshObjects.Length; i++) {
            var m = meshObjects[i];
            uint triCount = m.mesh.GetIndexCount(0) / 3;
            totalTris += (int)triCount;
            if (m.update) anyMeshMarkedUpdated = true;
            m.update = false;
        }
        bool needMeshes    = meshBuffer == null || meshBuffer.count != Mathf.Max(1, meshes.Length) || anyMeshMarkedUpdated;
        bool needTriangles = TriangleBuffer == null || TriangleBuffer.count != Mathf.Max(1, totalTris) || anyMeshMarkedUpdated;
        bool needBVH       = BVHBuffer == null
                      || BVHBuffer.count != Mathf.Max(1, totalBVHNodes)
                      || BVHBuffer.stride != ShaderHelper.GetStride<GPUBVHNode>()
                      || anyMeshMarkedUpdated;
        if (needMeshes || needTriangles || needBVH) {
            Debug.Log($"Updating mesh buffers: meshes={meshes.Length}, totalTris={totalTris}, totalBVHNodes={totalBVHNodes}");
            Triangle[] triangles = new Triangle[totalTris];
            for (int i = 0; i < meshObjects.Length; i++)
            {
                totalVertexCount += meshObjects[i].numVertices;
            }

            List<float3> vertices = new List<float3>(totalVertexCount);
            List<float3> normals = new List<float3>(totalVertexCount);
            for (int i = 0; i < meshObjects.Length; i++)
            {
                List<Vector3> tV = new List<Vector3>();
                List<Vector3> tN = new List<Vector3>();
                meshObjects[i].mesh.GetVertices(tV);
                meshObjects[i].mesh.GetNormals(tN);
                
                foreach(Vector3 v in tV)
                {
                    vertices.Add(v);
                }

                foreach (Vector3 N in tN)
                {
                    normals.Add(N);
                }
                
                int[] tT = meshObjects[i].mesh.triangles;
                int bvhStart = currentBVHNodeIndex; //offset BVH start index
                if (meshObjects[i].GPUBVH.Count == 0) {
                    Debug.LogError("No BVH for mesh " + meshObjects[i].name);
                }
                for (int j = 0; j < meshObjects[i].GPUBVH.Count; j++) {
                    GPUBVHNode node = meshObjects[i].GPUBVH[j];
                    if (node.left != -1) {
                        node.left += bvhStart; 
                    }
                    if (node.right != -1) {
                        node.right += bvhStart;
                    }
                    //note
                    node.firstTriangleIndex += indexOffset;
                    BVHNodes[currentBVHNodeIndex++] = node;
                }
                var bvh = meshObjects[i].GetComponent<BVHCreator>(); // or however you store it
                int[] order = bvh.triIndexArray; // must be built by BVHCreator.BuildBVH()
                for (int t = 0; t < order.Length; t++)
                {
                    int triId = order[t];                       // triangle id in original buildTriangles list
                    var bt = meshObjects[i].buildTriangles[triId];

                    int baseIndex = bt.triangleIndex;           // index into meshObjects[i].triangles list (x3 indices)
                    int v1 = tT[baseIndex];
                    int v2 = tT[baseIndex + 1];
                    int v3 = tT[baseIndex + 2];

                    Vector3 edgeAB = tV[v2] - tV[v1];
                    Vector3 edgeAC = tV[v3] - tV[v1];

                    triangles[indexOffset] = new Triangle()
                    {
                        vertex1 = tV[v1],
                        normalIndex1 = v1 + vertexAndNormalOffset,
                        normalIndex2 = v2 + vertexAndNormalOffset,
                        normalIndex3 = v3 + vertexAndNormalOffset,
                        edgeAB = edgeAB,
                        edgeAC = edgeAC,
                    };

                    indexOffset++;
                }
                vertexAndNormalOffset += tV.Count;
                tV.Clear();
                tN.Clear();
            }


            PerfTimer.Time("StructuredVertexBufferCreation", () => ShaderHelper.CreateStructuredBuffer(ref MeshVerticesBuffer, vertices));

            ShaderHelper.CreateStructuredBuffer(ref MeshNormalsBuffer, normals);

            ShaderHelper.CreateStructuredBuffer(ref meshBuffer, meshes);
            PerfTimer.Time("StructuredMeshBufferCreation: ", () => ShaderHelper.CreateStructuredBuffer(ref TriangleBuffer, triangles));
            ShaderHelper.CreateStructuredBuffer(ref BVHBuffer, BVHNodes);
            rayTracingMaterial.SetBuffer("Triangles", TriangleBuffer);
            rayTracingMaterial.SetBuffer("BVHNodes", BVHBuffer);
            rayTracingMaterial.SetBuffer("Vertices", MeshVerticesBuffer);
            rayTracingMaterial.SetBuffer("Normals", MeshNormalsBuffer);
        }
        
        // Update worldPos every frame (meshes can move) - always rebuild meshes array with current positions
        if (meshBuffer != null && meshObjects.Length > 0) {
            MeshStruct[] updatedMeshes = new MeshStruct[meshObjects.Length];
            int meshIdx = 0;
            int triOffset = 0;
            int bvhOffset = 0;
            for (int i = 0; i < meshObjects.Length; i++)
            {
                var bvh = meshObjects[i].BVH;
                var localRoot = bvh.root.bounds;
                updatedMeshes[i] = new MeshStruct() {
                    indexOffset = triOffset,
                    triangleCount = meshObjects[i].buildTriangles.Length,
                    material = meshObjects[i].material,
                    AABBLeftX = localRoot.min.x,
                    AABBLeftY = localRoot.min.y,
                    AABBLeftZ = localRoot.min.z,
                    AABBRightX = localRoot.max.x,
                    AABBRightY = localRoot.min.y,
                    AABBRightZ = localRoot.min.z,
                    firstBVHNodeIndex = bvhOffset,
                    largestAxis = meshObjects[i].largestAxis,
                    localToWorld = meshObjects[i].transform.localToWorldMatrix,
                    worldToLocal = meshObjects[i].transform.worldToLocalMatrix,
                };
                triOffset += meshObjects[i].buildTriangles.Length;
                bvhOffset += meshObjects[i].GPUBVH.Count;
            }
            meshBuffer.SetData(updatedMeshes);
        }
        
        rayTracingMaterial.SetBuffer("Meshes", meshBuffer);
        rayTracingMaterial.SetInt("numMeshes", meshObjects.Length);
        long after = Profiler.GetMonoUsedSizeLong();
        Debug.Log($"Mono delta: {(after-Before)/1024f/1024f:0.0} MB");
    }

    void UpdateShaderValues() {
        rayTracingMaterial.SetInt("MarchStepsCount", marchStepsCount);
    }

    void allocateSphereBuffer() {
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

		// Create buffer containing all sphere data, and send it to the shader
		ShaderHelper.CreateStructuredBuffer(ref sphereBuffer, spheres);
		rayTracingMaterial.SetBuffer("Spheres", sphereBuffer);
		rayTracingMaterial.SetInt("numSpheres", sphereObjects.Length);
    }



    void allocateBlackHoleBuffer() {
        RayTracedBlackHole[] blackHoleObjects = FindObjectsOfType<RayTracedBlackHole>();
        BlackHole[] blackHoles = new BlackHole[blackHoleObjects.Length];

		for (int i = 0; i < blackHoleObjects.Length; i++)
		{
			blackHoles[i] = new BlackHole()
			{
				position = blackHoleObjects[i].transform.position,
				radius = blackHoleObjects[i].transform.localScale.x * 0.5f,
				blackHoleSOIMultiplier = blackHoleObjects[i].blackHoleSOIMultiplier,
                blackHoleMass = (blackHoleObjects[i].transform.localScale.x * 0.5f * C * C) / (2.0f * G),
			};
            if (blackHoles[i].blackHoleSOIMultiplier <= 0) {
                Debug.LogError("BlackHoleSOIMultiplier is less than or equal to 0 for " + blackHoleObjects[i].name);
                blackHoles[i].blackHoleSOIMultiplier = 1.0f;
            }

            if (blackHoles[i].blackHoleMass == 0) {
                Debug.LogError("BlackHoleMass is 0 for " + blackHoleObjects[i].name);
            }
		}

		ShaderHelper.CreateStructuredBuffer(ref blackHoleBuffer, blackHoles);
		rayTracingMaterial.SetBuffer("BlackHoles", blackHoleBuffer);
		rayTracingMaterial.SetInt("numBlackHoles", blackHoleObjects.Length);
    }

    int getTotalBVHNodes(RayTracedMesh[] meshObjects) {
        int totalBVHNodes = 0;
        for (int i = 0; i < meshObjects.Length; i++) {
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
