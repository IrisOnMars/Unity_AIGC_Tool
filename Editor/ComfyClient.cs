using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace AIGC.Toolchain.Editor
{
    public static class ComfyClient
    {
        public const string DefaultComfyBaseUrl = "http://127.0.0.1:8188";

        private const string ComfyBaseUrlPrefKey = "AIGC.Toolchain.ComfyBaseUrl";
        private const string GenerationTimeoutPrefKey = "AIGC.Toolchain.GenerationTimeoutSeconds";
        private const string TemplateFileName = "single_image_api.json";
        private const string PromptPlaceholder = "__PROMPT__";
        private const string NegativePromptPlaceholder = "__NEGATIVE_PROMPT__";
        private const double PollIntervalSeconds = 1.0d;
        private const int SubmitTimeoutSeconds = 30;
        private const int PollRequestTimeoutSeconds = 15;
        private const int DownloadTimeoutSeconds = 60;
        private const int DefaultGenerationTimeoutSeconds = 600;

        public static string ComfyBaseUrl
        {
            get => NormalizeBaseUrl(EditorPrefs.GetString(ComfyBaseUrlPrefKey, DefaultComfyBaseUrl));
            set => EditorPrefs.SetString(ComfyBaseUrlPrefKey, NormalizeBaseUrl(value));
        }

        public static int GenerationTimeoutSeconds
        {
            get => Mathf.Max(30, EditorPrefs.GetInt(GenerationTimeoutPrefKey, DefaultGenerationTimeoutSeconds));
            set => EditorPrefs.SetInt(GenerationTimeoutPrefKey, Mathf.Max(30, value));
        }

        public static void GenerateImage(
            string finalPrompt,
            Action<byte[]> onSuccess,
            Action<string> onError,
            Action<string> onStatus = null)
        {
            if (onError == null)
            {
                onError = Debug.LogError;
            }

            if (onSuccess == null)
            {
                onError("ComfyClient requires a success callback.");
                return;
            }

            if (string.IsNullOrWhiteSpace(finalPrompt))
            {
                onError("The final prompt is empty.");
                return;
            }

            string requestJson;
            try
            {
                requestJson = BuildPromptRequestJson(finalPrompt);
            }
            catch (Exception ex)
            {
                onError(ex.Message);
                return;
            }

            onStatus?.Invoke("Submitting prompt to ComfyUI...");

            AigcEditorWebRequest.PostJson(
                CombineUrl(ComfyBaseUrl, "/prompt"),
                requestJson,
                SubmitTimeoutSeconds,
                responseText =>
                {
                    string promptId;
                    try
                    {
                        promptId = ParsePromptId(responseText);
                    }
                    catch (Exception ex)
                    {
                        onError(ex.Message);
                        return;
                    }

                    onStatus?.Invoke($"ComfyUI prompt submitted: {promptId}");
                    new HistoryPoller(promptId, onSuccess, onError, onStatus).Start();
                },
                error => onError($"ComfyUI /prompt request failed. {error}"));
        }

        private static string BuildPromptRequestJson(string finalPrompt)
        {
            string templatePath = FindTemplatePath();
            string templateJson;

            try
            {
                templateJson = File.ReadAllText(templatePath, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to read {TemplateFileName}: {ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(templateJson))
            {
                throw new InvalidOperationException($"{TemplateFileName} is empty.");
            }

            if (!templateJson.Contains(PromptPlaceholder))
            {
                throw new InvalidOperationException($"{TemplateFileName} must contain the {PromptPlaceholder} placeholder inside the positive prompt string.");
            }

            string replacedJson = templateJson.Replace(PromptPlaceholder, MiniJson.EscapeString(finalPrompt));
            if (replacedJson.Contains(NegativePromptPlaceholder))
            {
                string dynamicNegativePrompt = BuildDynamicNegativePrompt(finalPrompt);
                replacedJson = replacedJson.Replace(NegativePromptPlaceholder, MiniJson.EscapeString(dynamicNegativePrompt));
            }
            object parsedTemplate;

            try
            {
                parsedTemplate = MiniJson.Deserialize(replacedJson);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Failed to parse {TemplateFileName} after prompt replacement: {ex.Message}");
            }

            if (parsedTemplate == null)
            {
                throw new InvalidOperationException($"{TemplateFileName} is not valid JSON after prompt replacement.");
            }

            RandomizeGenerationSeeds(parsedTemplate);

            Dictionary<string, object> requestRoot;
            if (parsedTemplate is Dictionary<string, object> root && root.ContainsKey("prompt"))
            {
                requestRoot = new Dictionary<string, object>(root);
            }
            else
            {
                requestRoot = new Dictionary<string, object>
                {
                    ["prompt"] = parsedTemplate
                };
            }

            if (!requestRoot.ContainsKey("client_id"))
            {
                requestRoot["client_id"] = $"unity-aigc-toolchain-{Guid.NewGuid():N}";
            }

            return MiniJson.Serialize(requestRoot);
        }

        private static void RandomizeGenerationSeeds(object workflow)
        {
            if (!(workflow is Dictionary<string, object> nodes))
            {
                return;
            }

            if (nodes.TryGetValue("prompt", out object promptValue) &&
                promptValue is Dictionary<string, object> promptNodes)
            {
                nodes = promptNodes;
            }

            foreach (KeyValuePair<string, object> nodeEntry in nodes)
            {
                if (!(nodeEntry.Value is Dictionary<string, object> node) ||
                    !node.TryGetValue("inputs", out object inputsValue) ||
                    !(inputsValue is Dictionary<string, object> inputs))
                {
                    continue;
                }

                RandomizeNumericSeed(inputs, "seed");
                RandomizeNumericSeed(inputs, "noise_seed");
            }
        }

        private static void RandomizeNumericSeed(Dictionary<string, object> inputs, string key)
        {
            if (!inputs.TryGetValue(key, out object currentValue) || !IsNumericJsonValue(currentValue))
            {
                return;
            }

            byte[] bytes = Guid.NewGuid().ToByteArray();
            long seed = BitConverter.ToInt64(bytes, 0) & long.MaxValue;
            inputs[key] = seed;
        }

        private static bool IsNumericJsonValue(object value)
        {
            return value is byte || value is sbyte || value is short || value is ushort ||
                   value is int || value is uint || value is long || value is ulong ||
                   value is float || value is double || value is decimal;
        }

        private static string BuildDynamicNegativePrompt(string finalPrompt)
        {
            string lower = (finalPrompt ?? string.Empty).ToLowerInvariant();
            List<string> terms = new List<string>
            {
                "display pedestal",
                "round base",
                "stage",
                "spotlight beam",
                "light column",
                "dramatic backdrop",
                "colored background glow",
                "cast shadow",
                "background silhouette",
                "inset image"
            };

            bool isCharacter = ContainsAny(
                lower,
                "game character", "one character", "full-body figure", "visible face", "elf", "archer", "warrior", "knight", "mage");
            bool isCreature = ContainsAny(lower, "game creature", "one creature", "creature anatomy");
            bool isProp = ContainsAny(lower, "game prop", "one object only", "isolated object", "centered object");
            bool isArcher = isCharacter && ContainsAny(lower, "archer", "bowman", "bow user", "longbow");
            bool isElf = isCharacter && ContainsAny(lower, "elf", "elven");
            bool requestsMagic = ContainsAny(
                lower,
                "magic archer", "arcane archer", "mage", "wizard", "sorcer", "spell",
                "elemental", "fire archer", "ice archer", "lightning archer");

            if (isProp)
            {
                terms.AddRange(new[]
                {
                    "person",
                    "human",
                    "humanoid",
                    "character",
                    "face",
                    "portrait",
                    "full body person",
                    "arms",
                    "legs",
                    "hands",
                    "clothing",
                    "human silhouette"
                });
            }
            else if (isCreature)
            {
                terms.AddRange(new[]
                {
                    "human",
                    "humanoid person",
                    "rider",
                    "handler",
                    "multiple creatures",
                    "duplicate creature"
                });
            }
            else if (isCharacter)
            {
                terms.AddRange(new[]
                {
                    "cropped body",
                    "cropped feet",
                    "duplicate character",
                    "multiple characters",
                    "extra limbs",
                    "humanoid shadow",
                    "inset figure"
                });
            }

            if (isElf)
            {
                terms.AddRange(new[]
                {
                    "human ears",
                    "round ears",
                    "hidden ears",
                    "cropped ears"
                });
            }

            if (isArcher)
            {
                terms.AddRange(new[]
                {
                    "two bows",
                    "two longbows",
                    "multiple bows",
                    "duplicate bow",
                    "second bow",
                    "second longbow",
                    "mirrored weapon",
                    "broken bow",
                    "deformed bow",
                    "sword",
                    "longsword",
                    "blade weapon",
                    "staff",
                    "wand",
                    "spellbook",
                    "scepter",
                    "melee weapon",
                    "empty quiver",
                    "missing bow",
                    "bow cropped out",
                    "hidden bowstring",
                    "character silhouette in background",
                    "shadow person",
                    "reference silhouette",
                    "back view"
                });

                if (!requestsMagic)
                {
                    terms.AddRange(new[]
                    {
                        "mage",
                        "wizard",
                        "spellcaster",
                        "fireball",
                        "magic orb",
                        "glowing rune",
                        "spellcasting pose",
                        "magic particles around hands"
                    });
                }
            }

            return string.Join(", ", terms);
        }

        private static bool ContainsAny(string value, params string[] terms)
        {
            if (string.IsNullOrEmpty(value) || terms == null)
            {
                return false;
            }

            foreach (string term in terms)
            {
                if (!string.IsNullOrEmpty(term) && value.IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }

        private static string FindTemplatePath()
        {
            const string packageRelativePath = "Packages/com.aigc.toolchain/Editor/" + TemplateFileName;
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;

            if (!string.IsNullOrEmpty(projectRoot))
            {
                string expectedPath = Path.Combine(projectRoot, packageRelativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(expectedPath))
                {
                    return expectedPath;
                }
            }

            string[] guids = AssetDatabase.FindAssets(Path.GetFileNameWithoutExtension(TemplateFileName), new[] { "Packages/com.aigc.toolchain/Editor" });
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.Equals(Path.GetFileName(assetPath), TemplateFileName, StringComparison.OrdinalIgnoreCase))
                {
                    string fullPath = Path.GetFullPath(assetPath);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                }
            }

            throw new FileNotFoundException($"Cannot find {TemplateFileName}. Place it at {packageRelativePath} and put {PromptPlaceholder} inside the positive prompt text.");
        }

        private static string ParsePromptId(string responseJson)
        {
            object parsed;
            try
            {
                parsed = MiniJson.Deserialize(responseJson);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"ComfyUI /prompt returned invalid JSON: {ex.Message}");
            }

            Dictionary<string, object> root = parsed as Dictionary<string, object>;
            if (root == null)
            {
                throw new InvalidOperationException("ComfyUI /prompt response is not a JSON object.");
            }

            if (root.TryGetValue("prompt_id", out object promptIdValue))
            {
                string promptId = ConvertJsonValueToString(promptIdValue);
                if (!string.IsNullOrWhiteSpace(promptId))
                {
                    return promptId.Trim();
                }
            }

            throw new InvalidOperationException("ComfyUI /prompt response did not contain prompt_id.");
        }

        private static string NormalizeBaseUrl(string value)
        {
            value = string.IsNullOrWhiteSpace(value) ? DefaultComfyBaseUrl : value.Trim();
            return value.TrimEnd('/');
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            baseUrl = NormalizeBaseUrl(baseUrl);
            return path.StartsWith("/", StringComparison.Ordinal) ? baseUrl + path : baseUrl + "/" + path;
        }

        private static string BuildViewUrl(string baseUrl, ComfyOutputImage image)
        {
            string filename = UnityWebRequest.EscapeURL(image.Filename);
            string subfolder = UnityWebRequest.EscapeURL(image.Subfolder ?? string.Empty);
            string type = UnityWebRequest.EscapeURL(string.IsNullOrWhiteSpace(image.Type) ? "output" : image.Type);
            return $"{CombineUrl(baseUrl, "/view")}?filename={filename}&subfolder={subfolder}&type={type}";
        }

        private static string ConvertJsonValueToString(object value)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value is string)
            {
                return (string)value;
            }

            if (value is double)
            {
                return ((double)value).ToString(CultureInfo.InvariantCulture);
            }

            if (value is float)
            {
                return ((float)value).ToString(CultureInfo.InvariantCulture);
            }

            if (value is long)
            {
                return ((long)value).ToString(CultureInfo.InvariantCulture);
            }

            if (value is int)
            {
                return ((int)value).ToString(CultureInfo.InvariantCulture);
            }

            if (value is bool)
            {
                return (bool)value ? "true" : "false";
            }

            return value.ToString();
        }

        private sealed class HistoryPoller
        {
            private readonly string promptId;
            private readonly Action<byte[]> onSuccess;
            private readonly Action<string> onError;
            private readonly Action<string> onStatus;
            private readonly string baseUrl;
            private readonly double startedAt;
            private readonly int timeoutSeconds;

            private bool terminal;
            private bool requestInFlight;
            private double nextPollAt;

            public HistoryPoller(string promptId, Action<byte[]> onSuccess, Action<string> onError, Action<string> onStatus)
            {
                this.promptId = promptId;
                this.onSuccess = onSuccess;
                this.onError = onError ?? Debug.LogError;
                this.onStatus = onStatus;
                baseUrl = ComfyBaseUrl;
                timeoutSeconds = GenerationTimeoutSeconds;
                startedAt = EditorApplication.timeSinceStartup;
                nextPollAt = startedAt;
            }

            public void Start()
            {
                EditorApplication.update += Tick;
            }

            private void Tick()
            {
                if (terminal)
                {
                    StopPolling();
                    return;
                }

                double now = EditorApplication.timeSinceStartup;
                if (now - startedAt > timeoutSeconds)
                {
                    Fail($"ComfyUI generation timed out after {timeoutSeconds} seconds.");
                    return;
                }

                if (requestInFlight || now < nextPollAt)
                {
                    return;
                }

                requestInFlight = true;
                onStatus?.Invoke($"Polling ComfyUI history for {promptId}...");

                string historyUrl = CombineUrl(baseUrl, "/history/" + UnityWebRequest.EscapeURL(promptId));
                AigcEditorWebRequest.GetText(
                    historyUrl,
                    PollRequestTimeoutSeconds,
                    responseText =>
                    {
                        requestInFlight = false;
                        HandleHistoryResponse(responseText);
                    },
                    error =>
                    {
                        requestInFlight = false;
                        Fail($"ComfyUI history request failed. {error}");
                    });
            }

            private void HandleHistoryResponse(string responseText)
            {
                if (terminal)
                {
                    return;
                }

                object parsed;
                try
                {
                    parsed = MiniJson.Deserialize(responseText);
                }
                catch (Exception ex)
                {
                    Fail($"ComfyUI history returned invalid JSON: {ex.Message}");
                    return;
                }

                Dictionary<string, object> root = parsed as Dictionary<string, object>;
                if (root == null)
                {
                    Fail("ComfyUI history response is not a JSON object.");
                    return;
                }

                if (!TryGetPromptHistory(root, promptId, out Dictionary<string, object> history))
                {
                    ScheduleNextPoll();
                    return;
                }

                if (TryGetFailureStatus(history, out string failureStatus))
                {
                    Fail($"ComfyUI generation failed: {failureStatus}");
                    return;
                }

                bool completed = IsCompleted(history);
                if (TryFindFirstOutputImage(history, out ComfyOutputImage image))
                {
                    onStatus?.Invoke($"Downloading generated image: {image.Filename}");
                    DownloadImage(image);
                    return;
                }

                if (completed)
                {
                    Fail("ComfyUI marked the task as completed, but no output image filename was found in history.");
                    return;
                }

                ScheduleNextPoll();
            }

            private void DownloadImage(ComfyOutputImage image)
            {
                StopPolling();
                requestInFlight = true;

                AigcEditorWebRequest.GetBytes(
                    BuildViewUrl(baseUrl, image),
                    DownloadTimeoutSeconds,
                    bytes =>
                    {
                        requestInFlight = false;
                        if (bytes == null || bytes.Length == 0)
                        {
                            Fail("ComfyUI /view returned an empty image payload.");
                            return;
                        }

                        Succeed(bytes);
                    },
                    error =>
                    {
                        requestInFlight = false;
                        Fail($"ComfyUI /view image download failed. {error}");
                    });
            }

            private void ScheduleNextPoll()
            {
                nextPollAt = EditorApplication.timeSinceStartup + PollIntervalSeconds;
                onStatus?.Invoke("ComfyUI task is still running...");
            }

            private void StopPolling()
            {
                EditorApplication.update -= Tick;
            }

            private void Succeed(byte[] bytes)
            {
                if (terminal)
                {
                    return;
                }

                terminal = true;
                StopPolling();
                onSuccess?.Invoke(bytes);
            }

            private void Fail(string message)
            {
                if (terminal)
                {
                    return;
                }

                terminal = true;
                StopPolling();
                onError?.Invoke(message);
            }

            private static bool TryGetPromptHistory(Dictionary<string, object> root, string promptId, out Dictionary<string, object> history)
            {
                history = null;

                if (root.TryGetValue(promptId, out object historyObject) && historyObject is Dictionary<string, object> promptHistory)
                {
                    history = promptHistory;
                    return true;
                }

                if (root.ContainsKey("status") || root.ContainsKey("outputs"))
                {
                    history = root;
                    return true;
                }

                return false;
            }

            private static bool IsCompleted(Dictionary<string, object> history)
            {
                object statusObject;
                Dictionary<string, object> status;
                if (!history.TryGetValue("status", out statusObject) || (status = statusObject as Dictionary<string, object>) == null)
                {
                    return false;
                }

                if (status.TryGetValue("completed", out object completedValue) && completedValue is bool completed && completed)
                {
                    return true;
                }

                if (status.TryGetValue("status_str", out object statusValue))
                {
                    string statusText = ConvertJsonValueToString(statusValue).Trim().ToLowerInvariant();
                    return statusText == "success" || statusText == "completed";
                }

                return false;
            }

            private static bool TryGetFailureStatus(Dictionary<string, object> history, out string failureStatus)
            {
                failureStatus = null;

                object statusObject;
                Dictionary<string, object> status;
                if (!history.TryGetValue("status", out statusObject) || (status = statusObject as Dictionary<string, object>) == null)
                {
                    return false;
                }

                if (!status.TryGetValue("status_str", out object statusValue))
                {
                    return false;
                }

                string statusText = ConvertJsonValueToString(statusValue).Trim();
                string normalized = statusText.ToLowerInvariant();
                if (normalized.Contains("error") || normalized.Contains("fail"))
                {
                    failureStatus = statusText;

                    if (status.TryGetValue("messages", out object messages))
                    {
                        string serializedMessages = MiniJson.Serialize(messages);
                        if (!string.IsNullOrWhiteSpace(serializedMessages))
                        {
                            failureStatus += " " + Truncate(serializedMessages, 600);
                        }
                    }

                    return true;
                }

                return false;
            }

            private static bool TryFindFirstOutputImage(Dictionary<string, object> history, out ComfyOutputImage image)
            {
                image = default;

                if (!history.TryGetValue("outputs", out object outputs))
                {
                    return false;
                }

                return TryFindFirstImage(outputs, out image);
            }

            private static bool TryFindFirstImage(object node, out ComfyOutputImage image)
            {
                image = default;

                if (node is Dictionary<string, object> dict)
                {
                    if (dict.TryGetValue("images", out object imagesObject) && imagesObject is List<object> images)
                    {
                        foreach (object item in images)
                        {
                            if (TryReadImage(item, out image))
                            {
                                return true;
                            }
                        }
                    }

                    if (TryReadImage(dict, out image))
                    {
                        return true;
                    }

                    foreach (object value in dict.Values)
                    {
                        if (TryFindFirstImage(value, out image))
                        {
                            return true;
                        }
                    }
                }
                else if (node is List<object> list)
                {
                    foreach (object item in list)
                    {
                        if (TryFindFirstImage(item, out image))
                        {
                            return true;
                        }
                    }
                }

                return false;
            }

            private static bool TryReadImage(object node, out ComfyOutputImage image)
            {
                image = default;

                Dictionary<string, object> dict = node as Dictionary<string, object>;
                object filenameValue;
                if (dict == null || !dict.TryGetValue("filename", out filenameValue))
                {
                    return false;
                }

                string filename = ConvertJsonValueToString(filenameValue);
                if (string.IsNullOrWhiteSpace(filename))
                {
                    return false;
                }

                string subfolder = dict.TryGetValue("subfolder", out object subfolderValue) ? ConvertJsonValueToString(subfolderValue) : string.Empty;
                string type = dict.TryGetValue("type", out object typeValue) ? ConvertJsonValueToString(typeValue) : "output";

                image = new ComfyOutputImage(filename.Trim(), subfolder?.Trim() ?? string.Empty, string.IsNullOrWhiteSpace(type) ? "output" : type.Trim());
                return true;
            }
        }

        private readonly struct ComfyOutputImage
        {
            public readonly string Filename;
            public readonly string Subfolder;
            public readonly string Type;

            public ComfyOutputImage(string filename, string subfolder, string type)
            {
                Filename = filename;
                Subfolder = subfolder;
                Type = type;
            }
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength) + "...";
        }
    }

    internal static class AigcEditorWebRequest
    {
        public static void PostJson(
            string url,
            string json,
            int timeoutSeconds,
            Action<string> onSuccess,
            Action<string> onError,
            Dictionary<string, string> headers = null)
        {
            byte[] body = Encoding.UTF8.GetBytes(json ?? string.Empty);
            UnityWebRequest request = new UnityWebRequest(url, "POST")
            {
                uploadHandler = new UploadHandlerRaw(body),
                downloadHandler = new DownloadHandlerBuffer(),
                timeout = Mathf.Max(1, timeoutSeconds)
            };

            request.SetRequestHeader("Content-Type", "application/json; charset=utf-8");
            request.SetRequestHeader("Accept", "application/json");
            ApplyHeaders(request, headers);

            Send(request, completedRequest => onSuccess?.Invoke(completedRequest.downloadHandler?.text ?? string.Empty), onError);
        }

        public static void GetText(string url, int timeoutSeconds, Action<string> onSuccess, Action<string> onError, Dictionary<string, string> headers = null)
        {
            UnityWebRequest request = UnityWebRequest.Get(url);
            request.timeout = Mathf.Max(1, timeoutSeconds);
            request.SetRequestHeader("Accept", "application/json");
            ApplyHeaders(request, headers);

            Send(request, completedRequest => onSuccess?.Invoke(completedRequest.downloadHandler?.text ?? string.Empty), onError);
        }

        public static void GetBytes(string url, int timeoutSeconds, Action<byte[]> onSuccess, Action<string> onError, Dictionary<string, string> headers = null)
        {
            UnityWebRequest request = UnityWebRequest.Get(url);
            request.timeout = Mathf.Max(1, timeoutSeconds);
            ApplyHeaders(request, headers);

            Send(
                request,
                completedRequest =>
                {
                    byte[] data = completedRequest.downloadHandler?.data;
                    if (data == null)
                    {
                        onSuccess?.Invoke(new byte[0]);
                        return;
                    }

                    byte[] copy = new byte[data.Length];
                    Buffer.BlockCopy(data, 0, copy, 0, data.Length);
                    onSuccess?.Invoke(copy);
                },
                onError);
        }

        private static void Send(UnityWebRequest request, Action<UnityWebRequest> onSuccess, Action<string> onError)
        {
            UnityWebRequestAsyncOperation operation;
            try
            {
                operation = request.SendWebRequest();
            }
            catch (Exception ex)
            {
                request.Dispose();
                onError?.Invoke($"Failed to start request: {ex.Message}");
                return;
            }

            EditorApplication.CallbackFunction tick = null;
            tick = () =>
            {
                if (!operation.isDone)
                {
                    return;
                }

                EditorApplication.update -= tick;

                try
                {
                    string error = GetRequestError(request);
                    if (!string.IsNullOrEmpty(error))
                    {
                        onError?.Invoke(error);
                        return;
                    }

                    onSuccess?.Invoke(request);
                }
                catch (Exception ex)
                {
                    onError?.Invoke($"Request callback failed: {ex.Message}");
                }
                finally
                {
                    request.Dispose();
                }
            };

            EditorApplication.update += tick;
        }

        private static void ApplyHeaders(UnityWebRequest request, Dictionary<string, string> headers)
        {
            if (headers == null)
            {
                return;
            }

            foreach (KeyValuePair<string, string> header in headers)
            {
                if (!string.IsNullOrWhiteSpace(header.Key) && header.Value != null)
                {
                    request.SetRequestHeader(header.Key, header.Value);
                }
            }
        }

        private static string GetRequestError(UnityWebRequest request)
        {
            bool failed;

#if UNITY_2020_2_OR_NEWER
            failed = request.result == UnityWebRequest.Result.ConnectionError
                     || request.result == UnityWebRequest.Result.ProtocolError
                     || request.result == UnityWebRequest.Result.DataProcessingError;
#else
            failed = request.isNetworkError || request.isHttpError;
#endif

            if (!failed && (request.responseCode == 0 || (request.responseCode >= 200 && request.responseCode <= 299)))
            {
                return null;
            }

            StringBuilder builder = new StringBuilder();
            if (request.responseCode > 0)
            {
                builder.Append("HTTP ").Append(request.responseCode).Append(". ");
            }

            if (!string.IsNullOrEmpty(request.error))
            {
                builder.Append(request.error).Append(". ");
            }

            string responseBody = request.downloadHandler?.text;
            if (!string.IsNullOrWhiteSpace(responseBody))
            {
                builder.Append("Response: ").Append(Truncate(responseBody.Trim(), 800));
            }

            string message = builder.ToString().Trim();
            return string.IsNullOrEmpty(message) ? "Unknown UnityWebRequest error." : message;
        }

        private static string Truncate(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value;
            }

            return value.Substring(0, maxLength) + "...";
        }
    }

    internal static class MiniJson
    {
        public static object Deserialize(string json)
        {
            if (json == null)
            {
                return null;
            }

            using (Parser parser = new Parser(json))
            {
                return parser.ParseRoot();
            }
        }

        public static string Serialize(object obj)
        {
            return Serializer.Serialize(obj);
        }

        public static string EscapeString(string value)
        {
            return Serializer.EscapeString(value);
        }

        private sealed class Parser : IDisposable
        {
            private readonly StringReader json;

            public Parser(string jsonString)
            {
                json = new StringReader(jsonString);
            }

            public void Dispose()
            {
                json.Dispose();
            }

            public object ParseRoot()
            {
                object value = ParseValue();
                EatWhitespace();

                if (json.Peek() != -1)
                {
                    throw new FormatException("Unexpected trailing characters after JSON value.");
                }

                return value;
            }

            public object ParseValue()
            {
                EatWhitespace();
                if (json.Peek() == -1)
                {
                    return null;
                }

                switch (NextToken)
                {
                    case JsonToken.CurlyOpen:
                        return ParseObject();
                    case JsonToken.SquaredOpen:
                        return ParseArray();
                    case JsonToken.String:
                        return ParseString();
                    case JsonToken.Number:
                        return ParseNumber();
                    case JsonToken.True:
                        return true;
                    case JsonToken.False:
                        return false;
                    case JsonToken.Null:
                        return null;
                    default:
                        throw new FormatException("Unexpected JSON token.");
                }
            }

            private Dictionary<string, object> ParseObject()
            {
                Dictionary<string, object> table = new Dictionary<string, object>();
                json.Read();

                while (true)
                {
                    switch (NextToken)
                    {
                        case JsonToken.None:
                            throw new FormatException("Unterminated JSON object.");
                        case JsonToken.CurlyClose:
                            json.Read();
                            return table;
                        case JsonToken.Comma:
                            json.Read();
                            continue;
                    }

                    string name = ParseString();
                    if (NextToken != JsonToken.Colon)
                    {
                        throw new FormatException("Expected ':' after JSON object key.");
                    }

                    json.Read();
                    table[name] = ParseValue();
                }
            }

            private List<object> ParseArray()
            {
                List<object> array = new List<object>();
                json.Read();

                bool parsing = true;
                while (parsing)
                {
                    JsonToken nextToken = NextToken;
                    switch (nextToken)
                    {
                        case JsonToken.None:
                            throw new FormatException("Unterminated JSON array.");
                        case JsonToken.SquaredClose:
                            json.Read();
                            parsing = false;
                            break;
                        case JsonToken.Comma:
                            json.Read();
                            break;
                        default:
                            array.Add(ParseValue());
                            break;
                    }
                }

                return array;
            }

            private string ParseString()
            {
                if (json.Read() != '"')
                {
                    throw new FormatException("Expected JSON string.");
                }

                StringBuilder builder = new StringBuilder();
                bool parsing = true;

                while (parsing)
                {
                    int next = json.Read();
                    if (next == -1)
                    {
                        throw new FormatException("Unterminated JSON string.");
                    }

                    char c = (char)next;
                    switch (c)
                    {
                        case '"':
                            parsing = false;
                            break;
                        case '\\':
                            builder.Append(ParseEscapedCharacter());
                            break;
                        default:
                            builder.Append(c);
                            break;
                    }
                }

                return builder.ToString();
            }

            private char ParseEscapedCharacter()
            {
                int next = json.Read();
                if (next == -1)
                {
                    throw new FormatException("Unterminated JSON escape sequence.");
                }

                char escaped = (char)next;
                switch (escaped)
                {
                    case '"':
                        return '"';
                    case '\\':
                        return '\\';
                    case '/':
                        return '/';
                    case 'b':
                        return '\b';
                    case 'f':
                        return '\f';
                    case 'n':
                        return '\n';
                    case 'r':
                        return '\r';
                    case 't':
                        return '\t';
                    case 'u':
                        return ParseUnicodeCharacter();
                    default:
                        throw new FormatException($"Invalid JSON escape sequence '\\{escaped}'.");
                }
            }

            private char ParseUnicodeCharacter()
            {
                char[] hex = new char[4];
                for (int i = 0; i < 4; i++)
                {
                    int next = json.Read();
                    if (next == -1)
                    {
                        throw new FormatException("Unterminated JSON unicode escape sequence.");
                    }

                    hex[i] = (char)next;
                }

                return (char)Convert.ToInt32(new string(hex), 16);
            }

            private object ParseNumber()
            {
                string number = NextWord;

                if (number.IndexOfAny(new[] { '.', 'e', 'E' }) != -1)
                {
                    if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedDouble))
                    {
                        return parsedDouble;
                    }
                }
                else if (long.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsedLong))
                {
                    return parsedLong;
                }

                throw new FormatException($"Invalid JSON number '{number}'.");
            }

            private void EatWhitespace()
            {
                while (json.Peek() != -1 && char.IsWhiteSpace(PeekChar))
                {
                    json.Read();
                }
            }

            private char PeekChar => Convert.ToChar(json.Peek());

            private string NextWord
            {
                get
                {
                    StringBuilder word = new StringBuilder();
                    while (json.Peek() != -1 && !IsWordBreak(PeekChar))
                    {
                        word.Append((char)json.Read());
                    }

                    return word.ToString();
                }
            }

            private JsonToken NextToken
            {
                get
                {
                    EatWhitespace();

                    if (json.Peek() == -1)
                    {
                        return JsonToken.None;
                    }

                    switch (PeekChar)
                    {
                        case '{':
                            return JsonToken.CurlyOpen;
                        case '}':
                            return JsonToken.CurlyClose;
                        case '[':
                            return JsonToken.SquaredOpen;
                        case ']':
                            return JsonToken.SquaredClose;
                        case ',':
                            return JsonToken.Comma;
                        case '"':
                            return JsonToken.String;
                        case ':':
                            return JsonToken.Colon;
                        case '0':
                        case '1':
                        case '2':
                        case '3':
                        case '4':
                        case '5':
                        case '6':
                        case '7':
                        case '8':
                        case '9':
                        case '-':
                            return JsonToken.Number;
                    }

                    string word = NextWord;
                    switch (word)
                    {
                        case "false":
                            return JsonToken.False;
                        case "true":
                            return JsonToken.True;
                        case "null":
                            return JsonToken.Null;
                        default:
                            return JsonToken.None;
                    }
                }
            }

            private static bool IsWordBreak(char c)
            {
                return char.IsWhiteSpace(c) || c == ',' || c == ':' || c == ']' || c == '}' || c == '[' || c == '{';
            }
        }

        private enum JsonToken
        {
            None,
            CurlyOpen,
            CurlyClose,
            SquaredOpen,
            SquaredClose,
            Colon,
            Comma,
            String,
            Number,
            True,
            False,
            Null
        }

        private sealed class Serializer
        {
            private readonly StringBuilder builder = new StringBuilder();

            public static string Serialize(object obj)
            {
                Serializer serializer = new Serializer();
                serializer.SerializeValue(obj);
                return serializer.builder.ToString();
            }

            public static string EscapeString(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return string.Empty;
                }

                StringBuilder escaped = new StringBuilder();
                foreach (char c in value)
                {
                    switch (c)
                    {
                        case '"':
                            escaped.Append("\\\"");
                            break;
                        case '\\':
                            escaped.Append("\\\\");
                            break;
                        case '\b':
                            escaped.Append("\\b");
                            break;
                        case '\f':
                            escaped.Append("\\f");
                            break;
                        case '\n':
                            escaped.Append("\\n");
                            break;
                        case '\r':
                            escaped.Append("\\r");
                            break;
                        case '\t':
                            escaped.Append("\\t");
                            break;
                        default:
                            if (c < ' ')
                            {
                                escaped.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            }
                            else
                            {
                                escaped.Append(c);
                            }

                            break;
                    }
                }

                return escaped.ToString();
            }

            private void SerializeValue(object value)
            {
                switch (value)
                {
                    case null:
                        builder.Append("null");
                        break;
                    case string stringValue:
                        SerializeString(stringValue);
                        break;
                    case char charValue:
                        SerializeString(charValue.ToString());
                        break;
                    case bool boolValue:
                        builder.Append(boolValue ? "true" : "false");
                        break;
                    case IDictionary dictionary:
                        SerializeObject(dictionary);
                        break;
                    case IEnumerable enumerable:
                        SerializeArray(enumerable);
                        break;
                    default:
                        SerializeNumber(value);
                        break;
                }
            }

            private void SerializeObject(IDictionary obj)
            {
                bool first = true;
                builder.Append('{');

                foreach (DictionaryEntry entry in obj)
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    SerializeString(Convert.ToString(entry.Key, CultureInfo.InvariantCulture));
                    builder.Append(':');
                    SerializeValue(entry.Value);
                    first = false;
                }

                builder.Append('}');
            }

            private void SerializeArray(IEnumerable array)
            {
                bool first = true;
                builder.Append('[');

                foreach (object item in array)
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    SerializeValue(item);
                    first = false;
                }

                builder.Append(']');
            }

            private void SerializeString(string value)
            {
                builder.Append('"').Append(EscapeString(value)).Append('"');
            }

            private void SerializeNumber(object value)
            {
                if (value is byte || value is sbyte || value is short || value is ushort ||
                    value is int || value is uint || value is long || value is ulong)
                {
                    builder.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    return;
                }

                if (value is float)
                {
                    builder.Append(((float)value).ToString("R", CultureInfo.InvariantCulture));
                    return;
                }

                if (value is double)
                {
                    builder.Append(((double)value).ToString("R", CultureInfo.InvariantCulture));
                    return;
                }

                if (value is decimal)
                {
                    builder.Append(((decimal)value).ToString(CultureInfo.InvariantCulture));
                    return;
                }

                SerializeString(Convert.ToString(value, CultureInfo.InvariantCulture));
            }
        }
    }
}
