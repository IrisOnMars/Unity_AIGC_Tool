using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AIGC.Toolchain.Editor
{
    public sealed class AIGCWindow : EditorWindow
    {
        private const string OutputFolderAssetPath = "Assets/AIGC_Generated";
        private const string Generate3DPrefKey = "AIGC.Toolchain.Generate3DModel";
        private const string ReviewConceptPrefKey = "AIGC.Toolchain.ReviewConceptBefore3D";
        private const string ModelSeedPrefKey = "AIGC.Toolchain.ModelSeed";
        private const string AssetTypePrefKey = "AIGC.Toolchain.AssetType";

        private string rawDescription = "elf archer";
        private AigcAssetType assetType = AigcAssetType.Auto;
        private string qwenEndpoint;
        private string qwenModel;
        private string qwenApiKey;
        private bool qwenAutoStart;
        private string qwenPythonPath;
        private string qwenServerScriptPath;
        private string qwenDevice;
        private int qwenStartupTimeoutSeconds;
        private string comfyBaseUrl;
        private int generationTimeoutSeconds;
        private bool generate3D;
        private bool reviewConceptBefore3D;
        private int modelSeed;
        private string studioApiBaseUrl;
        private bool studioAutoStart;
        private string studioProjectRoot;
        private string studioPythonPath;
        private int studioStartupTimeoutSeconds;
        private int studioGenerationTimeoutSeconds;
        private string statusMessage = "Ready.";
        private string expandedPrompt;
        private bool generating;
        private bool showSettings = true;
        private Vector2 scrollPosition;
        private GeneratedAssetContext currentAsset;

        [MenuItem("AIGC Tool/Asset Generator")]
        public static void Open()
        {
            AIGCWindow window = GetWindow<AIGCWindow>("AIGC Asset Generator");
            window.minSize = new Vector2(460, 420);
            window.Show();
        }

        [MenuItem("AIGC Tool/Create Editable Preview For Selected Model")]
        private static void CreateEditablePreviewForSelectedModel()
        {
            string modelAssetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            GameObject importedModel = AssetDatabase.LoadAssetAtPath<GameObject>(modelAssetPath);
            if (importedModel == null)
            {
                throw new InvalidOperationException("Select an imported AIGC model.obj asset first.");
            }

            string folderAssetPath = Path.GetDirectoryName(modelAssetPath)?.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(folderAssetPath))
            {
                throw new InvalidOperationException("Could not resolve the selected model folder.");
            }

            string prefabAssetPath = CreateEditablePrefab(importedModel, folderAssetPath);
            RevealAsset(prefabAssetPath);
            Debug.Log("Created editable AIGC preview prefab: " + prefabAssetPath);
        }

        [MenuItem("AIGC Tool/Create Editable Preview For Selected Model", true)]
        private static bool ValidateCreateEditablePreviewForSelectedModel()
        {
            string assetPath = AssetDatabase.GetAssetPath(Selection.activeObject);
            return !string.IsNullOrWhiteSpace(assetPath) &&
                assetPath.EndsWith(".obj", StringComparison.OrdinalIgnoreCase) &&
                AssetDatabase.LoadAssetAtPath<GameObject>(assetPath) != null;
        }

        private void OnEnable()
        {
            LoadSettings();
        }

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            EditorGUILayout.LabelField("AIGC Asset Generator", EditorStyles.boldLabel);
            EditorGUILayout.Space(6);

            EditorGUILayout.LabelField("Source Description");
            rawDescription = EditorGUILayout.TextArea(rawDescription, GUILayout.MinHeight(80));

            EditorGUILayout.Space(8);

            EditorGUI.BeginChangeCheck();
            assetType = (AigcAssetType)EditorGUILayout.EnumPopup("Asset Type", assetType);
            generate3D = EditorGUILayout.Toggle("Generate 3D Model", generate3D);
            using (new EditorGUI.DisabledScope(!generate3D))
            {
                reviewConceptBefore3D = EditorGUILayout.Toggle("Review Concept First", reviewConceptBefore3D);
            }
            if (EditorGUI.EndChangeCheck())
            {
                SaveSettings();
            }

            EditorGUILayout.Space(6);

            using (new EditorGUI.DisabledScope(generating))
            {
                string buttonLabel = generate3D ? "Generate 3D Asset" : "Generate Image Asset";
                if (GUILayout.Button(buttonLabel, GUILayout.Height(32)))
                {
                    StartGeneration();
                }
            }

            EditorGUILayout.Space(8);
            DrawStatus();

            if (currentAsset != null)
            {
                DrawConceptPreview();
            }

            EditorGUILayout.Space(10);
            showSettings = EditorGUILayout.Foldout(showSettings, "Settings", true);
            if (showSettings)
            {
                DrawSettings();
            }

            if (!string.IsNullOrWhiteSpace(expandedPrompt))
            {
                EditorGUILayout.Space(10);
                EditorGUILayout.LabelField("Expanded Prompt", EditorStyles.boldLabel);
                EditorGUILayout.SelectableLabel(expandedPrompt, EditorStyles.textArea, GUILayout.MinHeight(80));
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawStatus()
        {
            MessageType messageType = statusMessage.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)
                ? MessageType.Error
                : generating
                    ? MessageType.Info
                    : MessageType.None;

            EditorGUILayout.HelpBox(statusMessage, messageType);
        }

        private void DrawConceptPreview()
        {
            Texture2D concept = AssetDatabase.LoadAssetAtPath<Texture2D>(currentAsset.ConceptAssetPath);
            if (concept == null)
            {
                return;
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Concept Preview", EditorStyles.boldLabel);

            float availableWidth = Mathf.Max(100f, position.width - 40f);
            float aspect = concept.height > 0 ? (float)concept.width / concept.height : 1f;
            float previewHeight = Mathf.Min(420f, availableWidth / Mathf.Max(0.1f, aspect));
            Rect previewRect = GUILayoutUtility.GetRect(availableWidth, previewHeight, GUILayout.ExpandWidth(true));
            EditorGUI.DrawPreviewTexture(previewRect, concept, null, ScaleMode.ScaleToFit);

            using (new EditorGUI.DisabledScope(generating))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Regenerate Concept", GUILayout.Height(28)))
                {
                    RegenerateConcept();
                }

                if (generate3D && GUILayout.Button("Generate 3D From Preview", GUILayout.Height(28)))
                {
                    StartModelGenerationFromPreview();
                }

                if (GUILayout.Button("Reveal", GUILayout.Width(72), GUILayout.Height(28)))
                {
                    RevealAsset(currentAsset.ConceptAssetPath);
                }
            }
        }

        private void DrawSettings()
        {
            EditorGUI.BeginChangeCheck();

            qwenEndpoint = EditorGUILayout.TextField("Qwen Endpoint", qwenEndpoint);
            qwenModel = EditorGUILayout.TextField("Qwen Model", qwenModel);
            qwenApiKey = EditorGUILayout.PasswordField("Qwen API Key", qwenApiKey);
            qwenAutoStart = EditorGUILayout.Toggle("Auto Start Qwen", qwenAutoStart);
            using (new EditorGUI.DisabledScope(!qwenAutoStart))
            {
                qwenPythonPath = EditorGUILayout.TextField("Qwen Python", qwenPythonPath);
                qwenServerScriptPath = EditorGUILayout.TextField("Qwen Server Script", qwenServerScriptPath);
                qwenDevice = EditorGUILayout.TextField("Qwen Device", qwenDevice);
                qwenStartupTimeoutSeconds = EditorGUILayout.IntField("Qwen Startup Timeout", qwenStartupTimeoutSeconds);
            }
            comfyBaseUrl = EditorGUILayout.TextField("ComfyUI Base URL", comfyBaseUrl);
            generationTimeoutSeconds = EditorGUILayout.IntField("Generation Timeout", generationTimeoutSeconds);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("GameAssetAIStudio 3D", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!generate3D))
            {
                studioApiBaseUrl = EditorGUILayout.TextField("Studio API URL", studioApiBaseUrl);
                studioAutoStart = EditorGUILayout.Toggle("Auto Start Studio API", studioAutoStart);
                using (new EditorGUI.DisabledScope(!studioAutoStart))
                {
                    studioProjectRoot = EditorGUILayout.TextField("Studio Project Root", studioProjectRoot);
                    studioPythonPath = EditorGUILayout.TextField("Studio Python", studioPythonPath);
                    studioStartupTimeoutSeconds = EditorGUILayout.IntField("Studio Startup Timeout", studioStartupTimeoutSeconds);
                }
                studioGenerationTimeoutSeconds = EditorGUILayout.IntField("3D Generation Timeout", studioGenerationTimeoutSeconds);
                modelSeed = EditorGUILayout.IntField("3D Seed (0 = Default)", modelSeed);
            }

            if (EditorGUI.EndChangeCheck())
            {
                SaveSettings();
            }
        }

        private void StartGeneration()
        {
            SaveSettings();

            if (string.IsNullOrWhiteSpace(rawDescription))
            {
                SetError("Please enter a source description.");
                return;
            }

            generating = true;
            expandedPrompt = string.Empty;
            currentAsset = null;
            SetStatus("Extending prompt with Qwen...");

            PromptExtender.ExtendPrompt(
                rawDescription,
                assetType,
                prompt =>
                {
                    expandedPrompt = prompt;
                    SetStatus("Prompt extended. Submitting ComfyUI generation...");

                    ComfyClient.GenerateImage(
                        prompt,
                        HandleGeneratedImage,
                        FailGeneration,
                        SetStatus);
                },
                FailGeneration,
                SetStatus);
        }

        private void HandleGeneratedImage(byte[] imageBytes)
        {
            try
            {
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    throw new InvalidOperationException("Generated image payload is empty.");
                }

                currentAsset = CreateAssetPackage(imageBytes, rawDescription, expandedPrompt);
                if (!generate3D)
                {
                    generating = false;
                    RevealAsset(currentAsset.ConceptAssetPath);
                    SetStatus("Saved generated image: " + currentAsset.ConceptAssetPath);
                    return;
                }

                if (reviewConceptBefore3D)
                {
                    generating = false;
                    SetStatus("Concept image ready for review: " + currentAsset.ConceptAssetPath);
                    return;
                }

                StartModelGenerationFromPreview();
            }
            catch (Exception ex)
            {
                FailGeneration("Failed to save generated concept image: " + ex.Message);
            }
        }

        private void RegenerateConcept()
        {
            if (string.IsNullOrWhiteSpace(expandedPrompt))
            {
                StartGeneration();
                return;
            }

            generating = true;
            currentAsset = null;
            SetStatus("Regenerating concept with a new seed...");
            ComfyClient.GenerateImage(
                expandedPrompt,
                HandleGeneratedImage,
                FailGeneration,
                SetStatus);
        }

        private void StartModelGenerationFromPreview()
        {
            if (currentAsset == null)
            {
                SetError("Generate and review a concept image before starting 3D generation.");
                return;
            }

            try
            {
                byte[] imageBytes = File.ReadAllBytes(currentAsset.ConceptFullPath);
                if (imageBytes.Length == 0)
                {
                    throw new InvalidOperationException("The reviewed concept image is empty.");
                }

                generating = true;
                SetStatus("Concept approved. Starting image-to-3D pipeline...");
                GameAssetStudioClient.GenerateModel(
                    currentAsset.Description,
                    currentAsset.Prompt,
                    imageBytes,
                    modelSeed,
                    SaveGeneratedModel,
                    FailModelGeneration,
                    SetStatus);
            }
            catch (Exception ex)
            {
                FailModelGeneration("Failed to read the reviewed concept image: " + ex.Message);
            }
        }

        private GeneratedAssetContext CreateAssetPackage(byte[] imageBytes, string description, string prompt)
        {
            string rootDirectory = Path.Combine(Application.dataPath, "AIGC_Generated");
            Directory.CreateDirectory(rootDirectory);

            string folderName = "aigc_" + DateTime.Now.ToString(
                "yyyyMMdd_HHmmss_fff",
                System.Globalization.CultureInfo.InvariantCulture);
            string outputDirectory = Path.Combine(rootDirectory, folderName);
            Directory.CreateDirectory(outputDirectory);

            string folderAssetPath = OutputFolderAssetPath + "/" + folderName;
            string conceptFullPath = Path.Combine(outputDirectory, "concept.png");
            string conceptAssetPath = folderAssetPath + "/concept.png";
            File.WriteAllBytes(conceptFullPath, imageBytes);
            File.WriteAllText(
                Path.Combine(outputDirectory, "concept_request.json"),
                JsonUtility.ToJson(new ConceptRequestRecord(description, assetType, prompt), true),
                new System.Text.UTF8Encoding(false));

            AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
            AssetDatabase.ImportAsset(conceptAssetPath, ImportAssetOptions.ForceUpdate);
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(conceptAssetPath);
            if (texture == null)
            {
                throw new InvalidOperationException("The PNG was written, but Unity could not import it as a Texture2D.");
            }

            return new GeneratedAssetContext(
                outputDirectory,
                folderAssetPath,
                conceptFullPath,
                conceptAssetPath,
                description,
                prompt);
        }

        private void SaveGeneratedModel(GameAssetStudioResult result)
        {
            try
            {
                if (currentAsset == null)
                {
                    throw new InvalidOperationException("The generated asset output folder is no longer available.");
                }
                if (result == null || result.GlbBytes == null || result.GlbBytes.Length == 0)
                {
                    throw new InvalidOperationException("The generated GLB payload is empty.");
                }
                if (result.ObjBytes == null || result.ObjBytes.Length == 0)
                {
                    throw new InvalidOperationException("The generated OBJ payload is empty.");
                }

                File.WriteAllBytes(Path.Combine(currentAsset.FullPath, "model.glb"), result.GlbBytes);
                File.WriteAllBytes(Path.Combine(currentAsset.FullPath, "model.obj"), result.ObjBytes);
                File.WriteAllText(
                    Path.Combine(currentAsset.FullPath, "manifest.json"),
                    result.ManifestJson,
                    new System.Text.UTF8Encoding(false));
                File.WriteAllText(
                    Path.Combine(currentAsset.FullPath, "quality_report.json"),
                    result.QualityReportJson,
                    new System.Text.UTF8Encoding(false));

                AssetDatabase.Refresh(ImportAssetOptions.ForceUpdate);
                string objAssetPath = currentAsset.AssetPath + "/model.obj";
                AssetDatabase.ImportAsset(objAssetPath, ImportAssetOptions.ForceUpdate);
                GameObject importedModel = AssetDatabase.LoadAssetAtPath<GameObject>(objAssetPath);
                if (importedModel == null)
                {
                    throw new InvalidOperationException("Unity could not import the generated OBJ as a model asset.");
                }

                string prefabAssetPath = CreateEditablePrefab(importedModel, currentAsset.AssetPath);

                generating = false;
                RevealAsset(prefabAssetPath);
                SetStatus(
                    "3D geometry imported. An editable Lit material and prefab were created at " +
                    prefabAssetPath + ". The current Hunyuan workflow generates geometry only, so no color texture is available yet.");
            }
            catch (Exception ex)
            {
                FailModelGeneration("Failed to save or import generated 3D model: " + ex.Message);
            }
        }

        private static string CreateEditablePrefab(GameObject importedModel, string folderAssetPath)
        {
            const string materialFileName = "AIGC_Preview.mat";
            const string prefabFileName = "model.prefab";

            string materialAssetPath = folderAssetPath + "/" + materialFileName;
            Material material = AssetDatabase.LoadAssetAtPath<Material>(materialAssetPath);
            if (material == null)
            {
                Shader shader = Shader.Find("Universal Render Pipeline/Lit") ?? Shader.Find("Standard");
                if (shader == null)
                {
                    throw new InvalidOperationException("Could not find a Lit shader for the generated preview material.");
                }

                material = new Material(shader)
                {
                    name = "AIGC_Preview"
                };
                if (material.HasProperty("_BaseColor"))
                {
                    material.SetColor("_BaseColor", new Color(0.48f, 0.52f, 0.58f, 1f));
                }
                if (material.HasProperty("_Color"))
                {
                    material.SetColor("_Color", new Color(0.48f, 0.52f, 0.58f, 1f));
                }
                if (material.HasProperty("_Metallic"))
                {
                    material.SetFloat("_Metallic", 0f);
                }
                if (material.HasProperty("_Smoothness"))
                {
                    material.SetFloat("_Smoothness", 0.32f);
                }
                AssetDatabase.CreateAsset(material, materialAssetPath);
            }

            GameObject instance = PrefabUtility.InstantiatePrefab(importedModel) as GameObject;
            if (instance == null)
            {
                instance = Instantiate(importedModel);
            }

            try
            {
                instance.name = "AIGC_Model";
                Renderer[] renderers = instance.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0)
                {
                    throw new InvalidOperationException("The imported model contains no renderers.");
                }

                foreach (Renderer renderer in renderers)
                {
                    Material[] materials = renderer.sharedMaterials;
                    if (materials == null || materials.Length == 0)
                    {
                        renderer.sharedMaterial = material;
                        continue;
                    }

                    for (int i = 0; i < materials.Length; i++)
                    {
                        materials[i] = material;
                    }
                    renderer.sharedMaterials = materials;
                }

                string prefabAssetPath = folderAssetPath + "/" + prefabFileName;
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(instance, prefabAssetPath, out bool success);
                if (!success || prefab == null)
                {
                    throw new InvalidOperationException("Unity could not create the editable preview prefab.");
                }

                AssetDatabase.SaveAssets();
                AssetDatabase.ImportAsset(prefabAssetPath, ImportAssetOptions.ForceUpdate);
                return prefabAssetPath;
            }
            finally
            {
                DestroyImmediate(instance);
            }
        }

        private void FailModelGeneration(string message)
        {
            string suffix = currentAsset == null
                ? string.Empty
                : " Concept image was preserved at " + currentAsset.ConceptAssetPath + ".";
            FailGeneration(message + suffix);
        }

        private static void RevealAsset(string assetPath)
        {
            UnityEngine.Object importedAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (importedAsset == null)
            {
                throw new InvalidOperationException("Unity could not load imported asset: " + assetPath);
            }

            EditorUtility.FocusProjectWindow();
            Selection.activeObject = importedAsset;
            EditorGUIUtility.PingObject(importedAsset);
        }

        private void LoadSettings()
        {
            qwenEndpoint = PromptExtender.Endpoint;
            qwenModel = PromptExtender.Model;
            qwenApiKey = PromptExtender.ApiKey;
            qwenAutoStart = PromptExtender.AutoStartServer;
            qwenPythonPath = PromptExtender.PythonPath;
            qwenServerScriptPath = PromptExtender.ServerScriptPath;
            qwenDevice = PromptExtender.Device;
            qwenStartupTimeoutSeconds = PromptExtender.StartupTimeoutSeconds;
            comfyBaseUrl = ComfyClient.ComfyBaseUrl;
            generationTimeoutSeconds = ComfyClient.GenerationTimeoutSeconds;
            int savedAssetType = EditorPrefs.GetInt(AssetTypePrefKey, (int)AigcAssetType.Auto);
            assetType = Enum.IsDefined(typeof(AigcAssetType), savedAssetType)
                ? (AigcAssetType)savedAssetType
                : AigcAssetType.Auto;
            generate3D = EditorPrefs.GetBool(Generate3DPrefKey, true);
            reviewConceptBefore3D = EditorPrefs.GetBool(ReviewConceptPrefKey, true);
            modelSeed = EditorPrefs.GetInt(ModelSeedPrefKey, 0);
            studioApiBaseUrl = GameAssetStudioClient.ApiBaseUrl;
            studioAutoStart = GameAssetStudioClient.AutoStartServer;
            studioProjectRoot = GameAssetStudioClient.ProjectRoot;
            studioPythonPath = GameAssetStudioClient.PythonPath;
            studioStartupTimeoutSeconds = GameAssetStudioClient.StartupTimeoutSeconds;
            studioGenerationTimeoutSeconds = GameAssetStudioClient.GenerationTimeoutSeconds;
        }

        private void SaveSettings()
        {
            PromptExtender.Endpoint = qwenEndpoint;
            PromptExtender.Model = qwenModel;
            PromptExtender.ApiKey = qwenApiKey;
            PromptExtender.AutoStartServer = qwenAutoStart;
            PromptExtender.PythonPath = qwenPythonPath;
            PromptExtender.ServerScriptPath = qwenServerScriptPath;
            PromptExtender.Device = qwenDevice;
            PromptExtender.StartupTimeoutSeconds = qwenStartupTimeoutSeconds;
            ComfyClient.ComfyBaseUrl = comfyBaseUrl;
            ComfyClient.GenerationTimeoutSeconds = generationTimeoutSeconds;
            EditorPrefs.SetInt(AssetTypePrefKey, (int)assetType);
            EditorPrefs.SetBool(Generate3DPrefKey, generate3D);
            EditorPrefs.SetBool(ReviewConceptPrefKey, reviewConceptBefore3D);
            EditorPrefs.SetInt(ModelSeedPrefKey, Mathf.Max(0, modelSeed));
            GameAssetStudioClient.ApiBaseUrl = studioApiBaseUrl;
            GameAssetStudioClient.AutoStartServer = studioAutoStart;
            GameAssetStudioClient.ProjectRoot = studioProjectRoot;
            GameAssetStudioClient.PythonPath = studioPythonPath;
            GameAssetStudioClient.StartupTimeoutSeconds = studioStartupTimeoutSeconds;
            GameAssetStudioClient.GenerationTimeoutSeconds = studioGenerationTimeoutSeconds;
        }

        private void SetStatus(string message)
        {
            statusMessage = string.IsNullOrWhiteSpace(message) ? "Working..." : message;
            Repaint();
        }

        private void SetError(string message)
        {
            statusMessage = "Error: " + message;
            Repaint();
        }

        private void FailGeneration(string message)
        {
            generating = false;
            SetError(message);
        }

        private sealed class GeneratedAssetContext
        {
            public string FullPath { get; }
            public string AssetPath { get; }
            public string ConceptFullPath { get; }
            public string ConceptAssetPath { get; }
            public string Description { get; }
            public string Prompt { get; }

            public GeneratedAssetContext(
                string fullPath,
                string assetPath,
                string conceptFullPath,
                string conceptAssetPath,
                string description,
                string prompt)
            {
                FullPath = fullPath;
                AssetPath = assetPath;
                ConceptFullPath = conceptFullPath;
                ConceptAssetPath = conceptAssetPath;
                Description = description ?? string.Empty;
                Prompt = prompt ?? string.Empty;
            }
        }

        [Serializable]
        private sealed class ConceptRequestRecord
        {
            public string description;
            public string assetType;
            public string expandedPrompt;
            public string generatedAtUtc;

            public ConceptRequestRecord(string sourceDescription, AigcAssetType sourceAssetType, string prompt)
            {
                description = sourceDescription ?? string.Empty;
                assetType = sourceAssetType.ToString();
                expandedPrompt = prompt ?? string.Empty;
                generatedAtUtc = DateTime.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);
            }
        }
    }
}
