using MosWord2019.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace MosWord2019.Projects
{
    public sealed class ProjectValidator
    {
        private static readonly Regex ProjectIdPattern =
            new Regex(@"^Word2019_P\d{2,}$", RegexOptions.CultureInvariant);
        private static readonly HashSet<string> SupportedStarterExtensions =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".docx", ".docm" };

        public ValidationResult Validate(string projectFolderPath, string requestedLanguageCode = "en")
        {
            var result = new ValidationResult();
            string folderName = GetFolderName(projectFolderPath);
            if (string.IsNullOrWhiteSpace(projectFolderPath) || !Directory.Exists(projectFolderPath))
            {
                AddError(result, "PROJECT_FOLDER_INVALID", folderName, "", "Project folder does not exist.");
                return result;
            }

            string metaPath = Path.Combine(projectFolderPath, "meta.json");
            string tasksPath = Path.Combine(projectFolderPath, "tasks.json");
            result.Meta = ReadJson<ProjectMeta>(metaPath, result, "META_MISSING", "META_JSON_INVALID", folderName);
            result.Tasks = ReadJson<List<TaskDefinition>>(tasksPath, result, "TASKS_MISSING", "TASKS_JSON_INVALID", folderName);

            string projectId = result.Meta == null ? folderName : (result.Meta.ProjectId ?? "").Trim();
            ValidateMeta(result, projectFolderPath, metaPath, folderName, projectId);
            ValidateTasks(result, projectId);
            LoadLanguages(result, projectFolderPath, projectId, requestedLanguageCode);
            ValidateLanguageKeys(result, projectId);
            return result;
        }

        private static void ValidateMeta(
            ValidationResult result,
            string projectFolderPath,
            string metaPath,
            string folderName,
            string projectId)
        {
            if (result.Meta == null) return;
            result.Meta.ProjectId = projectId;
            if (string.IsNullOrWhiteSpace(projectId))
                AddError(result, "PROJECT_ID_MISSING", folderName, "", "projectId is required.");
            else if (!ProjectIdPattern.IsMatch(projectId))
                AddError(result, "PROJECT_ID_INVALID", projectId, "", "projectId must match Word2019_P followed by at least two digits.");
            else if (!string.Equals(folderName, projectId, StringComparison.Ordinal))
                AddError(result, "PROJECT_FOLDER_MISMATCH", projectId, "", "Project folder name must exactly match projectId.");

            JObject metaJson = ReadObject(metaPath);
            JToken starterToken;
            string starter = result.Meta.Starter == null ? "" : result.Meta.Starter.Trim();
            if (metaJson == null || !TryGetValue(metaJson, "starter", out starterToken) ||
                starterToken.Type != JTokenType.String || string.IsNullOrWhiteSpace((string)starterToken))
            {
                AddError(result, "STARTER_NOT_DECLARED", projectId, "", "starter must be declared in meta.json.");
                return;
            }

            result.Meta.Starter = starter;
            if (!string.Equals(starter, Path.GetFileName(starter), StringComparison.Ordinal) || Path.IsPathRooted(starter))
            {
                AddError(result, "STARTER_PATH_INVALID", projectId, "", "starter must be a file name in the package root.");
                return;
            }
            if (!SupportedStarterExtensions.Contains(Path.GetExtension(starter)))
                AddError(result, "STARTER_EXTENSION_UNSUPPORTED", projectId, "", "starter must use .docx or .docm.");
            if (!File.Exists(Path.Combine(projectFolderPath, starter)))
                AddError(result, "STARTER_MISSING", projectId, "", "Declared starter does not exist: " + starter);
        }

        private static void ValidateTasks(ValidationResult result, string projectId)
        {
            if (result.Tasks == null) return;
            if (result.Tasks.Count == 0)
            {
                AddError(result, "TASKS_EMPTY", projectId, "", "tasks.json must contain at least one task.");
                return;
            }
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < result.Tasks.Count; index++)
            {
                TaskDefinition task = result.Tasks[index];
                if (task == null)
                {
                    AddError(result, "TASK_NULL", projectId, "", "Task at index " + index + " is null.");
                    continue;
                }
                task.ProjectId = projectId;
                task.TaskId = (task.TaskId ?? "").Trim();
                if (string.IsNullOrWhiteSpace(task.TaskId))
                    AddError(result, "TASK_ID_MISSING", projectId, "", "taskId is required.");
                else if (!ids.Add(task.TaskId))
                    AddError(result, "TASK_ID_DUPLICATE", projectId, task.TaskId, "taskId must be unique within the project.");
                Require(result, projectId, task.TaskId, task.TitleKey, "TITLE_KEY_MISSING", "titleKey");
                Require(result, projectId, task.TaskId, task.InstructionKey, "INSTRUCTION_KEY_MISSING", "instructionKey");
                Require(result, projectId, task.TaskId, task.AssertionType, "ASSERTION_TYPE_MISSING", "assertionType");
            }
        }

        private static void LoadLanguages(
            ValidationResult result,
            string projectFolderPath,
            string projectId,
            string requestedLanguageCode)
        {
            string language = NormalizeLanguage(requestedLanguageCode);
            string languageFolder = Path.Combine(projectFolderPath, "lang");
            if (!Directory.Exists(languageFolder))
            {
                AddError(result, "LANG_FOLDER_MISSING", projectId, "", "lang folder is missing.");
                return;
            }

            string[] files;
            try { files = Directory.GetFiles(languageFolder, "*.json", SearchOption.TopDirectoryOnly); }
            catch (Exception ex)
            {
                AddError(result, "LANG_FOLDER_READ_FAILED", projectId, "", "lang folder could not be read: " + ex.Message);
                return;
            }
            foreach (string file in files)
            {
                string code = Path.GetFileNameWithoutExtension(file);
                Dictionary<string, string> dictionary = ReadJson<Dictionary<string, string>>(
                    file, result, "LANG_MISSING", "LANG_JSON_INVALID", projectId);
                if (dictionary != null) result.Languages[code] = dictionary;
            }
            if (!result.Languages.ContainsKey(language))
                AddError(result, "REQUESTED_LANGUAGE_MISSING", projectId, "", language + ".json is required for this load.");
        }

        private static void ValidateLanguageKeys(ValidationResult result, string projectId)
        {
            if (result.Tasks == null) return;
            foreach (KeyValuePair<string, Dictionary<string, string>> language in result.Languages)
            {
                foreach (TaskDefinition task in result.Tasks)
                {
                    if (task == null) continue;
                    ValidateKey(result, projectId, task.TaskId, language.Key, language.Value, task.TitleKey, "TITLE_KEY_NOT_FOUND");
                    ValidateKey(result, projectId, task.TaskId, language.Key, language.Value, task.InstructionKey, "INSTRUCTION_KEY_NOT_FOUND");
                }
            }
        }

        private static T ReadJson<T>(
            string path,
            ValidationResult result,
            string missingCode,
            string invalidCode,
            string projectId) where T : class
        {
            if (!File.Exists(path))
            {
                AddError(result, missingCode, projectId, "", Path.GetFileName(path) + " is missing.");
                return null;
            }
            try
            {
                T value = JsonConvert.DeserializeObject<T>(File.ReadAllText(path));
                if (value == null) AddError(result, invalidCode, projectId, "", Path.GetFileName(path) + " contains null JSON.");
                return value;
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                AddError(result, invalidCode, projectId, "", Path.GetFileName(path) + " is invalid: " + ex.Message);
                return null;
            }
        }

        private static JObject ReadObject(string path)
        {
            try { return File.Exists(path) ? JObject.Parse(File.ReadAllText(path)) : null; }
            catch (Exception) { return null; }
        }

        private static bool TryGetValue(JObject value, string name, out JToken token)
        {
            foreach (JProperty property in value.Properties())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    token = property.Value;
                    return true;
                }
            }
            token = null;
            return false;
        }

        private static void ValidateKey(
            ValidationResult result,
            string projectId,
            string taskId,
            string languageCode,
            Dictionary<string, string> language,
            string key,
            string code)
        {
            if (!string.IsNullOrWhiteSpace(key) && !language.ContainsKey(key))
                AddError(result, code, projectId, taskId, "Language key '" + key + "' is missing from " + languageCode + ".json.");
        }

        private static void Require(
            ValidationResult result,
            string projectId,
            string taskId,
            string value,
            string code,
            string field)
        {
            if (string.IsNullOrWhiteSpace(value)) AddError(result, code, projectId, taskId, field + " is required.");
        }

        private static string NormalizeLanguage(string value)
        {
            string language = (value ?? "").Trim().ToLowerInvariant();
            if (language != "en" && language != "vi")
                throw new ArgumentException("Requested language must be 'en' or 'vi'.", nameof(value));
            return language;
        }

        private static string GetFolderName(string path)
        {
            try { return string.IsNullOrWhiteSpace(path) ? "" : new DirectoryInfo(path).Name; }
            catch (Exception) { return ""; }
        }

        private static void AddError(
            ValidationResult result,
            string code,
            string projectId,
            string taskId,
            string message)
        {
            result.Issues.Add(new ValidationIssue
            {
                Severity = ValidationSeverity.Error,
                Code = code,
                ProjectId = projectId ?? "",
                TaskId = taskId ?? "",
                Message = message ?? ""
            });
        }
    }
}
