using System.Collections.Generic;
using UnityEngine;
using static UnityEngine.Mathf;
using UnityEditor;
using Unity.Mathematics;
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
    

    void OnValidate() {
        // This method is called when the shader is changed in the inspector
        if (rayTracingShader != null) {
            ShaderHelper.InitMaterial(rayTracingShader, ref rayTracingMaterial);
        }
        if (accumulatorShader != null) {
            ShaderHelper.InitMaterial(accumulatorShader, ref accumulatorMaterial);
        }

        numRenderedFrames = 0;
    }

    void OnRenderImage(RenderTexture source, RenderTexture target) {
    //Debug.Log("Working");

       if (Camera.current.name != "SceneCamera" || useShaderInSceneView) {
                float t0 = Time.realtimeSinceStartup;
        InitFrame();
        if (stopRendering) {
            Graphics.Blit(resultTexture, target);
            return;
        }

        //Store the previous frame
        RenderTexture prevFrame = RenderTexture.GetTemporary(source.width, source.height, 0, ShaderHelper.RGBA_SFloat);
        Graphics.Blit(resultTexture, prevFrame); //the previous frame is stored in resultTexuture

        //Render the current frame without averaging
        rayTracingMaterial.SetInt("numRenderedFrames", numRenderedFrames);
        RenderTexture currentFrame = RenderTexture.GetTemporary(source.width, source.height, 0, ShaderHelper.RGBA_SFloat);

        Graphics.Blit(null, currentFrame, rayTracingMaterial);

        //Accumulate step
        accumulatorMaterial.SetInt("numRenderedFrames", numRenderedFrames);
        accumulatorMaterial.SetTexture("_MainTexOld", prevFrame);
        Graphics.Blit(currentFrame, resultTexture, accumulatorMaterial);
        Graphics.Blit(resultTexture, target);
        if (Camera.current.name == "SceneCamera" && !accumlateInSceneView) {
            Graphics.Blit(currentFrame, target);
        }

        if (Camera.current.name != "SceneCamera" && !accumulateInGameView) {
            Graphics.Blit(currentFrame, target);
        }
        if (Camera.current.name != "SceneCamera") {
            numRenderedFrames++;
            if (numRenderedFrames % 100 == 0) {
                Debug.Log("Num Rendered Frames: " + numRenderedFrames);
            }
            if (numRenderedFrames > numFrames) {
                //stopRendering = true;
            }
        }

        RenderTexture.ReleaseTemporary(prevFrame);
        RenderTexture.ReleaseTemporary(currentFrame);

       }

       else {
        Graphics.Blit(source, target);
       }
    }

    void InitFrame() {
        Camera.current.cullingMask = 0;
        ShaderHelper.InitMaterial(accumulatorShader, ref accumulatorMaterial);
        ShaderHelper.InitMaterial(rayTracingShader, ref rayTracingMaterial);
        ShaderHelper.CreateRenderTexture(ref resultTexture, Screen.width, Screen.height, FilterMode.Bilinear, ShaderHelper.RGBA_SFloat, "Result");
        var cam = Camera.current;
        UpdateCameraParams(cam);
        UpdateShaderValues();
        allocateSphereBuffer();
        allocateBlackHoleBuffer();
        allocateMeshBuffer();
    }

    void UpdateCameraParams(Camera camera) {
        float planeHeight = camera.nearClipPlane * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) * 2f;
        float planeWidth = planeHeight * camera.aspect;
        rayTracingMaterial.SetVector("ViewParams", new Vector3(planeWidth, planeHeight, camera.nearClipPlane));
        rayTracingMaterial.SetMatrix("CameraLocalToWorldMatrix", camera.transform.localToWorldMatrix);
        rayTracingMaterial.SetFloat("CameraNear", camera.nearClipPlane);
        rayTracingMaterial.SetFloat("CameraFar", renderDistance);
        rayTracingMaterial.SetVector("CameraWorldPos", camera.transform.position);
        rayTracingMaterial.SetInt("RaysPerPixel", raysPerPixel);
        rayTracingMaterial.SetInt("maxBounces", maxBounces);
        if (blackHoleSOIStepSize == 0) {
            blackHoleSOIStepSize = 1.0f;
        }
        rayTracingMaterial.SetFloat("stepSize", blackHoleSOIStepSize);
        rayTracingMaterial.SetInt("emergencyBreakMaxSteps", emergencyBreakMaxSteps);
        accumulatorMaterial.SetFloat("accumWeight", accumWeight);

        if (renderSphere) {
            rayTracingMaterial.EnableKeyword("TEST_SPHERE");
        }
        else {
            rayTracingMaterial.DisableKeyword("TEST_SPHERE");
        }
        if (renderTriangles) {
            rayTracingMaterial.EnableKeyword("TEST_TRIANGLE");
        }
        else {
            rayTracingMaterial.DisableKeyword("TEST_TRIANGLE");
        }
    }

    void allocateMeshBuffer() {
        RayTracedMesh[] meshObjects = FindObjectsOfType<RayTracedMesh>();
        MeshStruct[] meshes = new MeshStruct[meshObjects.Length];
        int indexOffset = 0;
        int vertexAndNormalOffset = 0;
        List<float3> vertices = new List<float3>();
        List<float3> normals = new List<float3>();
        List<int> indices = new List<int>();
        
        int totalTris = 0;
        int currentBVHNodeIndex = 0;
        int totalBVHNodes = getTotalBVHNodes(meshObjects);
        GPUBVHNode[] BVHNodes = new GPUBVHNode[totalBVHNodes];
        bool anyMeshMarkedUpdated = false;
        for (int i = 0; i < meshObjects.Length; i++) {
            var m = meshObjects[i];
            int triCount = (m.triangles != null) ? (m.triangles.Count / 3) : 0;
            totalTris += triCount;
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
            Debug.Log("Updating mesh buffers");
            Triangle[] triangles = new Triangle[totalTris];
            for (int i = 0; i < meshObjects.Length; i++) {
                for (int j = 0; j < meshObjects[i].vertices.Count; j++) {
                    vertices.Add(meshObjects[i].vertices[j]);
                    normals.Add(meshObjects[i].normals[j]);
                }
                meshes[i] = new MeshStruct() {
                    indexOffset = indexOffset,
                    triangleCount = meshObjects[i].triangles.Count/3,
                    material = meshObjects[i].material,
                    AABBLeftX = meshObjects[i].ModelBounds.min.x,
                    AABBLeftY = meshObjects[i].ModelBounds.min.y,
                    AABBLeftZ = meshObjects[i].ModelBounds.min.z,
                    AABBRightX = meshObjects[i].ModelBounds.max.x,
                    AABBRightY = meshObjects[i].ModelBounds.max.y,
                    AABBRightZ = meshObjects[i].ModelBounds.max.z,
                    firstBVHNodeIndex = currentBVHNodeIndex,
                    largestAxis = meshObjects[i].largestAxis,
                };
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

                    node.firstTriangleIndex += indexOffset;
                    BVHNodes[currentBVHNodeIndex++] = node;
                }
                for (int j = 0; j < meshObjects[i].buildTriangles.Count; j++) {
                    int baseIndex = meshObjects[i].buildTriangles[j].triangleIndex;
                    int vertexIndex1 = meshObjects[i].triangles[baseIndex];
                    int vertexIndex2 = meshObjects[i].triangles[baseIndex+1];
                    int vertexIndex3 = meshObjects[i].triangles[baseIndex+2];
                    Vector3 edgeAB = meshObjects[i].vertices[vertexIndex2] - meshObjects[i].vertices[vertexIndex1];
                    Vector3 edgeAC = meshObjects[i].vertices[vertexIndex3] - meshObjects[i].vertices[vertexIndex1];
                    triangles[indexOffset] = new Triangle() {
                        /*vertexIndex1 = vertexIndex1 + vertexAndNormalOffset,
                        vertexIndex2 = vertexIndex2 + vertexAndNormalOffset,
                        vertexIndex3 = vertexIndex3 + vertexAndNormalOffset,*/
                        vertex1 = meshObjects[i].vertices[vertexIndex1],
                        normalIndex1 = vertexIndex1 + vertexAndNormalOffset,
                        normalIndex2 = vertexIndex2 + vertexAndNormalOffset,
                        normalIndex3 = vertexIndex3 + vertexAndNormalOffset,
                        edgeAB = edgeAB,
                        edgeAC = edgeAC,
                    };
                    indexOffset++;
                }
                vertexAndNormalOffset += meshObjects[i].vertices.Count;
            }
            ShaderHelper.CreateStructuredBuffer(ref MeshVerticesBuffer, vertices);

            ShaderHelper.CreateStructuredBuffer(ref MeshNormalsBuffer, normals);

            ShaderHelper.CreateStructuredBuffer(ref meshBuffer, meshes);

            ShaderHelper.CreateStructuredBuffer(ref TriangleBuffer, triangles);

            ShaderHelper.CreateStructuredBuffer(ref BVHBuffer, BVHNodes);

        }
        rayTracingMaterial.SetBuffer("Meshes", meshBuffer);
        rayTracingMaterial.SetInt("numMeshes", meshes.Length);
        rayTracingMaterial.SetBuffer("Triangles", TriangleBuffer);
        rayTracingMaterial.SetBuffer("BVHNodes", BVHBuffer);
        rayTracingMaterial.SetBuffer("Vertices", MeshVerticesBuffer);
        rayTracingMaterial.SetBuffer("Normals", MeshNormalsBuffer);
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
