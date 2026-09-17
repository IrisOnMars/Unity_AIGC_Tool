using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using Debug = UnityEngine.Debug;
using Process = System.Diagnostics.Process;
using ProcessStartInfo = System.Diagnostics.ProcessStartInfo;

namespace AIGC.Toolchain.Editor
{
    public sealed class GameAssetStudioResult
    {
        public byte[] GlbBytes { get; }
        public byte[] ObjBytes { get; }
        public string ManifestJson { get; }
        public string QualityReportJson { get; }

        public GameAssetStudioResult(byte[] glbBytes, byte[] objBytes, string manifestJson, string qualityReportJson)
        {
            GlbBytes = glbBytes;
            ObjBytes = objBytes;
            ManifestJson = manifestJson ?? string.Empty;
            QualityReportJson = qualityReportJson ?? string.Empty;
        }
    }

    public static class GameAssetStudioClient
    {
        public const string DefaultApiBaseUrl = "http://127.0.0.1:7861";
        public const string DefaultProjectRoot = @"F:\GameAssetAIStudio";

        private const string ApiBaseUrlPrefKey = "AIGC.Toolchain.GameAssetStudio.ApiBaseUrl";
        private const string AutoStartPrefKey = "AIGC.Toolchain.GameAssetStudio.AutoStart";
        private const string ProjectRootPrefKey = "AIGC.Toolchain.GameAssetStudio.ProjectRoot";
        private const string PythonPathPrefKey = "AIGC.Toolchain.GameAssetStudio.PythonPath";
        private const string StartupTimeoutPrefKey = "AIGC.Toolchain.GameAssetStudio.StartupTimeoutSeconds";
        private const string GenerationTimeoutPrefKey = "AIGC.Toolchain.GameAssetStudio.GenerationTimeoutSeconds";
        private const int HealthTimeoutSeconds = 3;
        private const int SubmitTimeoutSeconds = 45;
        private const int PollRequestTimeoutSeconds = 15;
        private const int DownloadTimeoutSeconds = 120;
        private const double PollIntervalSeconds = 1.0d;

        public static string ApiBaseUrl
        {
            get => NormalizeBaseUrl(EditorPrefs.GetString(ApiBaseUrlPrefKey, DefaultApiBaseUrl));
            set => EditorPrefs.SetString(ApiBaseUrlPrefKey, NormalizeBaseUrl(value));
        }

        public static bool AutoStartServer
        {
            get => EditorPrefs.GetBool(AutoStartPrefKey, true);
            set => EditorPrefs.SetBool(AutoStartPrefKey, value);
        }

        public static string ProjectRoot
        {
            get => EditorPrefs.GetString(ProjectRootPrefKey, DefaultProjectRoot);
            set => EditorPrefs.SetString(ProjectRootPrefKey, string.IsNullOrWhiteSpace(value) ? DefaultProjectRoot : value.Trim());
        }

        public static string PythonPath
        {
            get
            {
                string fallback = Path.Combine(ProjectRoot, ".venv", "Scripts", "python.exe");
                return EditorPrefs.GetString(PythonPathPrefKey, fallback);
            }
            set
            {
                string fallback = Path.Combine(ProjectRoot, ".venv", "Scripts", "python.exe");
                EditorPrefs.SetString(PythonPathPrefKey, string.IsNullOrWhiteSpace(value) ? fallback : value.Trim());
            }
        }

        public static int StartupTimeoutSeconds
        {
            get => Mathf.Max(5, EditorPrefs.GetInt(StartupTimeoutPrefKey, 30));
            set => EditorPrefs.SetInt(StartupTimeoutPrefKey, Mathf.Max(5, value));
        }

        public static int GenerationTimeoutSeconds
        {
            get => Mathf.Max(60, EditorPrefs.GetInt(GenerationTimeoutPrefKey, 7200));
            set => EditorPrefs.SetInt(GenerationTimeoutPrefKey, Mathf.Max(60, value));
        }

        public static void GenerateModel(
            string description,
            byte[] conceptPng,
            int seed,
            Action<GameAssetStudioResult> onSuccess,
            Action<string> onError,
            Action<string> onStatus = null)
        {
            GenerateModel(
                description,
                string.Empty,
                InferAssetCategory(description, string.Empty),
                conceptPng,
                seed,
                onSuccess,
                onError,
                onStatus);
        }

