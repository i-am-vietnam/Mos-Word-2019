using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Projects;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class PackageLoadingTests
    {
        [DataTestMethod]
        [DataRow("DocumentStyleSet", null)]
        [DataRow("documentstyleset", "ASSERTION_UNSUPPORTED")]
        [DataRow("DocumentStyleSet ", "ASSERTION_UNSUPPORTED")]
        [DataRow("NotAnImplementedAssertion", "ASSERTION_UNSUPPORTED")]
        [DataRow(null, "ASSERTION_TYPE_MISSING")]
        [DataRow("", "ASSERTION_TYPE_MISSING")]
        [DataRow("   ", "ASSERTION_TYPE_MISSING")]
        public void AssertionMetadataUsesTheProductionRoute(string assertionType, string expectedCode)
        {
            using (var workspace = new TestWorkspace())
            {
                string folder = CreatePackage(workspace.Folder, "Word2019_P90", assertionType);
                ValidationResult result = new ProjectValidator().Validate(folder);
                if (expectedCode == null)
                {
                    Assert.AreEqual(0, result.ErrorCount);
                    Assert.IsNotNull(new ProjectLoader().Load(folder, "en"));
                }
                else
                {
                    Assert.AreEqual(1, result.ErrorCount);
                    Assert.AreEqual(expectedCode, result.Issues.Single().Code);
                    Assert.AreEqual("Word2019_P90", result.Issues.Single().ProjectId);
                    Assert.AreEqual("T01", result.Issues.Single().TaskId);
                    Assert.IsNull(new ProjectLoader().Load(folder, "en"));
                }
            }
        }

        [DataTestMethod]
        [DataRow("en")]
        [DataRow("vi")]
        public void LoadAllReportsEveryRejectedRecognizedPackageAndResetsEachLoad(string language)
        {
            using (var workspace = new TestWorkspace())
            using (var nextWorkspace = new TestWorkspace())
            {
                CreatePackage(workspace.Folder, "Word2019_P90");
                CreatePackage(workspace.Folder, "Word2019_P89");
                CreatePackage(workspace.Folder, "Word2019_P91", "UnknownAssertion");
                string malformed = CreatePackage(workspace.Folder, "Word2019_P92");
                File.WriteAllText(Path.Combine(malformed, "tasks.json"), "[invalid json");
                string missingFields = CreatePackage(workspace.Folder, "Word2019_P93");
                File.WriteAllText(Path.Combine(missingFields, "tasks.json"), "[{\"taskId\":\"T01\"}]");
                string empty = CreatePackage(workspace.Folder, "Word2019_P94");
                File.WriteAllText(Path.Combine(empty, "tasks.json"), "[]");
                string nullTask = CreatePackage(workspace.Folder, "Word2019_P95");
                File.WriteAllText(Path.Combine(nullTask, "tasks.json"), "[null]");
                string missingLanguage = CreatePackage(workspace.Folder, "Word2019_P96");
                File.Delete(Path.Combine(missingLanguage, "lang", language + ".json"));
                string nullMeta = CreatePackage(workspace.Folder, "Word2019_P97");
                File.WriteAllText(Path.Combine(nullMeta, "meta.json"), "null");
                Directory.CreateDirectory(Path.Combine(workspace.Folder, "Word2019_P98_nearmiss"));
                Directory.CreateDirectory(Path.Combine(workspace.Folder, "Unrelated"));

                var expected = new Dictionary<string, string[]>
                {
                    ["Word2019_P91"] = new[] { "ASSERTION_UNSUPPORTED", "T01" },
                    ["Word2019_P92"] = new[] { "TASKS_JSON_INVALID" },
                    ["Word2019_P93"] = new[] { "TITLE_KEY_MISSING", "INSTRUCTION_KEY_MISSING", "ASSERTION_TYPE_MISSING", "T01" },
                    ["Word2019_P94"] = new[] { "TASKS_EMPTY" },
                    ["Word2019_P95"] = new[] { "TASK_NULL" },
                    ["Word2019_P96"] = new[] { "REQUESTED_LANGUAGE_MISSING" },
                    ["Word2019_P97"] = new[] { "META_JSON_INVALID" }
                };
                var messages = new List<string>();
                var loader = new ProjectLoader(new ProjectValidator(), messages.Add);
                for (int load = 0; load < 2; load++)
                {
                    var packages = loader.LoadAll(workspace.Folder, language);
                    CollectionAssert.AreEqual(new[] { "Word2019_P89", "Word2019_P90" },
                        packages.Select(p => p.Meta.ProjectId).ToArray());
                    Assert.AreEqual(expected.Count, loader.LastRejectedCount);
                    Assert.AreEqual(expected.Count * (load + 1), messages.Count);
                    foreach (var rejected in expected)
                    {
                        string message = messages.Skip(load * expected.Count).Single(m =>
                            m.Contains(Path.Combine(workspace.Folder, rejected.Key)));
                        foreach (string code in rejected.Value) StringAssert.Contains(message, code);
                    }
                }

                Assert.AreEqual(0, loader.LoadAll(nextWorkspace.Folder, language).Count);
                Assert.AreEqual(0, loader.LastRejectedCount);
                CreatePackage(nextWorkspace.Folder, "Word2019_P90");
                Assert.AreEqual(1, loader.LoadAll(nextWorkspace.Folder, language).Count);
                Assert.AreEqual(0, loader.LastRejectedCount);
                Assert.AreEqual(expected.Count * 2, messages.Count, "Ignored folders and successful/empty loads must not log rejections.");
                foreach (string absentRoot in new[] { Path.Combine(nextWorkspace.Folder, "absent"), null, " " })
                {
                    loader.LoadAll(workspace.Folder, language);
                    Assert.AreEqual(expected.Count, loader.LastRejectedCount);
                    int before = messages.Count;
                    Assert.AreEqual(0, loader.LoadAll(absentRoot, language).Count);
                    Assert.AreEqual(0, loader.LastRejectedCount);
                    Assert.AreEqual(before, messages.Count);
                }
            }
        }

        private static string CreatePackage(string root, string projectId, string assertionType = "DocumentStyleSet")
        {
            string folder = Path.Combine(root, projectId);
            Directory.CreateDirectory(Path.Combine(folder, "lang"));
            File.Copy(Path.Combine(TestCaseData.ProjectsRoot, "Word2019_P01", "starter.docx"),
                Path.Combine(folder, "starter.docx"));
            File.WriteAllText(Path.Combine(folder, "meta.json"), new JObject
            {
                ["projectId"] = projectId,
                ["starter"] = "starter.docx"
            }.ToString());
            File.WriteAllText(Path.Combine(folder, "tasks.json"), JsonConvert.SerializeObject(new[]
            {
                new TaskDefinition { TaskId = "T01", TitleKey = "title", InstructionKey = "instruction", AssertionType = assertionType }
            }));
            foreach (string language in new[] { "en", "vi" })
                File.WriteAllText(Path.Combine(folder, "lang", language + ".json"),
                    "{\"title\":\"Title\",\"instruction\":\"Instruction\"}");
            return folder;
        }
    }
}
