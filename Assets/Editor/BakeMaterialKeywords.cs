using UnityEditor;
using UnityEngine;
using System.Text;

public class BakeMaterialKeywords
{
    [MenuItem("Tools/Bake RayTracer Keyword Variants")]
    static void Bake()
    {
        Shader shader = Shader.Find("Custom/RayTracer");
    
        string path = "Assets/Resources/RayTracerVariants.shadervariants";
        ShaderVariantCollection collection = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(path);
        if (collection == null)
        {
            collection = new ShaderVariantCollection();
            AssetDatabase.CreateAsset(collection, path);
        }

        collection.Clear();

        // Add the variant with all your keywords
        ShaderVariantCollection.ShaderVariant variant = new ShaderVariantCollection.ShaderVariant(
            shader,
            UnityEngine.Rendering.PassType.Normal,
            "TEST_SPHERE", "TEST_TRIANGLE", "ENABLE_LENSING",
            "USE_TLAS", "USE_REDSHIFTING", "APPLY_RAYLEIGH",
            "APPLY_MIE", "APPLY_SUNDISK", "APPLY_SCATTERING",
            "APPLY_SUN_LIGHTING", "APPLY_NEE", "USE_RAY_MAGNIFICATION",
            "MARCH_CHORD_COLLISION_LIMIT"
        );

        collection.Add(variant);

        EditorUtility.SetDirty(collection);
        AssetDatabase.SaveAssets();

        Debug.Log("Variant collection saved with " + collection.variantCount + " variants");
    }
    [MenuItem("Tools/Add Variants To Preload List")]
    static void AddToPreloadList()
    {
        string path = "Assets/Resources/RayTracerVariants.shadervariants";
        ShaderVariantCollection collection = AssetDatabase.LoadAssetAtPath<ShaderVariantCollection>(path);
    
        if (collection == null)
        {
            Debug.LogError("RayTracerVariants.shadervariants not found — run Bake first");
            return;
        }

        SerializedObject graphicsSettings = new SerializedObject(
            AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(
                "ProjectSettings/GraphicsSettings.asset"));

        SerializedProperty preloadedShaders = graphicsSettings.FindProperty("m_PreloadedShaders");

        // Check if already in list
        for (int i = 0; i < preloadedShaders.arraySize; i++)
        {
            if (preloadedShaders.GetArrayElementAtIndex(i).objectReferenceValue == collection)
            {
                Debug.Log("Already in preload list");
                return;
            }
        }

        // Add it
        preloadedShaders.arraySize++;
        preloadedShaders.GetArrayElementAtIndex(preloadedShaders.arraySize - 1)
            .objectReferenceValue = collection;

        graphicsSettings.ApplyModifiedProperties();
        AssetDatabase.SaveAssets();

        Debug.Log("Added RayTracerVariants to preloaded shaders");
    }
}