        public static void GenerateModel(
            string description,
            string conceptPrompt,
            byte[] conceptPng,
            int seed,
            Action<GameAssetStudioResult> onSuccess,
            Action<string> onError,
            Action<string> onStatus = null)
        {
            GenerateModel(
                description,
                conceptPrompt,
                InferAssetCategory(description, conceptPrompt),
                conceptPng,
                seed,
                onSuccess,
                onError,
                onStatus);
        }

        private static void GenerateModel(
            string description,
            string conceptPrompt,
            string category,
            byte[] conceptPng,
            int seed,
            Action<GameAssetStudioResult> onSuccess,
            Action<string> onError,
            Action<string> onStatus)
        {
            if (onError == null)
            {
                onError = Debug.LogError;
            }

            if (onSuccess == null)
            {
                onError("GameAssetStudioClient requires a success callback.");
                return;
            }

            if (string.IsNullOrWhiteSpace(description))
            {
                onError("A description is required for 3D generation.");
                return;
            }

            if (conceptPng == null || conceptPng.Length == 0)
            {
                onError("The concept image payload is empty.");
                return;
            }

            new GenerationSession(description.Trim(), conceptPrompt, category, conceptPng, seed, onSuccess, onError, onStatus).Start();
        }

        private static string InferAssetCategory(string description, string conceptPrompt)
        {
            string descriptionValue = (description ?? string.Empty).ToLowerInvariant();
            string promptValue = (conceptPrompt ?? string.Empty).ToLowerInvariant();
            string[] characterTerms =
            {
                "character", "elf", "archer", "bowman", "warrior", "mage", "wizard", "knight",
                "sorcerer", "witch", "paladin", "rogue", "druid", "princess", "humanoid", "human", "person",
                "\u7cbe\u7075", "\u5f13\u7bad\u624b", "\u89d2\u8272", "\u4eba\u7269", "\u6218\u58eb", "\u6cd5\u5e08", "\u9a91\u58eb"
            };

            foreach (string term in characterTerms)
            {
                if (descriptionValue.Contains(term))
                {
                    return "character";
                }
            }

            string[] explicitPromptTerms =
            {
                "elf", "archer", "bowman", "warrior", "mage", "wizard", "knight", "sorcerer", "witch",
                "paladin", "rogue", "druid", "princess", "humanoid", "human", "person", "\u7cbe\u7075", "\u5f13\u7bad\u624b"
            };
            foreach (string term in explicitPromptTerms)
            {
                if (promptValue.Contains(term))
                {
                    return "character";
                }
            }

            return "prop";
        }

        private sealed class GenerationSession
        {
            private readonly string description;
            private readonly string conceptPrompt;
            private readonly string category;
            private readonly byte[] conceptPng;
            private readonly int seed;
            private readonly Action<GameAssetStudioResult> onSuccess;
            private readonly Action<string> onError;
            private readonly Action<string> onStatus;
            private readonly string baseUrl;

            private Process ownedServerProcess;
            private string stdoutLogPath;
            private string stderrLogPath;
            private string jobId;
            private double startupStartedAt;
            private double generationStartedAt;
            private double nextPollAt;
            private bool requestInFlight;
            private bool terminal;

            public GenerationSession(
                string description,
                string conceptPrompt,
                string category,
                byte[] conceptPng,
                int seed,
                Action<GameAssetStudioResult> onSuccess,
                Action<string> onError,
                Action<string> onStatus)
            {
                this.description = description;
                this.conceptPrompt = conceptPrompt ?? string.Empty;
                this.category = string.IsNullOrWhiteSpace(category) ? "prop" : category.Trim();
                this.conceptPng = conceptPng;
                this.seed = seed;
                this.onSuccess = onSuccess;
                this.onError = onError;
                this.onStatus = onStatus;
                baseUrl = ApiBaseUrl;
            }

            public void Start()
            {
                onStatus?.Invoke("Checking GameAssetAIStudio service...");
                AigcEditorWebRequest.GetText(
                    CombineUrl(baseUrl, "/v1/health"),
                    HealthTimeoutSeconds,
                    _ => SubmitJob(),
                    error =>
                    {
                        if (!AutoStartServer)
                        {
                            Fail("GameAssetAIStudio service is unavailable and auto-start is disabled. " + error);
                            return;
                        }

                        StartOwnedServer();
                    });
            }

