namespace MosWord2019.Core.Models
{
    public sealed class ProjectMeta
    {
        public string ProjectId { get; set; } = "";
        public string OfficeVersion { get; set; } = "";
        public string Version { get; set; } = "";
        public string Starter { get; set; } = "starter.docx";
        // Optional, separately verified prerequisite baseline. Never replaces the supplied Starter.
        public string PreparedStarter { get; set; } = "";
        public string PreparationContextKey { get; set; } = "";
    }
}
