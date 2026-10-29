using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MosWord2019.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Projects
{
    public sealed class TestingCatalogLoader
    {
        private static readonly string[] Properties =
        {
            "projectId", "eligible", "conflictGroup", "requiredTaskIds", "taskGroups", "excludedTaskIds", "notes"
        };

        public IReadOnlyList<TestingCatalogEntry> Load(string catalogFilePath, IEnumerable<ProjectPackage> packages)
        {
            if (string.IsNullOrWhiteSpace(catalogFilePath) || !File.Exists(catalogFilePath))
                throw new InvalidDataException("CATALOG_MISSING: Testing catalog file does not exist.");
            JToken token;
            try
            {
                using (var reader = new JsonTextReader(new StringReader(File.ReadAllText(catalogFilePath))))
                {
                    reader.DateParseHandling = DateParseHandling.None;
                    token = JToken.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                    while (reader.Read())
                        if (reader.TokenType != JsonToken.Comment)
                            throw new JsonReaderException("Additional catalog content is not allowed.");
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is UnauthorizedAccessException)
            {
                throw new InvalidDataException("CATALOG_JSON_INVALID: Testing catalog cannot be read as JSON.", ex);
            }
            var array = token as JArray;
            if (array == null) throw SchemaError("Catalog must be an array.");
            var entries = new List<TestingCatalogEntry>();
            foreach (JToken item in array)
            {
                var obj = item as JObject;
                if (obj == null || obj.Properties().Count() != Properties.Length ||
                    obj.Properties().Any(p => !Properties.Contains(p.Name, StringComparer.Ordinal)))
                    throw SchemaError("Each entry must contain exactly the seven declared properties.");
                if (!IsString(obj["projectId"]) || obj["eligible"].Type != JTokenType.Boolean ||
                    !(IsString(obj["conflictGroup"]) || obj["conflictGroup"].Type == JTokenType.Null) ||
                    !IsString(obj["notes"]) || !IsStringArray(obj["requiredTaskIds"]) || !IsStringArray(obj["excludedTaskIds"]))
                    throw SchemaError("Entry property types do not match the catalog schema.");
                var groups = obj["taskGroups"] as JArray;
                if (groups == null || groups.Any(g => !IsStringArray(g)))
                    throw SchemaError("taskGroups must be an array of string arrays.");
                entries.Add(obj.ToObject<TestingCatalogEntry>());
            }
            var validation = new TestingCatalogValidator().Validate(entries, packages);
            if (validation.HasErrors)
                throw new InvalidDataException(string.Join(Environment.NewLine, validation.Issues.Select(i =>
                    i.Code + " [" + i.ProjectId + "] " + i.Message)));
            return entries.AsReadOnly();
        }

        private static bool IsString(JToken token)
        {
            return token != null && token.Type == JTokenType.String;
        }

        private static bool IsStringArray(JToken token)
        {
            var array = token as JArray;
            return array != null && array.All(IsString);
        }

        private static InvalidDataException SchemaError(string message)
        {
            return new InvalidDataException("CATALOG_SCHEMA_INVALID: " + message);
        }
    }
}