            private void StartOwnedServer()
            {
                string projectRoot = ProjectRoot;
                string pythonPath = PythonPath;
                string configPath = Path.Combine(projectRoot, "config", "project.json");

                if (!Directory.Exists(projectRoot))
                {
                    Fail("GameAssetAIStudio project root was not found: " + projectRoot);
                    return;
                }

                if (!File.Exists(pythonPath))
                {
                    Fail("GameAssetAIStudio Python executable was not found: " + pythonPath);
                    return;
                }

                if (!File.Exists(configPath))
                {
                    Fail("GameAssetAIStudio config was not found: " + configPath);
                    return;
                }

                if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out Uri apiUri))
                {
                    Fail("GameAssetAIStudio API URL is invalid: " + baseUrl);
                    return;
                }

                try
                {
                    stdoutLogPath = BuildLogPath("out");
                    stderrLogPath = BuildLogPath("err");
                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = pythonPath,
                        Arguments = "-m asset_pipeline.api_server --config " + Quote(configPath)
                                    + " --host " + Quote(apiUri.Host)
                                    + " --port " + apiUri.Port.ToString(CultureInfo.InvariantCulture),
                        WorkingDirectory = projectRoot,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    startInfo.EnvironmentVariables["PYTHONPATH"] = projectRoot;
                    startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                    startInfo.EnvironmentVariables["GAME_ASSET_API_OUT_LOG"] = stdoutLogPath;
                    startInfo.EnvironmentVariables["GAME_ASSET_API_ERR_LOG"] = stderrLogPath;
                    ownedServerProcess = Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    Fail("Failed to start GameAssetAIStudio service: " + ex.Message);
                    return;
                }

                if (ownedServerProcess == null)
                {
                    Fail("Failed to start GameAssetAIStudio service: process was null.");
                    return;
                }

                startupStartedAt = EditorApplication.timeSinceStartup;
                nextPollAt = startupStartedAt;
                onStatus?.Invoke("Starting GameAssetAIStudio service...");
                EditorApplication.update += PollServerStartup;
            }

            private void PollServerStartup()
            {
                if (terminal)
                {
                    EditorApplication.update -= PollServerStartup;
                    return;
                }

                if (ownedServerProcess != null && HasExited(ownedServerProcess))
                {
                    EditorApplication.update -= PollServerStartup;
                    Fail("GameAssetAIStudio service exited before it became ready." + ReadLogSummary());
                    return;
                }

                double now = EditorApplication.timeSinceStartup;
                if (now - startupStartedAt > StartupTimeoutSeconds)
                {
                    EditorApplication.update -= PollServerStartup;
                    Fail("GameAssetAIStudio service did not become ready within " + StartupTimeoutSeconds + " seconds." + ReadLogSummary());
                    return;
                }

                if (requestInFlight || now < nextPollAt)
                {
                    return;
                }

                requestInFlight = true;
                AigcEditorWebRequest.GetText(
                    CombineUrl(baseUrl, "/v1/health"),
                    HealthTimeoutSeconds,
                    _ =>
                    {
                        requestInFlight = false;
                        EditorApplication.update -= PollServerStartup;
                        SubmitJob();
                    },
                    _ =>
                    {
                        requestInFlight = false;
                        nextPollAt = EditorApplication.timeSinceStartup + PollIntervalSeconds;
                    });
            }

            private void SubmitJob()
            {
                string requestJson;
                try
                {
                    Dictionary<string, object> payload = new Dictionary<string, object>
                    {
                        ["description"] = description,
                        ["image_base64"] = Convert.ToBase64String(conceptPng),
                        ["category"] = category,
                        ["style"] = category == "character" ? "stylized fantasy game character" : "stylized fantasy game asset",
                        ["materials"] = category == "character" ? "fabric, leather, painted metal, wood" : "painted metal, wood, glass",
                        ["reference_mode"] = "single_view",
                        ["timeout"] = (long)GenerationTimeoutSeconds
                    };
                    if (!string.IsNullOrWhiteSpace(conceptPrompt))
                    {
                        payload["concept_prompt"] = conceptPrompt.Trim();
                    }
                    if (seed > 0)
                    {
                        payload["seed"] = (long)seed;
                    }
                    requestJson = MiniJson.Serialize(payload);
                }
                catch (Exception ex)
                {
                    Fail("Failed to build 3D generation request: " + ex.Message);
                    return;
                }

                onStatus?.Invoke("Submitting concept image for Hunyuan3D generation...");
                AigcEditorWebRequest.PostJson(
                    CombineUrl(baseUrl, "/v1/jobs"),
                    requestJson,
                    SubmitTimeoutSeconds,
                    response =>
                    {
                        try
                        {
                            Dictionary<string, object> root = ParseObject(response, "GameAssetAIStudio job response");
                            jobId = ReadRequiredString(root, "id", "GameAssetAIStudio job response did not contain id.");
                        }
                        catch (Exception ex)
                        {
                            Fail(ex.Message);
                            return;
                        }

                        generationStartedAt = EditorApplication.timeSinceStartup;
                        nextPollAt = generationStartedAt;
                        onStatus?.Invoke("3D generation job submitted: " + jobId);
                        EditorApplication.update += PollJob;
                    },
                    error => Fail("GameAssetAIStudio job submission failed. " + error));
            }

