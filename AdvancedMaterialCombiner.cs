using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using UnityEngine.Rendering;
using System.IO;

public class AdvancedMaterialCombiner : EditorWindow
{
    public enum MeshIndexLimit { Bit16, Bit32 }
    public enum TargetRendererType { MeshRenderer, SkinnedMeshRenderer }
    public enum MeshSaveMode { SceneInstanceOnly, SaveAsAsset }

    // Kullanıcı arayüzü değişkenleri
    public List<GameObject> targetObjects = new List<GameObject>();
    public TargetRendererType rendererType = TargetRendererType.MeshRenderer;
    public MeshIndexLimit indexLimit = MeshIndexLimit.Bit16; // Eski mobil cihazlar için 16-bit en güvenlisidir
    public int[] atlasSizeOptions = { 512, 1024, 2048, 4096, 8192 };
    public int selectedAtlasSizeIndex = 2; // Varsayılan 2048
    public MeshSaveMode saveMode = MeshSaveMode.SceneInstanceOnly;

    private SerializedObject so;
    private SerializedProperty targetObjectsProp;
    private Vector2 scrollPos;

    [MenuItem("Tools/Advanced Material Combiner")]
    public static void ShowWindow()
    {
        GetWindow<AdvancedMaterialCombiner>("Material Combiner");
    }

    private void OnEnable()
    {
        so = new SerializedObject(this);
        targetObjectsProp = so.FindProperty("targetObjects");
    }

    // Sürüm uyumluluğu için özel boşluk bırakma fonksiyonu
    private void DrawSpace(float pixels)
    {
#if UNITY_2019_1_OR_NEWER
        EditorGUILayout.Space(pixels);
#else
        GUILayout.Space(pixels);
#endif
    }

    void OnGUI()
    {
        so.Update();

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);

        DrawSpace(5f);
        EditorGUILayout.HelpBox("IMPORTANT INFORMATION:\n\n" +
            "• The textures to be merged must have the 'Read/Write Enabled' option enabled and be uncompressed.\n" +
            "• Remember to adjust the 'Max Size' of the textures to suit your needs (without setting it too high).\n" +
            "• You can export the resulting combined Mesh using the FBX Exporter plugin and use it as a raw model.", MessageType.Info);
        
        DrawSpace(10f);
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);

        rendererType = (TargetRendererType)EditorGUILayout.EnumPopup("Mesh Type", rendererType);
        indexLimit = (MeshIndexLimit)EditorGUILayout.EnumPopup("Mesh Index Format", indexLimit);
        selectedAtlasSizeIndex = EditorGUILayout.Popup("Atlas Max Size", selectedAtlasSizeIndex, 
            new string[] { "512", "1024", "2048", "4096", "8192" });
        saveMode = (MeshSaveMode)EditorGUILayout.EnumPopup("Mesh Save Location", saveMode);

        DrawSpace(10f);
        EditorGUILayout.LabelField("Target Objects", EditorStyles.boldLabel);

        EditorGUILayout.PropertyField(targetObjectsProp, true);

        so.ApplyModifiedProperties();

        DrawSpace(15f);
        if (GUILayout.Button("Combine Materials", GUILayout.Height(30)))
        {
            CombineListedObjects();
        }

        EditorGUILayout.EndScrollView();
    }

    void CombineListedObjects()
    {
        if (targetObjects.Count == 0)
        {
            Debug.LogWarning("There are no objects in the list!");
            return;
        }

        List<Renderer> renderers = new List<Renderer>();
        List<Texture2D> textures = new List<Texture2D>();

        foreach (var obj in targetObjects)
        {
            if (obj == null) continue;

            Renderer r = (rendererType == TargetRendererType.MeshRenderer) 
                ? (Renderer)obj.GetComponent<MeshRenderer>() 
                : (Renderer)obj.GetComponent<SkinnedMeshRenderer>();

            if (r != null && r.sharedMaterial != null)
            {
                renderers.Add(r);
                Texture2D tex = r.sharedMaterial.mainTexture as Texture2D;
                if (tex != null && !textures.Contains(tex))
                {
                    textures.Add(tex);
                }
            }
        }

        if (textures.Count == 0)
        {
            Debug.LogError("No valid texture was found for the objects. Check your Read/Write settings.");
            return;
        }

        int atlasSize = atlasSizeOptions[selectedAtlasSizeIndex];
        Texture2D atlas = new Texture2D(atlasSize, atlasSize);
        Rect[] rects = atlas.PackTextures(textures.ToArray(), 2, atlasSize);
        
        string atlasPath = "Assets/CombinedAtlas.png";
        byte[] bytes = atlas.EncodeToPNG();
        File.WriteAllBytes(Application.dataPath + "/CombinedAtlas.png", bytes);
        AssetDatabase.Refresh();

        Material combinedMaterial = new Material(Shader.Find("Mobile/Diffuse"));
        combinedMaterial.mainTexture = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);

        if (saveMode == MeshSaveMode.SaveAsAsset && !AssetDatabase.IsValidFolder("Assets/CombinedMeshes"))
        {
            AssetDatabase.CreateFolder("Assets", "CombinedMeshes");
        }

        foreach (var r in renderers)
        {
            Mesh originalMesh = null;
            
            if (rendererType == TargetRendererType.MeshRenderer)
            {
                MeshFilter mf = r.GetComponent<MeshFilter>();
                if (mf != null) originalMesh = mf.sharedMesh;
            }
            else
            {
                SkinnedMeshRenderer smr = (SkinnedMeshRenderer)r;
                if (smr != null) originalMesh = smr.sharedMesh;
            }

            if (originalMesh == null) continue;

            Mesh newMesh = Instantiate(originalMesh);
            newMesh.name = originalMesh.name + "_Combined";
            
            newMesh.indexFormat = (indexLimit == MeshIndexLimit.Bit16) ? IndexFormat.UInt16 : IndexFormat.UInt32;

            Vector2[] uvs = newMesh.uv;
            Texture2D currentTex = r.sharedMaterial.mainTexture as Texture2D;
            int texIndex = textures.IndexOf(currentTex);

            if (texIndex != -1)
            {
                Rect uvRect = rects[texIndex];

                for (int i = 0; i < uvs.Length; i++)
                {
                    uvs[i].x = Mathf.Lerp(uvRect.xMin, uvRect.xMax, uvs[i].x);
                    uvs[i].y = Mathf.Lerp(uvRect.yMin, uvRect.yMax, uvs[i].y);
                }

                newMesh.uv = uvs;

                if (saveMode == MeshSaveMode.SaveAsAsset)
                {
                    string meshPath = AssetDatabase.GenerateUniqueAssetPath("Assets/CombinedMeshes/" + newMesh.name + ".asset");
                    AssetDatabase.CreateAsset(newMesh, meshPath);
                }

                if (rendererType == TargetRendererType.MeshRenderer)
                {
                    r.GetComponent<MeshFilter>().sharedMesh = newMesh;
                }
                else
                {
                    ((SkinnedMeshRenderer)r).sharedMesh = newMesh;
                }

                r.sharedMaterial = combinedMaterial;
            }
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"The operation was completed successfully! Atlas has been saved as '{atlasPath}'.");
    }
}