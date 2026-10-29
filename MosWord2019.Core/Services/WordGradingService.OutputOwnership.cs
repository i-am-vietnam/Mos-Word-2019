using System;
using System.IO;
using System.Linq;
using MosWord2019.Core.Models;

namespace MosWord2019.Core.Services
{
    public sealed partial class WordGradingService
    {
        private static bool CheckOutputFreshness(TaskDefinition task, TrainingGradeContext context,
            string documentsFolder, out string outputPath, out string detail)
        {
            string name = RequiredString(task, "expectedFileName");
            var ownership = context?.OutputOwnership;
            if (ownership == null || string.IsNullOrWhiteSpace(ownership.SessionId) ||
                ownership.SessionStartedUtc == default(DateTime) || ownership.SessionStartedUtc.Kind != DateTimeKind.Utc)
                throw new InvalidDataException("The current session's declared-output baseline is unavailable.");
            bool template = task.AssertionType == TemplateOutputLocation.AssertionType || task.AssertionType == "SavedWordTemplate";
            string folder = template ? ownership.TemplateFolder : ownership.DocumentsFolder;
            if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathRooted(folder))
                throw new InvalidDataException("The declared output folder is unavailable.");
            string expectedPath = template ? TemplateOutputLocation.ResolveInFolder(name, folder)
                : ExternalOutputLocation.ResolveDocuments(name, folder);
            outputPath = expectedPath;
            var matches = ownership.Outputs.Where(o => o?.Baseline != null &&
                string.Equals(o.Baseline.Path, expectedPath, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
                throw new InvalidDataException("The declared output baseline is missing or ambiguous: " + name);
            var entry = matches[0];
            if (!string.IsNullOrWhiteSpace(entry.ObservationError))
                throw new InvalidDataException("Unable to observe the declared output: " + entry.ObservationError);
            var baseline = entry.Baseline;
            var current = entry.Current;
            if (current == null || !string.Equals(current.Path, outputPath, StringComparison.OrdinalIgnoreCase) ||
                baseline.ObservedUtc.Kind != DateTimeKind.Utc || current.ObservedUtc.Kind != DateTimeKind.Utc ||
                baseline.ObservedUtc < ownership.SessionStartedUtc || current.ObservedUtc < baseline.ObservedUtc)
                throw new InvalidDataException("The declared output observation does not belong to the current baseline.");
            foreach (var state in new[] { baseline, current }.Where(s => s.Exists))
                if (state.Length < 0 || string.IsNullOrWhiteSpace(state.Identity) || state.Sha256 == null ||
                    state.Sha256.Length != 64 || state.Sha256.Any(c => !Uri.IsHexDigit(c)))
                    throw new InvalidDataException("The declared output observation is incomplete: " + name);
            detail = "The declared output must exist and be new or have changed content since this session's baseline.";
            return current.Exists && (!baseline.Exists ||
                !string.Equals(current.Sha256, baseline.Sha256, StringComparison.OrdinalIgnoreCase));
        }
    }
}