            private void PollJob()
            {
                if (terminal)
                {
                    EditorApplication.update -= PollJob;
                    return;
                }

                double now = EditorApplication.timeSinceStartup;
                if (now - generationStartedAt > GenerationTimeoutSeconds)
                {
                    Fail("3D generation timed out after " + GenerationTimeoutSeconds + " seconds.");
                    return;
                }

                if (requestInFlight || now < nextPollAt)
                {
                    return;
                }

                requestInFlight = true;
                AigcEditorWebRequest.GetText(
                    CombineUrl(baseUrl, "/v1/jobs/" + jobId),
                    PollRequestTimeoutSeconds,
                    response =>
                    {
                        requestInFlight = false;
                        HandleJobStatus(response);
                    },
                    error =>
                    {
                        requestInFlight = false;
                        Fail("GameAssetAIStudio job polling failed. " + error);
                    });
            }

            private void HandleJobStatus(string response)
            {
                try
                {
                    Dictionary<string, object> root = ParseObject(response, "GameAssetAIStudio job status");
                    string status = ReadRequiredString(root, "status", "GameAssetAIStudio job status did not contain status.").ToLowerInvariant();
                    string message = ReadOptionalString(root, "message");
                    string progress = ReadOptionalString(root, "progress");

                    if (status == "failed")
                    {
                        string error = ReadOptionalString(root, "error");
                        Fail(string.IsNullOrWhiteSpace(error) ? "GameAssetAIStudio 3D generation failed." : error);
                        return;
                    }

                    if (status == "completed")
                    {
                        Dictionary<string, object> result = root.TryGetValue("result", out object value)
                            ? value as Dictionary<string, object>
                            : null;
                        if (result == null)
                        {
                            Fail("Completed 3D job did not contain result URLs.");
                            return;
                        }

                        EditorApplication.update -= PollJob;
                        DownloadResults(result);
                        return;
                    }

                    onStatus?.Invoke(string.IsNullOrWhiteSpace(message)
                        ? "Hunyuan3D generation is running..."
                        : message + (string.IsNullOrWhiteSpace(progress) ? string.Empty : " (" + progress + "%)"));
                    nextPollAt = EditorApplication.timeSinceStartup + PollIntervalSeconds;
                }
                catch (Exception ex)
                {
                    Fail(ex.Message);
                }
            }

            private void DownloadResults(Dictionary<string, object> result)
            {
                string glbUrl = ResolveResultUrl(ReadRequiredString(result, "glb_url", "3D result did not contain glb_url."));
                string objUrl = ResolveResultUrl(ReadRequiredString(result, "obj_url", "3D result did not contain obj_url."));
                string manifestUrl = ResolveResultUrl(ReadRequiredString(result, "manifest_url", "3D result did not contain manifest_url."));
                string qualityUrl = ResolveResultUrl(ReadRequiredString(result, "quality_url", "3D result did not contain quality_url."));

                onStatus?.Invoke("Downloading canonical GLB model...");
                AigcEditorWebRequest.GetBytes(
                    glbUrl,
                    DownloadTimeoutSeconds,
                    glbBytes =>
                    {
                        if (glbBytes == null || glbBytes.Length == 0)
                        {
                            Fail("GameAssetAIStudio returned an empty GLB file.");
                            return;
                        }

                        onStatus?.Invoke("Downloading Unity OBJ model...");
                        AigcEditorWebRequest.GetBytes(
                            objUrl,
                            DownloadTimeoutSeconds,
                            objBytes =>
                            {
                                if (objBytes == null || objBytes.Length == 0)
                                {
                                    Fail("GameAssetAIStudio returned an empty OBJ file.");
                                    return;
                                }

                                DownloadMetadata(glbBytes, objBytes, manifestUrl, qualityUrl);
                            },
                            error => Fail("Unity OBJ download failed. " + error));
                    },
                    error => Fail("Canonical GLB download failed. " + error));
            }

