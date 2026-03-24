using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Formats.Fbx.Exporter;
using UnityEngine;
using UnityEditor.CSV2UnityMesh;

namespace RenderDocImporting.Editor
{
    [Serializable]
    public class RenderDocMeshEntry
    {
        public string mesh_stage;
        public string topology;
        public int row_count;
        public int attribute_count;
        public string output_path;
        public string unity_triangle_soup_output_path;
    }

    [Serializable]
    public class RenderDocTextureEntry
    {
        public string stage;
        public int slot;
        public string name;
        public string resource_id;
        public string resource_name;
        public int width;
        public int height;
        public string format;
        public string output_path;
    }

    [Serializable]
    public class RenderDocManifest
    {
        public int event_id;
        public string action_name;
        public int instance;
        public int view;
        public RenderDocUnityImportHints unity_import_hints;
        public RenderDocMeshEntry mesh;
        public List<RenderDocTextureEntry> textures;
    }

    [Serializable]
    public class RenderDocUnityImportHints
    {
        public bool flip_uv = true;
        public float[] rotation_euler;
        public string preferred_mesh_output_path;
        public string fallback_mesh_output_path;
    }

    public static class RenderDocBundleImporter
    {
        [MenuItem("Tools/RenderDoc/Import Bundles")]
        public static void ImportAllBundles()
        {
            string[] manifestGuids = AssetDatabase.FindAssets("manifest", new[] { "Assets/RenderDocImports" });
            int importedCount = 0;

            foreach (string guid in manifestGuids)
            {
                string manifestPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!manifestPath.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (ImportBundle(manifestPath))
                {
                    importedCount++;
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"RenderDoc bundle import complete. Imported {importedCount} bundle(s).");
        }

        [MenuItem("Tools/RenderDoc/Import Selected Bundle")]
        public static bool ImportSelectedBundle()
        {
            if (Selection.objects.Length == 0)
                return false;

            string[] guids = Selection.assetGUIDs;
            if (guids == null || guids.Length == 0)
                return false;

            string manifestPath = AssetDatabase.GUIDToAssetPath(guids[0]);
            if (!string.IsNullOrEmpty(manifestPath) && manifestPath.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                if (ImportBundle(manifestPath))
                {
                    AssetDatabase.SaveAssets();
                    AssetDatabase.Refresh();
                    Debug.Log($"RenderDoc selected bundle import complete, manifestPath:{manifestPath}");
                    return true;
                }
            }

            return false;
        }

        private static bool ImportBundle(string manifestPath)
        {
            return ImportBundle(manifestPath, null);
        }

        private static bool ImportBundle(string manifestPath, string outputSubfolder)
        {
            TextAsset manifestAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(manifestPath);
            if (manifestAsset == null)
            {
                Debug.LogError($"RenderDoc importer: failed to load manifest at {manifestPath}");
                return false;
            }

            RenderDocManifest manifest = JsonUtility.FromJson<RenderDocManifest>(manifestAsset.text);
            if (manifest == null)
            {
                Debug.LogError($"RenderDoc importer: failed to parse manifest at {manifestPath}");
                return false;
            }

            string bundleDir = Path.GetDirectoryName(manifestPath)?.Replace("\\", "/");
            if (string.IsNullOrEmpty(bundleDir))
            {
                Debug.LogError($"RenderDoc importer: invalid bundle directory for {manifestPath}");
                return false;
            }

            string generatedDir = EnsureFolder(bundleDir, "Generated");
            if (!string.IsNullOrEmpty(outputSubfolder))
            {
                generatedDir = EnsureFolder(generatedDir, outputSubfolder);
            }

            string generatedMeshPath = null;
            string generatedFbxPath = null;
            if (manifest.mesh != null && !string.IsNullOrEmpty(manifest.mesh.output_path))
            {
                string preferredMeshPath =
                    !string.IsNullOrEmpty(manifest.unity_import_hints?.preferred_mesh_output_path)
                        ? manifest.unity_import_hints.preferred_mesh_output_path
                        : manifest.mesh.output_path;

                if (string.IsNullOrEmpty(preferredMeshPath) &&
                    !string.IsNullOrEmpty(manifest.unity_import_hints?.fallback_mesh_output_path))
                {
                    preferredMeshPath = manifest.unity_import_hints.fallback_mesh_output_path;
                }

                if (string.IsNullOrEmpty(preferredMeshPath) &&
                    !string.IsNullOrEmpty(manifest.mesh.unity_triangle_soup_output_path))
                {
                    preferredMeshPath = manifest.mesh.unity_triangle_soup_output_path;
                }

                string sourceCsvPath = FindAssetByFileName($"{bundleDir}/mesh", Path.GetFileName(preferredMeshPath));
                if (string.IsNullOrEmpty(sourceCsvPath))
                {
                    Debug.LogError($"RenderDoc importer: mesh CSV not found for {manifestPath}");
                    return false;
                }

                string compatibleCsvPath = CreateCompatibleCsv(sourceCsvPath, generatedDir);
                if (string.IsNullOrEmpty(compatibleCsvPath))
                {
                    return false;
                }

                if (!GenerateMeshAndFbx(
                    compatibleCsvPath,
                    generatedDir,
                    manifest.unity_import_hints != null ? manifest.unity_import_hints.flip_uv : true,
                    out generatedMeshPath,
                    out generatedFbxPath))
                {
                    return false;
                }
            }

            string materialPath = CreateMaterial(manifest, bundleDir, generatedDir);
            CreatePrefab(generatedMeshPath, materialPath, generatedDir, manifest);
            return true;
        }

        private static string EnsureFolder(string parentAssetPath, string childFolderName)
        {
            string normalizedParent = parentAssetPath.Replace("\\", "/");
            string targetPath = $"{normalizedParent}/{childFolderName}";
            if (AssetDatabase.IsValidFolder(targetPath))
            {
                return targetPath;
            }

            AssetDatabase.CreateFolder(normalizedParent, childFolderName);
            return targetPath;
        }

        private static string FindAssetByFileName(string assetFolder, string fileName)
        {
            if (string.IsNullOrEmpty(assetFolder) || string.IsNullOrEmpty(fileName))
            {
                return null;
            }

            string normalizedFolder = assetFolder.Replace("\\", "/");
            string normalizedName = fileName.Replace("\\", "/");

            foreach (string guid in AssetDatabase.FindAssets(string.Empty, new[] { normalizedFolder }))
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid).Replace("\\", "/");
                if (assetPath.EndsWith("/" + normalizedName, StringComparison.OrdinalIgnoreCase))
                {
                    return assetPath;
                }
            }

