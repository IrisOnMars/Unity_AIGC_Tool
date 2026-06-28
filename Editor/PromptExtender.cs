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
    public static class PromptExtender
    {
        public const string DefaultEndpoint = "http://127.0.0.1:8000/v1/chat/completions";
        public const string DefaultModel = "Qwen3-VL-2B-Instruct";
        public const string DefaultDevice = "cuda";

        private const string EndpointPrefKey = "AIGC.Toolchain.QwenEndpoint";
        private const string ModelPrefKey = "AIGC.Toolchain.QwenModel";
        private const string ApiKeyPrefKey = "AIGC.Toolchain.QwenApiKey";
        private const string AutoStartServerPrefKey = "AIGC.Toolchain.QwenAutoStartServer";
        private const string PythonPathPrefKey = "AIGC.Toolchain.QwenPythonPath";
        private const string ServerScriptPathPrefKey = "AIGC.Toolchain.QwenServerScriptPath";
        private const string DevicePrefKey = "AIGC.Toolchain.QwenDevice";
        private const string StartupTimeoutPrefKey = "AIGC.Toolchain.QwenStartupTimeoutSeconds";
        private const int RequestTimeoutSeconds = 240;
        private const int TranslationTimeoutSeconds = 120;
        private const int ExistingServerProbeTimeoutSeconds = 3;
        private const int FreeComfyMemoryTimeoutSeconds = 8;
        private const double ServerPollIntervalSeconds = 1.0d;
        private const double ProcessStopTimeoutSeconds = 6.0d;
        private const int DefaultStartupTimeoutSeconds = 120;

        public static string Endpoint
        {
            get => EditorPrefs.GetString(EndpointPrefKey, DefaultEndpoint);
            set => EditorPrefs.SetString(EndpointPrefKey, string.IsNullOrWhiteSpace(value) ? DefaultEndpoint : value.Trim());
        }

        public static string Model
        {
            get => EditorPrefs.GetString(ModelPrefKey, DefaultModel);
            set => EditorPrefs.SetString(ModelPrefKey, string.IsNullOrWhiteSpace(value) ? DefaultModel : value.Trim());
        }

        public static string ApiKey
        {
            get => EditorPrefs.GetString(ApiKeyPrefKey, string.Empty);
            set => EditorPrefs.SetString(ApiKeyPrefKey, value ?? string.Empty);
        }

        public static bool AutoStartServer
        {
            get => EditorPrefs.GetBool(AutoStartServerPrefKey, true);
            set => EditorPrefs.SetBool(AutoStartServerPrefKey, value);
        }

        public static string PythonPath
        {
            get => NormalizePythonPath(EditorPrefs.GetString(PythonPathPrefKey, GetDefaultPythonPath()));
            set => EditorPrefs.SetString(PythonPathPrefKey, NormalizePythonPath(string.IsNullOrWhiteSpace(value) ? GetDefaultPythonPath() : value.Trim()));
        }

        public static string ServerScriptPath
        {
            get => EditorPrefs.GetString(ServerScriptPathPrefKey, GetDefaultServerScriptPath());
            set => EditorPrefs.SetString(ServerScriptPathPrefKey, string.IsNullOrWhiteSpace(value) ? GetDefaultServerScriptPath() : value.Trim());
        }

        public static string Device
        {
            get => EditorPrefs.GetString(DevicePrefKey, DefaultDevice);
            set => EditorPrefs.SetString(DevicePrefKey, string.IsNullOrWhiteSpace(value) ? DefaultDevice : value.Trim());
        }

        public static int StartupTimeoutSeconds
        {
            get => Mathf.Max(20, EditorPrefs.GetInt(StartupTimeoutPrefKey, DefaultStartupTimeoutSeconds));
            set => EditorPrefs.SetInt(StartupTimeoutPrefKey, Mathf.Max(20, value));
        }

        public static void ExtendPrompt(string rawDescription, Action<string> onSuccess, Action<string> onError)
        {
            ExtendPrompt(rawDescription, onSuccess, onError, null);
        }

        public static void ExtendPrompt(string rawDescription, Action<string> onSuccess, Action<string> onError, Action<string> onStatus)
        {
            if (onError == null)
            {
                onError = Debug.LogError;
            }

            if (onSuccess == null)
            {
                onError("PromptExtender requires a success callback.");
                return;
            }

            if (string.IsNullOrWhiteSpace(rawDescription))
            {
                onError("Please enter a source description before generating.");
                return;
            }

            new PromptExtensionSession(rawDescription.Trim(), onSuccess, onError, onStatus).Start();
        }

        private sealed class PromptExtensionSession
        {
            private readonly string rawDescription;
            private readonly Action<string> onSuccess;
            private readonly Action<string> onError;
            private readonly Action<string> onStatus;

            private Process ownedServerProcess;
            private string ownedServerOutLogPath;
            private string ownedServerErrLogPath;
            private bool requestInFlight;
            private bool completed;
            private double startupStartedAt;
            private double nextPollAt;

            public PromptExtensionSession(string rawDescription, Action<string> onSuccess, Action<string> onError, Action<string> onStatus)
            {
                this.rawDescription = rawDescription;
                this.onSuccess = onSuccess;
                this.onError = onError;
                this.onStatus = onStatus;
            }

            public void Start()
            {
                onStatus?.Invoke("Checking Qwen service...");

                AigcEditorWebRequest.GetText(
                    GetModelsEndpoint(),
                    ExistingServerProbeTimeoutSeconds,
                    _ =>
                    {
                        onStatus?.Invoke("Using existing Qwen service.");
                        RunPromptPipeline();
                    },
                    error =>
                    {
                        if (!AutoStartServer)
                        {
                            Fail("Qwen service is not running, and auto-start is disabled. " + error);
                            return;
                        }

                        FreeComfyMemoryThen(StartOwnedServer);
                    },
                    BuildHeaders());
            }

            private void FreeComfyMemoryThen(Action continuation)
            {
                onStatus?.Invoke("Freeing ComfyUI VRAM before starting Qwen...");

                AigcEditorWebRequest.PostJson(
                    CombineUrl(ComfyClient.ComfyBaseUrl, "/free"),
                    "{\"unload_models\":true,\"free_memory\":true}",
                    FreeComfyMemoryTimeoutSeconds,
                    _ => continuation(),
                    error =>
                    {
                        Debug.LogWarning("ComfyUI /free request failed before Qwen startup. Continuing anyway. " + error);
                        continuation();
                    });
            }

            private void StartOwnedServer()
            {
                string pythonPath = PythonPath;
                string scriptPath = ServerScriptPath;

                if (string.IsNullOrWhiteSpace(pythonPath) || !File.Exists(pythonPath))
                {
                    Fail("Qwen Python executable was not found: " + pythonPath);
                    return;
                }

                if (string.IsNullOrWhiteSpace(scriptPath) || !File.Exists(scriptPath))
                {
                    Fail("Qwen server script was not found: " + scriptPath);
                    return;
                }

                try
                {
                    ownedServerOutLogPath = BuildQwenLogPath("out");
                    ownedServerErrLogPath = BuildQwenLogPath("err");

                    ProcessStartInfo startInfo = new ProcessStartInfo
                    {
                        FileName = pythonPath,
                        Arguments = QuoteArgument(scriptPath),
                        WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Application.dataPath,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    startInfo.EnvironmentVariables["QWEN_DEVICE"] = Device;
                    startInfo.EnvironmentVariables["QWEN_OUT_LOG"] = ownedServerOutLogPath;
                    startInfo.EnvironmentVariables["QWEN_ERR_LOG"] = ownedServerErrLogPath;
                    startInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                    ownedServerProcess = Process.Start(startInfo);
                }
                catch (Exception ex)
                {
                    Fail("Failed to start local Qwen server: " + ex.Message);
                    return;
                }

                if (ownedServerProcess == null)
                {
                    Fail("Failed to start local Qwen server: process was null.");
                    return;
                }

                startupStartedAt = EditorApplication.timeSinceStartup;
                nextPollAt = startupStartedAt + ServerPollIntervalSeconds;
                onStatus?.Invoke("Starting Qwen on " + Device + "...");
                EditorApplication.update += PollOwnedServerStartup;
            }

            private void PollOwnedServerStartup()
            {
                if (completed)
                {
                    EditorApplication.update -= PollOwnedServerStartup;
                    return;
                }

                if (ownedServerProcess != null && HasProcessExited(ownedServerProcess))
                {
                    EditorApplication.update -= PollOwnedServerStartup;
                    Fail("Qwen server exited before it became ready." + ReadQwenLogSummary(ownedServerOutLogPath, ownedServerErrLogPath));
                    return;
                }

                double now = EditorApplication.timeSinceStartup;
                if (now - startupStartedAt > StartupTimeoutSeconds)
                {
                    EditorApplication.update -= PollOwnedServerStartup;
                    Fail("Qwen server did not become ready within " + StartupTimeoutSeconds + " seconds." + ReadQwenLogSummary(ownedServerOutLogPath, ownedServerErrLogPath));
                    return;
                }

                if (requestInFlight || now < nextPollAt)
                {
                    return;
                }

                requestInFlight = true;
                onStatus?.Invoke("Waiting for Qwen service...");

                AigcEditorWebRequest.GetText(
                    GetModelsEndpoint(),
                    ExistingServerProbeTimeoutSeconds,
                    _ =>
                    {
                        requestInFlight = false;
                        EditorApplication.update -= PollOwnedServerStartup;
                        onStatus?.Invoke("Qwen service is ready.");
                        RunPromptPipeline();
                    },
                    _ =>
                    {
                        requestInFlight = false;
                        nextPollAt = EditorApplication.timeSinceStartup + ServerPollIntervalSeconds;
                    },
                    BuildHeaders());
            }

            private void RunPromptPipeline()
            {
                if (ContainsCjk(rawDescription))
                {
                    onStatus?.Invoke("Translating source subject...");
                    TranslateSubject(
                        rawDescription,
                        translatedSubject =>
                        {
                            onStatus?.Invoke("Extending translated prompt...");
                            RequestExpandedPrompt(translatedSubject, FinishSuccess, Fail);
                        },
                        Fail);
                    return;
                }

                onStatus?.Invoke("Extending prompt...");
                RequestExpandedPrompt(rawDescription, FinishSuccess, Fail);
            }

            private void FinishSuccess(string prompt)
            {
                StopOwnedServerThen(() => onSuccess(prompt));
            }

            private void Fail(string message)
            {
                StopOwnedServerThen(() => onError(message));
            }

            private void StopOwnedServerThen(Action continuation)
            {
                completed = true;
                EditorApplication.update -= PollOwnedServerStartup;

                if (ownedServerProcess == null)
                {
                    continuation();
                    return;
                }

                try
                {
                    if (!HasProcessExited(ownedServerProcess))
                    {
                        onStatus?.Invoke("Stopping Qwen and releasing VRAM...");
                        ownedServerProcess.Kill();
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogWarning("Failed to stop Qwen server process: " + ex.Message);
                    continuation();
                    return;
                }

                double stopStartedAt = EditorApplication.timeSinceStartup;
                EditorApplication.CallbackFunction waitForExit = null;
                waitForExit = () =>
                {
                    bool exited = HasProcessExited(ownedServerProcess);
                    bool timedOut = EditorApplication.timeSinceStartup - stopStartedAt >= ProcessStopTimeoutSeconds;
                    if (!exited && !timedOut)
                    {
                        return;
                    }

                    EditorApplication.update -= waitForExit;
                    continuation();
                };

                EditorApplication.update += waitForExit;
            }
        }

        private static void TranslateSubject(string rawDescription, Action<string> onSuccess, Action<string> onError)
        {
            string requestJson;
            try
            {
                requestJson = BuildTranslationRequestJson(rawDescription);
            }
            catch (Exception ex)
            {
                onError("Failed to build Qwen translation request: " + ex.Message);
                return;
            }

            AigcEditorWebRequest.PostJson(
                Endpoint,
                requestJson,
                TranslationTimeoutSeconds,
                responseText =>
                {
                    string translatedSubject;
                    try
                    {
                        translatedSubject = CleanSubjectTranslation(ParsePromptContent(responseText), rawDescription);
                    }
                    catch (Exception ex)
                    {
                        onError(ex.Message);
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(translatedSubject))
                    {
                        onError("Qwen returned an empty subject translation.");
                        return;
                    }

                    onSuccess(translatedSubject);
                },
                error => onError("Qwen subject translation request failed. " + error),
                BuildHeaders());
        }

        private static void RequestExpandedPrompt(string subjectDescription, Action<string> onSuccess, Action<string> onError)
        {
            string requestJson;
            try
            {
                requestJson = BuildRequestJson(subjectDescription);
            }
            catch (Exception ex)
            {
                onError($"Failed to build Qwen request: {ex.Message}");
                return;
            }

            AigcEditorWebRequest.PostJson(
                Endpoint,
                requestJson,
                RequestTimeoutSeconds,
                responseText =>
                {
                    string extendedPrompt;
                    try
                    {
                        extendedPrompt = ParsePromptContent(responseText);
                    }
                    catch (Exception ex)
                    {
                        onError(ex.Message);
                        return;
                    }

                    onSuccess(extendedPrompt);
                },
                error => onError($"Qwen prompt extension request failed. {error}"),
                BuildHeaders());
        }

        private static string BuildTranslationRequestJson(string rawDescription)
        {
            Dictionary<string, object> requestBody = new Dictionary<string, object>
            {
                ["model"] = Model,
                ["stream"] = false,
                ["temperature"] = 0.0d,
                ["max_tokens"] = 48L,
                ["messages"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] =
                            "Translate Chinese fantasy game character descriptions to concise English noun phrases. " +
                            "Preserve species, role, abilities, elements, weapons, and visual traits. In fantasy game context, translate jingling as elf, not spirit, unless the source explicitly means soul or ghost. " +
                            "Examples: kongzhi bingxue de jingling -> elf who controls ice and snow; yaojing gongjianshou -> fairy archer. Return only the English translation."
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] = rawDescription
                    }
                }
            };

            return MiniJson.Serialize(requestBody);
        }

        private static string BuildRequestJson(string rawDescription)
        {
            Dictionary<string, object> requestBody = new Dictionary<string, object>
            {
                ["model"] = Model,
                ["stream"] = false,
                ["temperature"] = 0.0d,
                ["max_tokens"] = 180L,
                ["messages"] = new List<object>
                {
                    new Dictionary<string, object>
                    {
                        ["role"] = "system",
                        ["content"] =
                            "You are a strict game prompt editor. Your job is enrichment, not subject replacement. " +
                            "The final answer must start with the given English subject noun phrase. " +
                            "Return one English comma-separated positive prompt for a single full-body game character concept image isolated on a pure white empty background. Never output a different subject. " +
                            "Do not include explanations, markdown, JSON, labels, or negative prompts."
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] =
                            "ENGLISH_SUBJECT: " + rawDescription +
                            "\nCreate a ComfyUI positive prompt for this exact subject as one solo game character design image." +
                            "\nThe first words of the answer must be ENGLISH_SUBJECT exactly or a lightly polished version of it." +
                            "\nDo not output warrior, dragon, robot, monster, vehicle, or landscape unless present in ENGLISH_SUBJECT." +
                            "\nComposition: one solo character only, single subject, centered full-body figure, front view or three-quarter view, isolated product cutout on pure white empty background." +
                            "\nElemental powers must appear only as small local effects attached to the character's hands, hair, costume, accessories, or held props." +
                            "\nInclude: visible face, clear silhouette, costume and equipment details, pure white canvas, even studio lighting, texture quality, and render quality."
                    }
                }
            };

            return MiniJson.Serialize(requestBody);
        }

        private static Dictionary<string, string> BuildHeaders()
        {
            string apiKey = ApiKey;
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return null;
            }

            apiKey = apiKey.Trim();
            string authorization = apiKey.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? apiKey : "Bearer " + apiKey;
            return new Dictionary<string, string>
            {
                ["Authorization"] = authorization
            };
        }

        private static string ParsePromptContent(string responseJson)
        {
            object parsed;
            try
            {
                parsed = MiniJson.Deserialize(responseJson);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Qwen returned invalid JSON: {ex.Message}");
            }

            Dictionary<string, object> root = parsed as Dictionary<string, object>;
            if (root == null)
            {
                throw new InvalidOperationException("Qwen response is not a JSON object.");
            }

            if (TryExtractError(root, out string apiError))
            {
                throw new InvalidOperationException("Qwen returned an error: " + apiError);
            }

            object choicesObject;
            List<object> choices;
            if (!root.TryGetValue("choices", out choicesObject) || (choices = choicesObject as List<object>) == null || choices.Count == 0)
            {
                throw new InvalidOperationException("Qwen response did not contain choices[0].message.content.");
            }

            Dictionary<string, object> firstChoice = choices[0] as Dictionary<string, object>;
            if (firstChoice == null)
            {
                throw new InvalidOperationException("Qwen response choices[0] is not a JSON object.");
            }

            string content = null;

            if (firstChoice.TryGetValue("message", out object messageObject) && messageObject is Dictionary<string, object> message)
            {
                if (message.TryGetValue("content", out object messageContent))
                {
                    content = ConvertJsonValueToString(messageContent);
                }
            }

            if (string.IsNullOrWhiteSpace(content) && firstChoice.TryGetValue("text", out object textContent))
            {
                content = ConvertJsonValueToString(textContent);
            }

            content = CleanModelOutput(content);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidOperationException("Qwen returned an empty prompt.");
            }

            return content;
        }

        private static bool TryExtractError(Dictionary<string, object> root, out string error)
        {
            error = null;

            if (!root.TryGetValue("error", out object errorObject))
            {
                return false;
            }

            if (errorObject is Dictionary<string, object> errorDict)
            {
                if (errorDict.TryGetValue("message", out object message))
                {
                    error = ConvertJsonValueToString(message);
                }
                else
                {
                    error = MiniJson.Serialize(errorDict);
                }
            }
            else
            {
                error = ConvertJsonValueToString(errorObject);
            }

            return !string.IsNullOrWhiteSpace(error);
        }

        private static string CleanModelOutput(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            content = content.Trim();

            int thinkEnd = content.LastIndexOf("</think>", StringComparison.OrdinalIgnoreCase);
            if (thinkEnd >= 0)
            {
                content = content.Substring(thinkEnd + "</think>".Length).Trim();
            }

            if (content.StartsWith("```", StringComparison.Ordinal))
            {
                int firstLineBreak = content.IndexOf('\n');
                int closingFence = content.LastIndexOf("```", StringComparison.Ordinal);
                if (firstLineBreak >= 0 && closingFence > firstLineBreak)
                {
                    content = content.Substring(firstLineBreak + 1, closingFence - firstLineBreak - 1).Trim();
                }
            }

            content = StripKnownPrefix(content, "Prompt:");
            content = StripKnownPrefix(content, "Final prompt:");
            content = StripKnownPrefix(content, "Positive prompt:");
            content = StripKnownPrefix(content, "ENGLISH_SUBJECT:");
            content = StripKnownPrefix(content, "Subject:");

            if (content.Length >= 2 && content[0] == '"' && content[content.Length - 1] == '"')
            {
                content = content.Substring(1, content.Length - 2).Trim();
            }

            content = RemoveInlineSubjectLabel(content);
            return EnsureSingleCharacterDefaults(CompactCommaSeparatedPrompt(content.Replace("\r", " ").Replace("\n", " ").Trim()));
        }

        private static string RemoveInlineSubjectLabel(string content)
        {
            const string marker = " subject:";
            int index = content.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            int firstComma = content.IndexOf(',');

            if (index > 0 && (firstComma < 0 || index < firstComma))
            {
                return content.Substring(0, index).Trim() + ", " + content.Substring(index + marker.Length).Trim();
            }

            return content;
        }

        private static string EnsureSingleCharacterDefaults(string content)
        {
            string result = content ?? string.Empty;
            string lower = result.ToLowerInvariant();
            string[] defaults =
            {
                "single full-body game character concept art",
                "one character only",
                "solo",
                "single subject",
                "centered composition",
                "one centered full-body figure",
                "visible face",
                "isolated product cutout",
                "pure white empty background",
                "white canvas",
                "studio lighting"
            };

            foreach (string item in defaults)
            {
                AppendPromptToken(ref result, ref lower, item);
            }

            if (lower.Contains("elf") || lower.Contains("fairy"))
            {
                AppendPromptToken(ref result, ref lower, "elegant humanoid elf face");
                AppendPromptToken(ref result, ref lower, "pointed ears");
                AppendPromptToken(ref result, ref lower, "slender fantasy silhouette");
            }

            if (lower.Contains("ice") || lower.Contains("snow") || lower.Contains("frost"))
            {
                AppendPromptToken(ref result, ref lower, "ice crystal ornaments");
                AppendPromptToken(ref result, ref lower, "snowflake motifs");
                AppendPromptToken(ref result, ref lower, "blue and silver color palette");
                AppendPromptToken(ref result, ref lower, "small frost magic effects around the hands");
                AppendPromptToken(ref result, ref lower, "translucent icy fabric details");
            }

            return result.Length > 1200 ? result.Substring(0, 1200).Trim().TrimEnd(',') : result;
        }

        private static void AppendPromptToken(ref string result, ref string lower, string item)
        {
            if (lower.Contains(item))
            {
                return;
            }

            result = string.IsNullOrWhiteSpace(result) ? item : result + ", " + item;
            lower = result.ToLowerInvariant();
        }

        private static string CleanSubjectTranslation(string content, string rawDescription)
        {
            content = CleanModelOutput(content);

            int commaIndex = content.IndexOf(',');
            if (commaIndex > 0)
            {
                content = content.Substring(0, commaIndex).Trim();
            }

            content = StripKnownPrefix(content, "Translation:");
            content = StripKnownPrefix(content, "English:");

            if (content.Length >= 2 && content[0] == '"' && content[content.Length - 1] == '"')
            {
                content = content.Substring(1, content.Length - 2).Trim();
            }

            return ApplyChineseFantasyTranslationHints(content, rawDescription);
        }

        private static string ApplyChineseFantasyTranslationHints(string translation, string rawDescription)
        {
            if (string.IsNullOrWhiteSpace(translation) || string.IsNullOrWhiteSpace(rawDescription))
            {
                return translation;
            }

            bool hasJingling = rawDescription.Contains("\u7cbe\u7075");
            bool hasIce = rawDescription.IndexOf('\u51b0') >= 0;
            bool hasSnow = rawDescription.IndexOf('\u96ea') >= 0;
            string lower = translation.ToLowerInvariant();

            if (hasJingling && !lower.Contains("elf") && !lower.Contains("fairy"))
            {
                if (lower.Contains("spirit"))
                {
                    translation = ReplaceIgnoreCase(translation, "spirit", "elf");
                }
                else
                {
                    translation += " elf";
                }

                lower = translation.ToLowerInvariant();
            }

            if (hasJingling && (hasIce || hasSnow) && !lower.Contains("ice") && !lower.Contains("snow"))
            {
                translation += " who controls ice and snow";
            }

            return translation.Trim();
        }

        private static string ReplaceIgnoreCase(string source, string oldValue, string newValue)
        {
            int index = source.IndexOf(oldValue, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                return source;
            }

            return source.Substring(0, index) + newValue + source.Substring(index + oldValue.Length);
        }

        private static bool ContainsCjk(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            foreach (char c in value)
            {
                if ((c >= '\u4e00' && c <= '\u9fff') ||
                    (c >= '\u3400' && c <= '\u4dbf') ||
                    (c >= '\uf900' && c <= '\ufaff'))
                {
                    return true;
                }
            }

            return false;
        }

        private static string CompactCommaSeparatedPrompt(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            string[] parts = content.Split(',');
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> compact = new List<string>();

            foreach (string part in parts)
            {
                string item = part.Trim();
                if (string.IsNullOrWhiteSpace(item))
                {
                    continue;
                }

                string key = item.ToLowerInvariant();
                if (!seen.Add(key))
                {
                    continue;
                }

                compact.Add(item);
                if (compact.Count >= 36)
                {
                    break;
                }
            }

            string result = string.Join(", ", compact.ToArray());
            return result.Length > 1200 ? result.Substring(0, 1200).Trim().TrimEnd(',') : result;
        }

        private static string StripKnownPrefix(string value, string prefix)
        {
            return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? value.Substring(prefix.Length).Trim()
                : value;
        }

        private static string GetModelsEndpoint()
        {
            string endpoint = string.IsNullOrWhiteSpace(Endpoint) ? DefaultEndpoint : Endpoint.Trim();
            const string chatSuffix = "/v1/chat/completions";
            int suffixIndex = endpoint.IndexOf(chatSuffix, StringComparison.OrdinalIgnoreCase);

            if (suffixIndex >= 0)
            {
                return endpoint.Substring(0, suffixIndex) + "/v1/models";
            }

            if (endpoint.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            {
                return endpoint.Substring(0, endpoint.Length - "/chat/completions".Length) + "/models";
            }

            return endpoint.TrimEnd('/') + "/models";
        }

        private static string CombineUrl(string baseUrl, string path)
        {
            baseUrl = string.IsNullOrWhiteSpace(baseUrl) ? string.Empty : baseUrl.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(baseUrl))
            {
                return path;
            }

            return path.StartsWith("/", StringComparison.Ordinal) ? baseUrl + path : baseUrl + "/" + path;
        }

        private static string GetDefaultPythonPath()
        {
            const string condaPython = @"D:\anaconda3\envs\qwen3\python.exe";
            const string condaPythonW = @"D:\anaconda3\envs\qwen3\pythonw.exe";

            if (File.Exists(condaPython))
            {
                return condaPython;
            }

            if (File.Exists(condaPythonW))
            {
                return condaPythonW;
            }

            return "python";
        }

        private static string NormalizePythonPath(string pythonPath)
        {
            if (string.IsNullOrWhiteSpace(pythonPath))
            {
                return GetDefaultPythonPath();
            }

            pythonPath = pythonPath.Trim();
            if (string.Equals(Path.GetFileName(pythonPath), "pythonw.exe", StringComparison.OrdinalIgnoreCase))
            {
                string pythonExe = Path.Combine(Path.GetDirectoryName(pythonPath) ?? string.Empty, "python.exe");
                if (File.Exists(pythonExe))
                {
                    return pythonExe;
                }
            }

            return pythonPath;
        }

        private static string GetDefaultServerScriptPath()
        {
            string projectRoot = Directory.GetParent(Application.dataPath)?.FullName;
            if (!string.IsNullOrEmpty(projectRoot))
            {
                return Path.Combine(
                    projectRoot,
                    "Packages",
                    "com.aigc.toolchain",
                    "Tools",
                    "qwen_openai_server.py");
            }

            return Path.Combine("Packages", "com.aigc.toolchain", "Tools", "qwen_openai_server.py");
        }

        private static string QuoteArgument(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\\\"") + "\"";
        }

        private static bool HasProcessExited(Process process)
        {
            if (process == null)
            {
                return true;
            }

            try
            {
                return process.HasExited;
            }
            catch
            {
                return true;
            }
        }

        private static string BuildQwenLogPath(string kind)
        {
            return Path.Combine(Path.GetTempPath(), "qwen_openai_server_" + Guid.NewGuid().ToString("N") + "." + kind + ".log");
        }

        private static string GetQwenDefaultOutLogPath()
        {
            return Path.Combine(Path.GetTempPath(), "qwen_openai_server.out.log");
        }

        private static string GetQwenDefaultErrLogPath()
        {
            return Path.Combine(Path.GetTempPath(), "qwen_openai_server.err.log");
        }

        private static string ReadQwenLogSummary(string stdoutPath, string stderrPath)
        {
            if (string.IsNullOrWhiteSpace(stdoutPath))
            {
                stdoutPath = GetQwenDefaultOutLogPath();
            }

            if (string.IsNullOrWhiteSpace(stderrPath))
            {
                stderrPath = GetQwenDefaultErrLogPath();
            }

            string stdout = ReadLogTail(stdoutPath, 1200);
            string stderr = ReadLogTail(stderrPath, 1600);
            string summary = string.Empty;

            if (!string.IsNullOrWhiteSpace(stdout))
            {
                summary += "\nQwen stdout:\n" + stdout;
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                summary += "\nQwen stderr:\n" + stderr;
            }

            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = "\nNo Qwen log output was available.";
            }

            return summary;
        }

        private static string ReadLogTail(string path, int maxChars)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return string.Empty;
                }

                string text;
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    text = reader.ReadToEnd();
                }

                if (text.Length <= maxChars)
                {
                    return text.Trim();
                }

                return text.Substring(text.Length - maxChars).Trim();
            }
            catch (Exception ex)
            {
                return "Failed to read log " + path + ": " + ex.Message;
            }
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

            return MiniJson.Serialize(value);
        }
    }
}
