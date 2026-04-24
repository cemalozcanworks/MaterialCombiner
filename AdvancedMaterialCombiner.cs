using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using UnityEngine.Rendering;
using System.IO;
using System.Linq;

public class AdvancedMaterialCombiner : EditorWindow
{
    public enum MeshIndexLimit { Bit16, Bit32 }
    public enum TargetRendererType { MeshRenderer, SkinnedMeshRenderer }
    public enum MeshSaveMode { SceneInstanceOnly, SaveAsAsset }

    // Kullanıcı arayüzü değişkenleri
    public List<GameObject> targetObjects = new List<GameObject>();
    public TargetRendererType rendererType = TargetRendererType.MeshRenderer;
    public MeshIndexLimit indexLimit = MeshIndexLimit.Bit16; 
    public int[] atlasSizeOptions = { 512, 1024, 2048, 4096, 8192 };
    public int selectedAtlasSizeIndex = 2; 
    public MeshSaveMode saveMode = MeshSaveMode.SceneInstanceOnly;
    
    // YENİ: Mesh birleştirme opsiyonu
    public bool mergeMeshes = false;

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
            "• Mesh merging is currently only supported by MeshRenderer. You can export the resulting combined Mesh using the FBX Exporter plugin and use it as a raw model.", MessageType.Info);
        
        DrawSpace(10f);
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);

        rendererType = (TargetRendererType)EditorGUILayout.EnumPopup("Mesh Type", rendererType);
        indexLimit = (MeshIndexLimit)EditorGUILayout.EnumPopup("Mesh Index Format", indexLimit);
        selectedAtlasSizeIndex = EditorGUILayout.Popup("Atlas Max Size", selectedAtlasSizeIndex, 
            new string[] { "512", "1024", "2048", "4096", "8192" });
        saveMode = (MeshSaveMode)EditorGUILayout.EnumPopup("Mesh Save Location", saveMode);

        // SkinnedMeshRenderer seçiliyse Merge özelliğini devre dışı bırakıp uyaralım
        GUI.enabled = rendererType == TargetRendererType.MeshRenderer;
        mergeMeshes = EditorGUILayout.Toggle("Mesh Merge", mergeMeshes);
        GUI.enabled = true;

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

        // 1. AŞAMA: Tüm objelerdeki TÜM materyalleri ve texture'ları topla
        foreach (var obj in targetObjects)
        {
            if (obj == null) continue;

            Renderer r = (rendererType == TargetRendererType.MeshRenderer) 
                ? (Renderer)obj.GetComponent<MeshRenderer>() 
                : (Renderer)obj.GetComponent<SkinnedMeshRenderer>();

            if (r != null && r.sharedMaterials.Length > 0)
            {
                renderers.Add(r);
                
                // Objedeki tüm materyalleri döngüye alıyoruz
                foreach (Material mat in r.sharedMaterials)
                {
                    if (mat != null && mat.mainTexture != null)
                    {
                        Texture2D tex = mat.mainTexture as Texture2D;
                        if (!textures.Contains(tex))
                        {
                            textures.Add(tex);
                        }
                    }
                }
            }
        }

        if (textures.Count == 0)
        {
            Debug.LogError("No valid texture was found for the objects. Check your Read/Write settings.");
            return;
        }

        // 2. AŞAMA: Texture Atlas Oluştur
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

        List<CombineInstance> combineInstances = new List<CombineInstance>();

        // 3. AŞAMA: Meshleri ve UV'leri Güncelle
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
            Material[] mats = r.sharedMaterials;
            List<int> combinedTriangles = new List<int>();
            
            // Aynı vertex'i birden fazla kez ölçeklememek için takip dizisi
            bool[] vertexProcessed = new bool[newMesh.vertexCount];

            // Her bir submesh (materyal) için işlem yapıyoruz
            for (int submeshIndex = 0; submeshIndex < newMesh.subMeshCount; submeshIndex++)
            {
                if (submeshIndex >= mats.Length) break;
                
                Material currentMat = mats[submeshIndex];
                if (currentMat == null || currentMat.mainTexture == null) continue;

                Texture2D currentTex = currentMat.mainTexture as Texture2D;
                int texIndex = textures.IndexOf(currentTex);

                if (texIndex != -1)
                {
                    Rect uvRect = rects[texIndex];
                    int[] submeshTriangles = newMesh.GetTriangles(submeshIndex);
                    
                    // Bu submesh'e ait üçgenleri ana listeye ekle
                    combinedTriangles.AddRange(submeshTriangles);

                    // Bu submesh'in kullandığı vertexlerin UV'lerini güncelle
                    foreach (int vertexIndex in submeshTriangles)
                    {
                        if (!vertexProcessed[vertexIndex])
                        {
                            uvs[vertexIndex].x = Mathf.Lerp(uvRect.xMin, uvRect.xMax, uvs[vertexIndex].x);
                            uvs[vertexIndex].y = Mathf.Lerp(uvRect.yMin, uvRect.yMax, uvs[vertexIndex].y);
                            vertexProcessed[vertexIndex] = true;
                        }
                    }
                }
            }

            newMesh.uv = uvs;
            
            // Çoklu materyali tek materyale düşürdüğümüz için tüm üçgenleri tek bir submesh'te topluyoruz
            newMesh.subMeshCount = 1;
            newMesh.triangles = combinedTriangles.ToArray();

            // Eğer birleştirme (Merge) seçiliyse, mesh'i CombineInstance listesine atıyoruz
            if (mergeMeshes && rendererType == TargetRendererType.MeshRenderer)
            {
                CombineInstance ci = new CombineInstance();
                ci.mesh = newMesh;
                // Objelerin dünyadaki pozisyon, rotasyon ve ölçeklerini koruyarak birleştir
                ci.transform = r.transform.localToWorldMatrix; 
                combineInstances.Add(ci);
                
                // Eski objeyi sahnede gizle (silmek yerine güvenli yol)
                r.gameObject.SetActive(false); 
            }
            else // Birleştirme yoksa, objenin kendi üzerine yeni mesh'i yaz
            {
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

                // Tek materyale düşür
                r.sharedMaterials = new Material[] { combinedMaterial };
            }
        }

        // 4. AŞAMA: Mesh Birleştirme İşlemi (Eğer Seçiliyse)
        if (mergeMeshes && rendererType == TargetRendererType.MeshRenderer && combineInstances.Count > 0)
        {
            Mesh finalMergedMesh = new Mesh();
            finalMergedMesh.name = "Final_Merged_Mesh";
            finalMergedMesh.indexFormat = (indexLimit == MeshIndexLimit.Bit16) ? IndexFormat.UInt16 : IndexFormat.UInt32;
            
            // Tüm parçaları tek bir fiziksel mesh'te birleştir
            finalMergedMesh.CombineMeshes(combineInstances.ToArray(), true, true);

            if (saveMode == MeshSaveMode.SaveAsAsset)
            {
                string meshPath = AssetDatabase.GenerateUniqueAssetPath("Assets/CombinedMeshes/Final_Merged_Mesh.asset");
                AssetDatabase.CreateAsset(finalMergedMesh, meshPath);
            }

            // Sahnede yeni bir obje oluştur
            GameObject mergedGO = new GameObject("Combined_Environment");
            mergedGO.AddComponent<MeshFilter>().sharedMesh = finalMergedMesh;
            mergedGO.AddComponent<MeshRenderer>().sharedMaterial = combinedMaterial;
            
            // Yeni oluşturulan objeyi seçili hale getir
            Selection.activeGameObject = mergedGO;
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
        Debug.Log($"The operation was completed successfully! Atlas has been saved as: '{atlasPath}'.");
    }
}
