namespace MosWord2019.Core.Models
{
    /// <summary>Runtime provenance supplied by the owner of the original working document, not by task JSON.</summary>
    public sealed class TrainingGradeContext
    {
        public string DefaultTemplateFolder { get; set; }
        public bool SavedFromCurrentWorkingDocument { get; set; }
    }
}