            return null;
        }

        private static string CreateCompatibleCsv(string sourceCsvPath, string generatedDir)
        {
            string absoluteSourcePath = Path.GetFullPath(sourceCsvPath);
            if (!File.Exists(absoluteSourcePath))
            {
                Debug.LogError($"RenderDoc importer: source CSV not found at {absoluteSourcePath}");
                return null;
            }

            string[] lines = File.ReadAllLines(absoluteSourcePath);
            if (lines.Length == 0)
            {
                Debug.LogError($"RenderDoc importer: empty CSV at {sourceCsvPath}");
                return null;
            }

            string[] sourceHeaders = lines[0].Split(',');
            List<int> keptColumnIndices = new List<int>();
            List<string> headers = new List<string>();

            for (int i = 0; i < sourceHeaders.Length; i++)
            {
                string header = sourceHeaders[i].Trim();
                if (header.Equals("instance", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                keptColumnIndices.Add(i);
                if (header.Equals("row", StringComparison.OrdinalIgnoreCase))
                {
                    headers.Add("VTX");
                    continue;
                }

                if (header.Equals("vertex_index", StringComparison.OrdinalIgnoreCase))
                {
                    headers.Add("IDX");
                    continue;
                }

                if (header.EndsWith("_x", StringComparison.OrdinalIgnoreCase) ||
                    header.EndsWith("_y", StringComparison.OrdinalIgnoreCase) ||
                    header.EndsWith("_z", StringComparison.OrdinalIgnoreCase) ||
                    header.EndsWith("_w", StringComparison.OrdinalIgnoreCase))
                {
                    int splitIndex = header.LastIndexOf('_');
                    headers.Add(header.Substring(0, splitIndex) + "." + char.ToLowerInvariant(header[splitIndex + 1]));
                    continue;
                }

                headers.Add(header);
            }

            List<string> outputLines = new List<string>(lines.Length)
            {
                string.Join(", ", headers)
            };

            for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
            {
                if (string.IsNullOrWhiteSpace(lines[lineIndex]))
                {
                    continue;
                }

                string[] sourceCells = lines[lineIndex].Split(',');
                List<string> outputCells = new List<string>(keptColumnIndices.Count);
                foreach (int columnIndex in keptColumnIndices)
                {
                    if (columnIndex < sourceCells.Length)
                    {
                        outputCells.Add(sourceCells[columnIndex].Trim());
                    }
                    else
                    {
                        outputCells.Add(string.Empty);
                    }
                }

                outputLines.Add(string.Join(", ", outputCells));
            }

            string outputFileName = Path.GetFileNameWithoutExtension(sourceCsvPath) + "_unity.csv";
            string outputAssetPath = $"{generatedDir}/{outputFileName}";
            string absoluteOutputPath = Path.GetFullPath(outputAssetPath);
            File.WriteAllLines(absoluteOutputPath, outputLines);
            AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceUpdate);
            return outputAssetPath;
        }

        private static bool GenerateMeshAndFbx(
            string csvAssetPath,
            string generatedDir,
            bool flipUV,
            out string meshAssetPath,
            out string fbxAssetPath)
        {
            meshAssetPath = null;
            fbxAssetPath = null;

            TextAsset csvAsset = AssetDatabase.LoadAssetAtPath<TextAsset>(csvAssetPath);
            if (csvAsset == null)
            {
                Debug.LogError($"RenderDoc importer: failed to load CSV asset {csvAssetPath}");
                return false;
            }

            string[] columnHeads = UnityEditor.CSV2UnityMesh.CSV2UnityMesh.ReadCSVColumnHeads(csvAsset)?.ToArray();
            if (columnHeads == null || columnHeads.Length == 0)
            {
                Debug.LogError($"RenderDoc importer: failed to read CSV headers from {csvAssetPath}");
                return false;
            }

            UnityEditor.CSV2UnityMesh.CSV2UnityMesh.positionColumnID = FindFirstIndex(columnHeads, "POSITION");
            UnityEditor.CSV2UnityMesh.CSV2UnityMesh.normalColumnID = FindFirstIndex(columnHeads, "NORMAL");
            UnityEditor.CSV2UnityMesh.CSV2UnityMesh.tangentColumnID = FindFirstIndex(columnHeads, "TANGENT");
            UnityEditor.CSV2UnityMesh.CSV2UnityMesh.colorColumnID = FindFirstIndex(columnHeads, "COLOR");
            UnityEditor.CSV2UnityMesh.CSV2UnityMesh.modelScale = 1.0f;
            UnityEditor.CSV2UnityMesh.CSV2UnityMesh.flipNormals = false;
            UnityEditor.CSV2UnityMesh.CSV2UnityMesh.flipUV = flipUV;
            UnityEditor.CSV2UnityMesh.CSV2UnityMesh.propertyOps = new[] {
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty
            };

            UnityEditor.CSV2UnityMesh.CSV2UnityMesh.texcoordColumnID = new[] {
                FindFirstIndex(columnHeads, "TEXCOORD0"),
                FindFirstIndex(columnHeads, "TEXCOORD1"),
                FindFirstIndex(columnHeads, "TEXCOORD2"),
                FindFirstIndex(columnHeads, "TEXCOORD3"),
                FindFirstIndex(columnHeads, "TEXCOORD4"),
            };
            UnityEditor.CSV2UnityMesh.CSV2UnityMesh.enableTexcoord = UnityEditor.CSV2UnityMesh.CSV2UnityMesh.texcoordColumnID.Select(index => index >= 0).ToArray();
            UnityEditor.CSV2UnityMesh.CSV2UnityMesh.fbxExportFormat =
                UnityEditor.CSV2UnityMesh.CSV2UnityMesh.ExportFormat.ASCII;

            if (UnityEditor.CSV2UnityMesh.CSV2UnityMesh.positionColumnID < 0)
            {
                Debug.LogError($"RenderDoc importer: POSITION columns not found in {csvAssetPath}");
                return false;
            }

            Mesh mesh = UnityEditor.CSV2UnityMesh.CSV2UnityMesh.CreateMeshFromCSVAsset(
                columnHeads,
                csvAsset,
                UnityEditor.CSV2UnityMesh.CSV2UnityMesh.propertyOps,
                flipUV: UnityEditor.CSV2UnityMesh.CSV2UnityMesh.flipUV,
                flipNormals: UnityEditor.CSV2UnityMesh.CSV2UnityMesh.flipNormals
            );
            if (mesh == null)
            {
                Debug.LogError($"RenderDoc importer: failed to generate mesh from {csvAssetPath}");
                return false;
            }

            string baseName = Path.GetFileNameWithoutExtension(csvAssetPath);
            fbxAssetPath = $"{generatedDir}/{baseName}.fbx";
            meshAssetPath = $"{generatedDir}/{baseName}_fullData.mesh";

            GameObject tempObject = new GameObject(baseName + "_Mesh");
            try
            {
                MeshFilter meshFilter = tempObject.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = mesh;
                tempObject.AddComponent<MeshRenderer>();

                ExportModelOptions exportOptions = new ExportModelOptions
                {
                    ExportFormat = ExportFormat.ASCII
                };
                ModelExporter.ExportObject(fbxAssetPath, tempObject, exportOptions);

                Mesh meshAsset = UnityEngine.Object.Instantiate(mesh);
                AssetDatabase.CreateAsset(meshAsset, meshAssetPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(tempObject);
                UnityEngine.Object.DestroyImmediate(mesh);
            }

            AssetDatabase.ImportAsset(fbxAssetPath, ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(meshAssetPath, ImportAssetOptions.ForceUpdate);
            return true;
        }

        private static int FindFirstIndex(string[] values, string target)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i].Equals(target, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string CreateMaterial(RenderDocManifest manifest, string bundleDir, string generatedDir)
        {
            Shader shader = Shader.Find("Standard");
            if (shader == null)
            {
                Debug.LogWarning("RenderDoc importer: Standard shader not found, skipping material creation.");
                return null;
            }

            Material material = new Material(shader);
            material.name = SanitizeFileName(manifest.action_name) + "_Material";

            if (manifest.textures != null)
            {
                foreach (RenderDocTextureEntry textureEntry in manifest.textures)
                {
                    string textureAssetPath = FindAssetByFileName($"{bundleDir}/textures", Path.GetFileName(textureEntry.output_path));
                    Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(textureAssetPath);
                    if (texture == null)
                    {
                        continue;
                    }

                    string resourceName = textureEntry.resource_name ?? string.Empty;
                    string slotName = textureEntry.name ?? string.Empty;

                    if (slotName.Equals("texture2", StringComparison.OrdinalIgnoreCase) || resourceName.IndexOf("Diffuse", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        material.SetTexture("_MainTex", texture);
                    }
                    else if (slotName.Equals("texture1", StringComparison.OrdinalIgnoreCase) || resourceName.IndexOf("Normal", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        TextureImporter importer = AssetImporter.GetAtPath(textureAssetPath) as TextureImporter;
                        if (importer != null && importer.textureType != TextureImporterType.NormalMap)
                        {
                            importer.textureType = TextureImporterType.NormalMap;
                            importer.SaveAndReimport();
                            texture = AssetDatabase.LoadAssetAtPath<Texture2D>(textureAssetPath);
                        }
                        material.SetTexture("_BumpMap", texture);
                        material.EnableKeyword("_NORMALMAP");
                    }
                    else if (slotName.Equals("texture3", StringComparison.OrdinalIgnoreCase) || resourceName.IndexOf("Metal", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        material.SetTexture("_MetallicGlossMap", texture);
                        material.EnableKeyword("_METALLICGLOSSMAP");
                    }
                }
            }

            string materialPath = $"{generatedDir}/{SanitizeFileName(manifest.action_name)}.mat";
            AssetDatabase.CreateAsset(material, materialPath);
            AssetDatabase.ImportAsset(materialPath, ImportAssetOptions.ForceUpdate);
            return materialPath;
        }

        private static void CreatePrefab(string meshAssetPath, string materialPath, string generatedDir, RenderDocManifest manifest)
        {
            if (string.IsNullOrEmpty(meshAssetPath))
            {
                return;
            }

            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath);
            if (mesh == null)
            {
                return;
            }

            Material material = string.IsNullOrEmpty(materialPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<Material>(materialPath);

            string prefabName = SanitizeFileName(manifest.action_name) + "_Preview.prefab";
            string prefabPath = $"{generatedDir}/{prefabName}";

            GameObject previewObject = new GameObject(prefabName.Replace(".prefab", ""));
            try
            {
                MeshFilter meshFilter = previewObject.AddComponent<MeshFilter>();
                meshFilter.sharedMesh = mesh;

                MeshRenderer meshRenderer = previewObject.AddComponent<MeshRenderer>();
                if (material != null)
                {
                    meshRenderer.sharedMaterial = material;
                }

                Vector3 importRotation = new Vector3(0f, 90f, -90f);
                if (manifest.unity_import_hints?.rotation_euler != null &&
                    manifest.unity_import_hints.rotation_euler.Length >= 3)
                {
                    importRotation = new Vector3(
                        manifest.unity_import_hints.rotation_euler[0],
                        manifest.unity_import_hints.rotation_euler[1],
                        manifest.unity_import_hints.rotation_euler[2]
                    );
                }
                previewObject.transform.rotation = Quaternion.Euler(importRotation);

                PrefabUtility.SaveAsPrefabAsset(previewObject, prefabPath);
                AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(previewObject);
            }
        }

        private static string SanitizeFileName(string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return "RenderDocAsset";
            }

            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                input = input.Replace(invalidChar, '_');
            }

            return input.Replace('<', '_').Replace('>', '_').Trim();
        }
    }
}
