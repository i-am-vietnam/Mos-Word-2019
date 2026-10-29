using System.IO;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;
using Newtonsoft.Json.Linq;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class PlainTextMetadataTests
    {
        [DataTestMethod]
        [DataRow("textSegmentAlternatives", "{}")]
        [DataRow("textSegmentAlternatives", "[]")]
        [DataRow("textSegmentAlternatives", "[{}]")]
        [DataRow("textSegmentAlternatives", "[[]]")]
        [DataRow("textSegmentAlternatives", "[[null]]")]
        [DataRow("textSegmentAlternatives", "[[123]]")]
        [DataRow("textSegmentAlternatives", "[[\" \"]]")]
        [DataRow("textSegmentAlternatives", "[\"not a group\"]")]
        [DataRow("requiredTextSegments", "{}")]
        [DataRow("requiredTextSegments", "[123]")]
        [DataRow("requiredTextSegments", "[\" \"]")]
        [DataRow("minimumCharacters", "{}")]
        [DataRow("expectedFileName", "123")]
        public void MalformedExportMetadataIsErrorEvenWhenOutputIsMissing(string key, string json)
        {
            using (var workspace = new TestWorkspace())
            {
                TaskDefinition task = ExportTask();
                task.Extra[key] = JToken.Parse(json);
                TaskGradeResult result = new WordGradingService(() => workspace.Folder).CheckTask(null, task);
                Assert.AreEqual(TaskGradeOutcome.Error, result.Outcome, result.Message);
                StringAssert.Contains(result.Message, "Invalid grading metadata:");
                StringAssert.Contains(result.Message, key);
            }
        }

        [DataTestMethod]
        [DataRow(null, "Alpha Beta Gamma", TaskGradeOutcome.Pass)]
        [DataRow("[[\"Beta\",\"Delta\"],[\"Gamma\"]]", "Alpha Beta Gamma", TaskGradeOutcome.Pass)]
        [DataRow("[[\"Beta\"],[\"Delta\"]]", "Alpha Beta Gamma", TaskGradeOutcome.Fail)]
        [DataRow(null, "Alpha", TaskGradeOutcome.Fail)]
        public void ValidExportMetadataPreservesContentDecisions(string alternatives, string content, TaskGradeOutcome expected)
        {
            using (var workspace = new TestWorkspace())
            {
                TaskDefinition task = ExportTask();
                if (alternatives != null) task.Extra["textSegmentAlternatives"] = JToken.Parse(alternatives);
                var ledger = new OutputOwnershipLedger(new ProjectPackage { Tasks = new List<TaskDefinition> { task } },
                    workspace.Folder, null, _ => { });
                File.WriteAllText(Path.Combine(workspace.Folder, "Answer.txt"), content);
                TaskGradeResult result = new WordGradingService(() => workspace.Folder).CheckTask(null, task,
                    new TrainingGradeContext { OutputOwnership = ledger.Capture() });
                Assert.AreEqual(expected, result.Outcome, result.Message);
            }
        }

        private static TaskDefinition ExportTask()
        {
            var task = new TaskDefinition { TaskId = "T01", AssertionType = "PlainTextDocumentExport" };
            task.Extra["expectedFileName"] = "Answer.txt";
            task.Extra["minimumCharacters"] = 10;
            task.Extra["requiredTextSegments"] = new JArray("Alpha");
            return task;
        }
    }
}
