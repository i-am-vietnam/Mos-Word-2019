using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;

namespace MosWord2019.Core.Models
{
    public sealed class TaskDefinition
    {
        [JsonIgnore]
        public string ProjectId { get; set; } = "";
        public string TaskId { get; set; } = "";
        public string TitleKey { get; set; } = "";
        public string InstructionKey { get; set; } = "";
        public string AssertionType { get; set; } = "";

        [JsonExtensionData]
        public IDictionary<string, JToken> Extra { get; set; } =
            new Dictionary<string, JToken>(System.StringComparer.OrdinalIgnoreCase);
    }
}
