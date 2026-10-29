using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MosWord2019.Core.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Tests
{
    public sealed class TestCaseData
    {
        public string Id { get; set; }
        public string ProjectId { get; set; }
        public string TaskId { get; set; }
        public string AssertionType { get; set; }
        public string Fixture { get; set; }
        public string Expected { get; set; }
        public string ContextKind { get; set; }
        public string TocLayout { get; set; }
        public JObject FormattingMarks { get; set; }
        public string OutputFileName { get; set; }
        public string OutputSource { get; set; }
        public bool? OutputExists { get; set; }
        public bool? SavedFromCurrentWorkingDocument { get; set; }
        public int? OwnedDocumentSaveFormat { get; set; }

        public static string FixturesRoot => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Fixtures");
        public static string ProjectsRoot => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Projects");

        public static List<TestCaseData> LoadAll()
        {
            var cases = JsonConvert.DeserializeObject<List<TestCaseData>>(
                File.ReadAllText(Path.Combine(FixturesRoot, "fixtures.json")));
            if (cases == null || cases.Count == 0) throw new InvalidDataException("The fixture manifest is empty.");
            if (cases.Any(c => c == null || string.IsNullOrWhiteSpace(c.Id)) ||
                cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != cases.Count)
                throw new InvalidDataException("Every fixture must have a unique nonempty id.");
            return cases;
        }

        public static string FixturePath(string relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
                throw new InvalidDataException("A fixture-relative path is required.");
            string root = Path.GetFullPath(FixturesRoot) + Path.DirectorySeparatorChar;
            string path = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Fixture path escapes the fixture directory.");
            if (!File.Exists(path)) throw new FileNotFoundException("A required fixture is missing.", path);
            return path;
        }
    }

    internal static class TestInventory
    {
        public const int ExpectedPackageCount = 28;
        public const int ExpectedTaskCount = 212;
        public const int ExpectedUsedAssertionTypeCount = 77;
        public const int ExpectedPackageFileCount = 149;
        public const int ExpectedNearMissTaskCount = 28;
    }

    internal sealed class TestWorkspace : IDisposable
    {
        private readonly string root = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "TestWorkspaces"));
        public string Folder { get; }

        public TestWorkspace()
        {
            Folder = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Folder);
        }

        public string Copy(string source, string name, bool customOfficeTemplate = false)
        {
            if (string.IsNullOrWhiteSpace(name) || name != Path.GetFileName(name) ||
                name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new InvalidDataException("A single output file name is required.");
            string directory = customOfficeTemplate ? Path.Combine(Folder, "Custom Office Templates") : Folder;
            Directory.CreateDirectory(directory);
            string target = Path.Combine(directory, name);
            File.Copy(source, target);
            return target;
        }

        public void Dispose()
        {
            string fullPath = Path.GetFullPath(Folder);
            if (!fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Refusing cleanup outside the test workspace.");
            if (Directory.Exists(fullPath)) Directory.Delete(fullPath, true);
        }
    }
}
