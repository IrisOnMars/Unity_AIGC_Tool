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
    public enum AigcAssetType
    {
        Auto,
        Character,
        Creature,
        Prop
    }

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

        private enum SubjectKind
        {
            Character,
            Creature,
            Prop
        }

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
            ExtendPrompt(rawDescription, AigcAssetType.Auto, onSuccess, onError, onStatus);
        }

        public static void ExtendPrompt(
            string rawDescription,
            AigcAssetType assetType,
            Action<string> onSuccess,
            Action<string> onError,
            Action<string> onStatus = null)
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

            new PromptExtensionSession(rawDescription.Trim(), assetType, onSuccess, onError, onStatus).Start();
        }

        private sealed class PromptExtensionSession
        {
            private readonly string rawDescription;
            private readonly AigcAssetType assetType;
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

            public PromptExtensionSession(
                string rawDescription,
                AigcAssetType assetType,
                Action<string> onSuccess,
                Action<string> onError,
                Action<string> onStatus)
            {
                this.rawDescription = rawDescription;
                this.assetType = assetType;
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
                            RequestExpandedPrompt(translatedSubject, assetType, FinishSuccess, Fail);
                        },
                        Fail);
                    return;
                }

                onStatus?.Invoke("Extending prompt...");
                RequestExpandedPrompt(rawDescription, assetType, FinishSuccess, Fail);
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

        private static void RequestExpandedPrompt(
            string subjectDescription,
            AigcAssetType assetType,
            Action<string> onSuccess,
            Action<string> onError)
        {
            string requestJson;
            try
            {
                requestJson = BuildRequestJson(subjectDescription, assetType);
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
                        extendedPrompt = ApplySubjectConstraints(subjectDescription, assetType, ParsePromptContent(responseText));
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
                            "Translate Chinese game asset descriptions to concise English noun phrases. " +
                            "The asset may be a character, creature, weapon, prop, building, or vehicle. Preserve the exact subject type, count, materials, role, abilities, and visual traits. " +
                            "In fantasy game context, translate jingling as elf, not spirit, unless the source explicitly means soul or ghost. " +
                            "Examples: yi ge baoxiang -> one treasure chest; kongzhi bingxue de jingling -> elf who controls ice and snow; yaojing gongjianshou -> fairy archer. Return only the English translation."
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

        private static string BuildRequestJson(string rawDescription, AigcAssetType assetType)
        {
            SubjectKind subjectKind = ResolveSubjectKind(rawDescription, assetType);
            string kindName = subjectKind == SubjectKind.Character
                ? "character"
                : subjectKind == SubjectKind.Creature
                    ? "creature"
                    : "prop or object";
            string composition = BuildCompositionInstruction(subjectKind);
            string exclusions = subjectKind == SubjectKind.Prop
                ? "Do not add a person, humanoid, character, creature, face, hands, body, clothing, pose, or unrelated object."
                : subjectKind == SubjectKind.Creature
                    ? "Do not turn the creature into a human or add a rider, handler, second creature, or unrelated prop."
                    : "Do not replace the character with a different species, profession, class, or unrelated prop.";
            string subjectRules;
            if (subjectKind == SubjectKind.Character)
            {
                subjectRules =
                    "Preserve every species, profession, item of equipment, material, and weapon literally. " +
                    "When the subject is an archer, show exactly one longbow held at the side with a visible grip, attached bowstring, visible quiver, and empty free hand. " +
                    "Do not add a different weapon, costume archetype, or magical effect unless explicitly requested.";
            }
            else if (subjectKind == SubjectKind.Creature)
            {
                subjectRules =
                    "Preserve the exact species, anatomy, count, colors, materials, and explicitly requested abilities. " +
                    "Do not add equipment, clothing, a rider, or magical effects unless explicitly requested.";
            }
            else
            {
                subjectRules =
                    "Preserve the exact object type, count, construction, materials, colors, functional parts, and condition. " +
                    "Do not anthropomorphize the object and do not add unrelated equipment or magical effects unless explicitly requested.";
            }

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
                            "Return one English comma-separated positive prompt for exactly one " + kindName + " isolated on a pure white empty background. Never output a different subject type. " +
                            "Never invent a different class, weapon, elemental power, spell, costume archetype, character, creature, or prop. " +
                            "Do not include explanations, markdown, JSON, labels, or negative prompts."
                    },
                    new Dictionary<string, object>
                    {
                        ["role"] = "user",
                        ["content"] =
                            "ENGLISH_SUBJECT: " + rawDescription +
                            "\nASSET_TYPE: " + kindName +
                            "\nCreate a ComfyUI positive prompt for this exact game asset." +
                            "\nThe first words of the answer must be ENGLISH_SUBJECT exactly or a lightly polished version of it." +
                            "\n" + exclusions +
                            "\n" + subjectRules +
                            "\nComposition: " + composition +
                            "\nOnly include visual effects that ENGLISH_SUBJECT explicitly requests, and keep them local to the subject." +
                            "\nInclude a clear silhouette, pure white canvas, flat shadowless catalog lighting, detailed materials, texture quality, and render quality."
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
            content = StripKnownPrefix(content, "ENGLISH_SUBJECT ");
            return CompactCommaSeparatedPrompt(content.Replace("\r", " ").Replace("\n", " ").Trim());
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

        private static string EnsureAssetDefaults(string content, SubjectKind subjectKind)
        {
            string result = content ?? string.Empty;
            string lower = result.ToLowerInvariant();
            string[] defaults;
            if (subjectKind == SubjectKind.Character)
            {
                defaults = new[]
                {
                    "single full-body game character concept art",
                    "one character only",
                    "single centered full-body figure",
                    "visible face",
                    "complete body visible from head to feet",
                    "isolated product cutout",
                    "pure white empty background",
                    "flat studio lighting"
                };
            }
            else if (subjectKind == SubjectKind.Creature)
            {
                defaults = new[]
                {
                    "single game creature concept art",
                    "one creature only",
                    "single centered creature",
                    "complete creature fully visible",
                    "clear creature silhouette",
                    "isolated product cutout",
                    "pure white empty background",
                    "flat studio lighting"
                };
            }
            else
            {
                defaults = new[]
                {
                    "single game prop concept art",
                    "exactly one object only",
                    "single centered object",
                    "complete object fully visible",
                    "three-quarter front view",
                    "clear object silhouette",
                    "isolated product cutout",
                    "pure white empty background",
                    "flat studio lighting",
                    "detailed material definition"
                };
            }

            foreach (string item in defaults)
            {
                AppendPromptToken(ref result, ref lower, item);
            }

            if (subjectKind == SubjectKind.Character && (lower.Contains("elf") || lower.Contains("fairy")))
            {
                AppendPromptToken(ref result, ref lower, "elegant humanoid elf face");
                AppendPromptToken(ref result, ref lower, "pointed ears");
                AppendPromptToken(ref result, ref lower, "slender fantasy silhouette");
            }

            if (subjectKind != SubjectKind.Prop && (lower.Contains("ice") || lower.Contains("snow") || lower.Contains("frost")))
            {
                AppendPromptToken(ref result, ref lower, "ice crystal ornaments");
                AppendPromptToken(ref result, ref lower, "snowflake motifs");
                AppendPromptToken(ref result, ref lower, "blue and silver color palette");
                AppendPromptToken(ref result, ref lower, "small frost magic effects around the hands");
                AppendPromptToken(ref result, ref lower, "translucent icy fabric details");
            }

            return result.Length > 1200 ? result.Substring(0, 1200).Trim().TrimEnd(',') : result;
        }

        private static string ApplySubjectConstraints(string subjectDescription, AigcAssetType assetType, string content)
        {
            string subject = (subjectDescription ?? string.Empty).Trim();
            string subjectLower = subject.ToLowerInvariant();
            SubjectKind subjectKind = ResolveSubjectKind(subject, assetType);
            bool isArcher = subjectKind == SubjectKind.Character && ContainsAny(subjectLower, "archer", "bowman", "bow user", "bow-wielding");
            bool isElf = subjectKind == SubjectKind.Character && ContainsAny(subjectLower, "elf", "elven");
            bool allowsMagic = ContainsAny(
                subjectLower,
                "magic", "mage", "wizard", "sorcer", "witch", "spell", "elemental",
                "fire", "flame", "ice", "snow", "frost", "lightning", "thunder",
                "wind", "water", "earth", "shadow", "holy", "necrom");
            bool requestsLooseGarment = ContainsAny(subjectLower, "robe", "cloak", "cape", "gown");

            string filtered = FilterPromptSegments(content, subjectKind, isArcher, allowsMagic, requestsLooseGarment);
            string result = subject;
            string lower = result.ToLowerInvariant();

            if (isElf)
            {
                AppendPromptToken(ref result, ref lower, "clearly non-human elf identity");
                AppendPromptToken(ref result, ref lower, "two long pointed elf ears extending sideways and fully visible");
            }

            if (isArcher)
            {
                AppendPromptToken(ref result, ref lower, "unmistakable archer identity");
                AppendPromptToken(ref result, ref lower, "exactly one clearly visible longbow held vertically at the side");
                AppendPromptToken(ref result, ref lower, "clear hand grip on the single longbow and its bowstring visibly attached to the same bow");
                AppendPromptToken(ref result, ref lower, "quiver filled with arrows clearly visible on the back");
                AppendPromptToken(ref result, ref lower, "free hand relaxed and empty");
                AppendPromptToken(ref result, ref lower, "practical fitted fantasy archer clothing");
            }

            if (subjectKind == SubjectKind.Character)
            {
                AppendPromptToken(ref result, ref lower, "one isolated character only, single centered subject");
                AppendPromptToken(ref result, ref lower, "detailed facial features with visible eyes, nose, and mouth");
                AppendPromptToken(ref result, ref lower, "generous white margin around the complete full body");
            }
            else if (subjectKind == SubjectKind.Creature)
            {
                AppendPromptToken(ref result, ref lower, "one isolated creature only, single centered subject");
                AppendPromptToken(ref result, ref lower, "complete creature anatomy fully visible");
                AppendPromptToken(ref result, ref lower, "generous white margin around the complete creature");
            }
            else
            {
                AppendPromptToken(ref result, ref lower, "exactly one isolated object only, single centered object");
                AppendPromptToken(ref result, ref lower, "complete object fully visible in a three-quarter front view");
                AppendPromptToken(ref result, ref lower, "clear object silhouette and detailed material surfaces");
                AppendPromptToken(ref result, ref lower, "generous white margin around the entire object");
            }

            AppendPromptToken(ref result, ref lower, "seamless pure white background");
            AppendPromptToken(ref result, ref lower, "flat uniform catalog lighting");

            if (!string.IsNullOrWhiteSpace(filtered))
            {
                result += ", " + filtered;
            }

            result = EnsureAssetDefaults(CompactCommaSeparatedPrompt(result), subjectKind);
            return result.Length > 1200 ? result.Substring(0, 1200).Trim().TrimEnd(',') : result;
        }

        private static string FilterPromptSegments(
            string content,
            SubjectKind subjectKind,
            bool isArcher,
            bool allowsMagic,
            bool requestsLooseGarment)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            List<string> kept = new List<string>();
            foreach (string rawPart in content.Split(','))
            {
                string part = rawPart.Trim();
                string lower = part.ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(part))
                {
                    continue;
                }

                if (lower.StartsWith("no ", StringComparison.Ordinal) ||
                    lower.StartsWith("without ", StringComparison.Ordinal))
                {
                    continue;
                }

                if (ContainsAny(
                    lower,
                    "cinematic lighting",
                    "soft shadow",
                    "smooth gradient",
                    "backdrop gradient",
                    "spotlight",
                    "pedestal",
                    "platform",
                    "environment",
                    "scenery",
                    "character reference sheet",
                    "reference sheet",
                    "turnaround sheet",
                    "character sheet",
                    "inset figure",
                    "shadow figure",
                    "background silhouette",
                    "duplicate character",
                    "multiple characters"))
                {
                    continue;
                }

                if (subjectKind == SubjectKind.Prop && ContainsAny(
                    lower,
                    "character",
                    "humanoid",
                    "person",
                    "visible face",
                    "facial feature",
                    "eyes, nose",
                    "full body",
                    "full-body",
                    "figure",
                    "profession-specific",
                    "wearing",
                    "holding pose"))
                {
                    continue;
                }

                if (subjectKind == SubjectKind.Creature && ContainsAny(
                    lower,
                    "humanoid character",
                    "human character",
                    "person",
                    "profession-specific equipment"))
                {
                    continue;
                }

                if (!allowsMagic && ContainsAny(lower, "elemental", "magic", "magical", "spell", "rune", "aura", "ethereal", "fireball", "flame", "glowing hand", "emanating from the fingers", "magic particles"))
                {
                    continue;
                }

                if (isArcher && ContainsAny(lower, "sword", "blade", "staff", "wand", "spellbook", "scepter", "mace", "axe", "spear"))
                {
                    continue;
                }

                if (isArcher && !requestsLooseGarment && ContainsAny(lower, "flowing robe", "long robe", "cloak", "cape", "gown"))
                {
                    continue;
                }

                kept.Add(part);
            }

            return string.Join(", ", kept);
        }

        private static SubjectKind ResolveSubjectKind(string subject, AigcAssetType assetType)
        {
            if (assetType == AigcAssetType.Character)
            {
                return SubjectKind.Character;
            }
            if (assetType == AigcAssetType.Creature)
            {
                return SubjectKind.Creature;
            }
            if (assetType == AigcAssetType.Prop)
            {
                return SubjectKind.Prop;
            }

            string lower = (subject ?? string.Empty).ToLowerInvariant();
            if (ContainsAny(
                lower,
                "creature", "monster", "dragon", "beast", "animal", "wolf", "tiger", "lion", "horse",
                "bird", "serpent", "spider", "dinosaur", "\u602a\u7269", "\u9f99", "\u91ce\u517d", "\u52a8\u7269"))
            {
                return SubjectKind.Creature;
            }

            if (ContainsAny(
                lower,
                "character", "person", "human", "humanoid", "woman", "female", "girl", "man", "male", "boy",
                "elf", "fairy", "archer", "bowman", "warrior", "knight", "mage", "wizard", "sorcer", "witch",
                "paladin", "rogue", "ranger", "druid", "priest", "soldier", "hunter", "assassin", "robot", "android",
                "\u89d2\u8272", "\u4eba\u7269", "\u5f13\u7bad\u624b", "\u6218\u58eb", "\u6cd5\u5e08", "\u9a91\u58eb"))
            {
                return SubjectKind.Character;
            }

            if (ContainsAny(
                lower,
                "chest", "treasure box", "crate", "barrel", "helmet", "armor", "shield", "sword", "dagger",
                "weapon", "staff", "wand", "potion", "bottle", "book", "chair", "table", "door", "key",
                "ring", "amulet", "vehicle", "ship", "building", "house", "tower", "statue", "\u5b9d\u7bb1", "\u9053\u5177", "\u6b66\u5668"))
            {
                return SubjectKind.Prop;
            }

            return SubjectKind.Prop;
        }

        private static string BuildCompositionInstruction(SubjectKind subjectKind)
        {
            if (subjectKind == SubjectKind.Character)
            {
                return "one solo character only, centered complete full-body figure, front or three-quarter view, visible face, isolated product cutout on a pure white empty background.";
            }
            if (subjectKind == SubjectKind.Creature)
            {
                return "one creature only, centered complete anatomy, front or three-quarter view, isolated product cutout on a pure white empty background.";
            }

            return "exactly one standalone object only, complete object fully visible, centered three-quarter front view, isolated product cutout on a pure white empty background.";
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