            private void DownloadMetadata(byte[] glbBytes, byte[] objBytes, string manifestUrl, string qualityUrl)
            {
                onStatus?.Invoke("Downloading asset manifest...");
                AigcEditorWebRequest.GetText(
                    manifestUrl,
                    DownloadTimeoutSeconds,
                    manifestJson =>
                    {
                        onStatus?.Invoke("Downloading quality report...");
                        AigcEditorWebRequest.GetText(
                            qualityUrl,
                            DownloadTimeoutSeconds,
                            qualityJson => Succeed(new GameAssetStudioResult(glbBytes, objBytes, manifestJson, qualityJson)),
                            error => Fail("Quality report download failed. " + error));
                    },
                    error => Fail("Asset manifest download failed. " + error));
            }

            private string ResolveResultUrl(string value)
            {
                if (Uri.TryCreate(value, UriKind.Absolute, out Uri absolute)
                    && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
                {
                    return absolute.ToString();
                }
                return CombineUrl(baseUrl, value);
            }

            private void Succeed(GameAssetStudioResult result)
            {
                if (terminal)
                {
                    return;
                }
                terminal = true;
                EditorApplication.update -= PollServerStartup;
                EditorApplication.update -= PollJob;
                onSuccess(result);
            }

            private void Fail(string message)
            {
                if (terminal)
                {
                    return;
                }
                terminal = true;
                EditorApplication.update -= PollServerStartup;
                EditorApplication.update -= PollJob;
                onError(message);
            }

            private string ReadLogSummary()
            {
                string stdout = ReadLogTail(stdoutLogPath, 1200);
                string stderr = ReadLogTail(stderrLogPath, 1800);
                StringBuilder result = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(stdout))
                {
                    result.Append("\nStudio stdout:\n").Append(stdout);
                }
                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    result.Append("\nStudio stderr:\n").Append(stderr);
                }
                return result.Length == 0 ? "\nNo GameAssetAIStudio log output was available." : result.ToString();
            }
        }

        private static Dictionary<string, object> ParseObject(string json, string label)
        {
            object parsed;
            try
            {
                parsed = MiniJson.Deserialize(json);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(label + " returned invalid JSON: " + ex.Message);
            }
            if (parsed is Dictionary<string, object> root)
            {
                return root;
            }
            throw new InvalidOperationException(label + " is not a JSON object.");
        }

        private static string ReadRequiredString(Dictionary<string, object> root, string key, string error)
        {
            string value = ReadOptionalString(root, key);
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidOperationException(error);
            }
            return value.Trim();
        }

        private static string ReadOptionalString(Dictionary<string, object> root, string key)
        {
            if (!root.TryGetValue(key, out object value) || value == null)
            {
                return string.Empty;
            }
            if (value is double number)
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }
            if (value is long integer)
            {
                return integer.ToString(CultureInfo.InvariantCulture);
            }
            return value.ToString();
        }

        private static string NormalizeBaseUrl(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? DefaultApiBaseUrl : value.Trim();
            return value.TrimEnd('/');
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            baseUrl = NormalizeBaseUrl(baseUrl);
            return path.StartsWith("/", StringComparison.Ordinal) ? baseUrl + path : baseUrl + "/" + path;
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static bool HasExited(Process process)
        {
            try
            {
                return process == null || process.HasExited;
            }
            catch
            {
                return true;
            }
        }

        private static string BuildLogPath(string kind)
        {
            return Path.Combine(Path.GetTempPath(), "game_asset_studio_" + Guid.NewGuid().ToString("N") + "." + kind + ".log");
        }

        private static string ReadLogTail(string path, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return string.Empty;
            }
            try
            {
                string text;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    text = reader.ReadToEnd();
                }
                text = text.Trim();
                return text.Length <= maxChars ? text : text.Substring(text.Length - maxChars);
            }
            catch (Exception ex)
            {
                return "Failed to read log " + path + ": " + ex.Message;
            }
        }
    }
}
