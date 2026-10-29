using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
using Newtonsoft.Json;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class FixtureTests
    {
        public static IEnumerable<object[]> Cases()
        {
            return TestCaseData.LoadAll().Select(c => new object[] { c.Id });
        }

        public static string CaseName(MethodInfo method, object[] data) => (string)data[0];

        [DataTestMethod]
        [DynamicData(nameof(Cases), DynamicDataSourceType.Method, DynamicDataDisplayName = nameof(CaseName))]
        public void SavedFixtureMatchesExpectedOutcome(string fixtureId)
        {
            TestCaseData data = TestCaseData.LoadAll().Single(c => c.Id == fixtureId);
            ProjectPackage package = new ProjectLoader().Load(Path.Combine(TestCaseData.ProjectsRoot, data.ProjectId), "en");
            Assert.IsNotNull(package, "The original production package must load.");
            TaskDefinition task = package.Tasks.Single(t => t.TaskId == data.TaskId);
            Assert.AreEqual(task.AssertionType, data.AssertionType, "The fixture must use the original task assertion.");
            TaskGradeOutcome expected;
            Assert.IsTrue(Enum.TryParse(data.Expected, false, out expected) && Enum.IsDefined(typeof(TaskGradeOutcome), expected),
                "Expected must be Pass, Fail or Error.");
            string source = TestCaseData.FixturePath(data.Fixture);
            using (var workspace = new TestWorkspace())
            {
                var context = new TrainingGradeContext();
                var grader = new WordGradingService();
                string path = source;
                switch (data.ContextKind)
                {
                    case null:
                    case "":
                        break;
                    case "SavedTemplate":
                        var templateLedger = new OutputOwnershipLedger(package, workspace.Folder, workspace.Folder, _ => { });
                        path = workspace.Copy(source, data.OutputFileName ?? (string)task.Extra["expectedFileName"]);
                        context.DefaultTemplateFolder = workspace.Folder;
                        context.SavedFromCurrentWorkingDocument = data.SavedFromCurrentWorkingDocument ?? true;
                        context.OutputOwnership = templateLedger.Capture(path);
                        break;
                    case "Toc":
                        var snapshot = JsonConvert.DeserializeObject<WordTocLayoutSnapshot>(
                            File.ReadAllText(TestCaseData.FixturePath(data.TocLayout)));
                        Assert.IsNotNull(snapshot, "A TOC fixture requires its recorded layout snapshot.");
                        context.TocLayout = new WordTocLayoutSnapshot(path, snapshot.Targets);
                        break;
                    case "FormattingMarks":
                        Assert.IsNotNull(data.FormattingMarks, "Formatting-mark fixture flags are required.");
                        context.FormattingMarks = data.FormattingMarks.ToObject<WordFormattingMarksSnapshot>();
                        break;
                    case "OwnedPlainText":
                        context.OwnedDocumentPath = workspace.Copy(
                            data.OutputSource == null ? source : TestCaseData.FixturePath(data.OutputSource),
                            data.OutputFileName ?? (string)task.Extra["expectedFileName"]);
                        context.OwnedDocumentSaveFormat = data.OwnedDocumentSaveFormat ?? 2;
                        break;
                    case "DocumentsExistence":
                    case "TemplatesExistence":
                        Assert.IsTrue(data.OutputExists.HasValue, "Output-existence fixture state is required.");
                        var outputLedger = new OutputOwnershipLedger(package, workspace.Folder,
                            Path.Combine(workspace.Folder, "Custom Office Templates"), _ => { });
                        if (data.OutputExists.Value)
                            workspace.Copy(data.OutputSource == null ? source : TestCaseData.FixturePath(data.OutputSource),
                                data.OutputFileName ?? (string)task.Extra["expectedFileName"],
                                data.ContextKind == "TemplatesExistence");
                        grader = new WordGradingService(() => workspace.Folder);
                        context.OutputOwnership = outputLedger.Capture();
                        break;
                    default:
                        Assert.Fail("Unsupported fixture context: " + data.ContextKind);
                        break;
                }
                TaskGradeResult result = grader.CheckTask(path, task, context);
                Assert.AreEqual(expected, result.Outcome, fixtureId + ": " + result.Message);
            }
        }

        [TestMethod]
        public void PublicOutputResolversKeepTheWindowsDocumentsDefault()
        {
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Assert.AreEqual(Path.Combine(documents, "Memo.txt"), ExternalOutputLocation.ResolveDocuments("Memo.txt"));
            Assert.AreEqual(Path.Combine(TemplateFolderResolver.Resolve(), "Notes.dotx"),
                TemplateOutputLocation.Resolve("Notes.dotx"));
        }

        [DataTestMethod]
        [DataRow("", "")]
        [DataRow("..\\Memo.txt", "..\\Notes.dotx")]
        [DataRow("folder/Memo.txt", "folder/Notes.dotx")]
        [DataRow("C:\\Memo.txt", "C:\\Notes.dotx")]
        [DataRow("Memo?.txt", "Notes?.dotx")]
        public void OutputResolversRejectInvalidFileNamesWithEitherRoot(string documentName, string templateName)
        {
            using (var workspace = new TestWorkspace())
            {
                Assert.ThrowsException<InvalidDataException>(() => ExternalOutputLocation.ResolveDocuments(documentName));
                Assert.ThrowsException<InvalidDataException>(() => ExternalOutputLocation.ResolveDocuments(documentName, workspace.Folder));
                Assert.ThrowsException<InvalidDataException>(() => TemplateOutputLocation.Resolve(templateName));
                Assert.ThrowsException<InvalidDataException>(() => TemplateOutputLocation.Resolve(templateName, workspace.Folder));
            }
        }

        [DataTestMethod]
        [DataRow("Word2019_P09", "T08", false)]
        [DataRow("Word2019_P07", "T08", true)]
        public void ExternalOutputRootsAreIsolatedPerGrader(string projectId, string taskId, bool customOfficeTemplate)
        {
            ProjectPackage package = new ProjectLoader().Load(Path.Combine(TestCaseData.ProjectsRoot, projectId), "en");
            Assert.IsNotNull(package);
            TaskDefinition task = package.Tasks.Single(t => t.TaskId == taskId);
            using (var first = new TestWorkspace())
            using (var second = new TestWorkspace())
            {
                var firstLedger = new OutputOwnershipLedger(package, first.Folder, Path.Combine(first.Folder, "Custom Office Templates"), _ => { });
                var secondLedger = new OutputOwnershipLedger(package, second.Folder, Path.Combine(second.Folder, "Custom Office Templates"), _ => { });
                first.Copy(Path.Combine(package.ProjectFolderPath, package.Meta.Starter),
                    (string)task.Extra["expectedFileName"], customOfficeTemplate);
                var firstGrader = new WordGradingService(() => first.Folder);
                var secondGrader = new WordGradingService(() => second.Folder);
                Assert.AreEqual(TaskGradeOutcome.Pass, firstGrader.CheckTask(null, task, new TrainingGradeContext { OutputOwnership = firstLedger.Capture() }).Outcome);
                Assert.AreEqual(TaskGradeOutcome.Fail, secondGrader.CheckTask(null, task, new TrainingGradeContext { OutputOwnership = secondLedger.Capture() }).Outcome);
                Assert.AreEqual(TaskGradeOutcome.Pass, firstGrader.CheckTask(null, task, new TrainingGradeContext { OutputOwnership = firstLedger.Capture() }).Outcome);
                PublicOutputResolversKeepTheWindowsDocumentsDefault();
            }
        }

        [DataTestMethod]
        [DataRow("en")]
        [DataRow("vi")]
        public void ProductionPackagesValidate(string language)
        {
            var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, language);
            Assert.AreEqual(TestInventory.ExpectedPackageCount, packages.Count,
                "When adding a package, update TestInventory.ExpectedPackageCount in TestCaseData.cs after validating its fixtures.");
            Assert.AreEqual(TestInventory.ExpectedTaskCount, packages.Sum(p => p.Tasks.Count),
                "When adding tasks, update TestInventory.ExpectedTaskCount in TestCaseData.cs and add their regression fixtures.");
            foreach (var package in packages)
            {
                ValidationResult result = new ProjectValidator().Validate(package.ProjectFolderPath, language);
                Assert.AreEqual(0, result.ErrorCount, package.Meta.ProjectId);
                Assert.AreEqual(0, result.WarningCount, package.Meta.ProjectId);
            }
        }

        [TestMethod]
        public void EveryUsedAssertionHasPositiveAndNegativeFixtures()
        {
            var cases = TestCaseData.LoadAll();
            var used = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, "en")
                .SelectMany(p => p.Tasks).Select(t => t.AssertionType).Distinct().OrderBy(t => t).ToList();
            Assert.AreEqual(TestInventory.ExpectedUsedAssertionTypeCount, used.Count,
                "When adding an assertion type, update TestInventory.ExpectedUsedAssertionTypeCount in TestCaseData.cs and add Pass/Fail fixtures.");
            CollectionAssert.AreEquivalent(used, cases.Select(c => c.AssertionType).Distinct().ToList());
            foreach (string type in used)
            {
                Assert.IsTrue(cases.Any(c => c.AssertionType == type && c.Expected == "Pass"), type + " needs a positive fixture.");
                Assert.IsTrue(cases.Any(c => c.AssertionType == type && c.Expected == "Fail"), type + " needs a negative fixture.");
            }
            foreach (var data in cases)
            {
                Assert.AreEqual(".docx", Path.GetExtension(TestCaseData.FixturePath(data.Fixture)),
                    "Retained fixtures use the portable .docx fixture convention: " + data.Id);
                string expectedFolder = data.ProjectId + "/" + data.TaskId + "/";
                Assert.IsTrue(data.Fixture.StartsWith(expectedFolder, StringComparison.Ordinal),
                    "Fixture must belong to its original project/task: " + data.Id);
                string name = Path.GetFileName(data.Fixture);
                Assert.IsTrue(name.StartsWith(data.Expected == "Pass" ? "pass" : "fail", StringComparison.Ordinal) ||
                    (data.Expected == "Fail" && name.StartsWith("nearmiss", StringComparison.Ordinal)),
                    "Fixture filename must declare pass, fail or nearmiss: " + data.Id);
            }
            foreach (string file in Directory.GetFiles(TestCaseData.FixturesRoot, "*", SearchOption.AllDirectories))
                Assert.IsTrue(new FileInfo(file).Length <= 2000000, "Fixture exceeds 2 MB: " + file);
        }

        [TestMethod]
        public void PackageCopyMatchesSource()
        {
            string source = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                "..", "..", "..", "MosWord2019.WinForms", "Projects"));
            Assert.IsTrue(Directory.Exists(source), "Run the tests from their repository build output.");
            string[] sourceFiles = Directory.GetFiles(source, "*", SearchOption.AllDirectories)
                .Where(p => p != Path.Combine(source, "testing-catalog.json")).ToArray();
            string[] copiedFiles = Directory.GetFiles(TestCaseData.ProjectsRoot, "*", SearchOption.AllDirectories)
                .Where(p => p != Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json")).ToArray();
            Assert.AreEqual(TestInventory.ExpectedPackageFileCount, sourceFiles.Length,
                "When adding package files, update TestInventory.ExpectedPackageFileCount in TestCaseData.cs after checking output hashes.");
            CollectionAssert.AreEquivalent(sourceFiles.Select(p => p.Substring(source.Length + 1)).ToList(),
                copiedFiles.Select(p => p.Substring(TestCaseData.ProjectsRoot.Length + 1)).ToList());
            using (var hash = SHA256.Create())
                foreach (string path in sourceFiles)
                {
                    string relative = path.Substring(source.Length + 1);
                    byte[] original;
                    using (var stream = File.OpenRead(path)) original = hash.ComputeHash(stream);
                    using (var stream = File.OpenRead(Path.Combine(TestCaseData.ProjectsRoot, relative)))
                        CollectionAssert.AreEqual(original, hash.ComputeHash(stream), relative);
                }
        }

        [TestMethod]
        public void TestAssemblyHasNoOfficeOrUiDependency()
        {
            foreach (var assembly in new[] { typeof(FixtureTests).Assembly, typeof(WordGradingService).Assembly, typeof(ProjectLoader).Assembly })
                foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
                    Assert.IsFalse(reference.Name.StartsWith("Microsoft.Office", StringComparison.OrdinalIgnoreCase) ||
                        reference.Name == "MosWord2019.Word" || reference.Name == "MosWord2019.WinForms" ||
                        reference.Name == "System.Windows.Forms", "Forbidden test dependency: " + reference.Name);
        }
    }
}
