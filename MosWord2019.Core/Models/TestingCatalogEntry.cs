using System.Collections.Generic;
using Newtonsoft.Json;

namespace MosWord2019.Core.Models
{
    public sealed class TestingCatalogEntry
    {
        [JsonProperty("projectId")]
        public string ProjectId { get; set; } = "";
        [JsonProperty("eligible")]
        public bool Eligible { get; set; }
        [JsonProperty("conflictGroup")]
        public string ConflictGroup { get; set; }
        [JsonProperty("requiredTaskIds")]
        public List<string> RequiredTaskIds { get; set; } = new List<string>();
        [JsonProperty("taskGroups")]
        public List<List<string>> TaskGroups { get; set; } = new List<List<string>>();
        [JsonProperty("excludedTaskIds")]
        public List<string> ExcludedTaskIds { get; set; } = new List<string>();
        [JsonProperty("notes")]
        public string Notes { get; set; } = "";
    }
}
