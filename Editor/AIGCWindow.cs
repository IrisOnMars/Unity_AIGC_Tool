using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace AIGC.Toolchain.Editor
{
    public sealed class AIGCWindow : EditorWindow
    {
        private const string OutputFolderAssetPath = "Assets/AIGC_Generated";

        private string rawDescription = "elf archer";
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
        private string statusMessage = "Ready.";
        private string expandedPrompt;
        private bool generating;
        private bool showSettings = true;
        private Vector2 scrollPosition;

        [MenuItem("AIGC Tool/Asset Generator")]
        public static void Open()
        {
            AIGCWindow window = GetWindow<AIGCWindow>("AIGC Asset Generator");
            window.minSize = new Vector2(460, 420);
            window.Show();
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

            using (new EditorGUI.DisabledScope(generating))
            {
                if (GUILayout.Button("Generate Asset", GUILayout.Height(32)))
                {
                    StartGeneration();
                }
            }

            EditorGUILayout.Space(8);
            DrawStatus();

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
            SetStatus("Extending prompt with Qwen...");

            PromptExtender.ExtendPrompt(
                rawDescription,
                prompt =>
                {
                    expandedPrompt = prompt;
                    SetStatus("Prompt extended. Submitting ComfyUI generation...");

                    ComfyClient.GenerateImage(
                        prompt,
                        SaveGeneratedImage,
                        FailGeneration,
                        SetStatus);
                },
                FailGeneration,
                SetStatus);
        }

        private void SaveGeneratedImage(byte[] imageBytes)
        {
            try
            {
                if (imageBytes == null || imageBytes.Length == 0)
                {
                    throw new InvalidOperationException("Generated image payload is empty.");
                }

                string outputDirectory = Path.Combine(Application.dataPath, "AIGC_Generated");
                Directory.CreateDirectory(outputDirectory);

                string fileName = "aigc_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture) + ".png";
                string fullPath = Path.Combine(outputDirectory, fileName);
                File.WriteAllBytes(fullPath, imageBytes);

                string assetPath = OutputFolderAssetPath + "/" + fileName;
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
                AssetDatabase.Refresh();

                UnityEngine.Object importedAsset = AssetDatabase.LoadAssetAtPath<Texture2D>(assetPath);
                if (importedAsset == null)
                {
                    throw new InvalidOperationException("The PNG was written, but Unity could not import it as a Texture2D.");
                }

                EditorUtility.FocusProjectWindow();
                Selection.activeObject = importedAsset;
                EditorGUIUtility.PingObject(importedAsset);

                generating = false;
                SetStatus("Saved generated asset: " + assetPath);
            }
            catch (Exception ex)
            {
                FailGeneration("Failed to save or import generated image: " + ex.Message);
            }
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
    }
}
