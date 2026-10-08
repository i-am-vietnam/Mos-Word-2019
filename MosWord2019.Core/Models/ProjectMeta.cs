namespace MosWord2019.Core.Models
{
    public sealed class ProjectMeta
    {
        public string ProjectId { get; set; } = "";
        public string OfficeVersion { get; set; } = "";
        public string Version { get; set; } = "";
        public string Starter { get; set; } = "starter.docx";
        public AssetStagingOptions AssetStaging { get; set; }
    }

    public sealed class AssetStagingOptions
    {
        public bool CopyAssetsToDocuments { get; set; } = true;
        public System.Collections.Generic.List<AssetDestinationCopy> AdditionalCopies { get; set; } =
            new System.Collections.Generic.List<AssetDestinationCopy>();
        public string CleanupPolicy { get; set; } = "OnProjectClose";
    }

    public sealed class AssetDestinationCopy
    {
        public string Source { get; set; } = "";
        public string Destination { get; set; } = "";
    }
}
