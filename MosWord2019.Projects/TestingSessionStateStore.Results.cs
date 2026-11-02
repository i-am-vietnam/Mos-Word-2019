using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MosWord2019.Core.Diagnostics;
using MosWord2019.Core.IO;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using Newtonsoft.Json.Linq;
namespace MosWord2019.Projects
{
    public sealed partial class TestingSessionStateStore
    {
        /// <summary>Idempotent result commit. Recovery state remains until cleanup/archive succeeds.</summary>
        public string PersistCompletedResult()
        {
            lock (gate)
            {
                var data = ReadEnvelope();
                if ((string)data["status"] != "Completed" || data["result"]?.Type != JTokenType.Object)
                    throw new InvalidOperationException("Only a stored completed result may be archived.");
                JObject result = BuildResult(data);
                string directory = Path.Combine(Path.GetDirectoryName(StatePath), "Results");
                PreparedTestingSession.CheckPath(directory); Directory.CreateDirectory(directory);
                string target = Path.Combine(directory, (string)data["sessionId"] + ".json");
                PreparedTestingSession.CheckPath(target);
                if (File.Exists(target))
                {
                    if (!SameCommittedResult(JObject.Parse(File.ReadAllText(target)), result))
                        throw new IOException("An existing result differs; both files are preserved: " + target);
                    return target;
                }
                string temp = Path.Combine(directory, ".result-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(result.ToString());
                    using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                    AtomicFile.Commit(temp, target, false, () => { PreparedTestingSession.CheckPath(temp); PreparedTestingSession.CheckPath(target); });
                    return target;
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }
        private static bool SameCommittedResult(JObject stored, JObject expected)
        {
            var left = (JObject)stored.DeepClone(); var right = (JObject)expected.DeepClone();
            // Freeze the first completed record: cleanup can subsequently observe saves/heartbeats.
            foreach (string field in new[] { "lastSaveUtc", "lastHeartbeatUtc" })
            {
                if (left.Property(field) == null) return false;
                left.Remove(field); right.Remove(field);
            }
            return JToken.DeepEquals(left, right);
        }
        private static JObject BuildResult(JObject data)
        {
            var projects = data["definition"]?["Projects"] as JArray;
            var locked = data["lockedResults"] as JArray;
            if (projects == null || locked == null) throw new InvalidDataException("Completed exam definition/grades are missing.");
            var definition = new ExamDefinition((long)data["seed"], projects.Select(p =>
            {
                var ids = p["TaskIds"] as JArray;
                if (ids == null) throw new InvalidDataException("Completed exam task IDs are missing.");
                return new ExamProjectDefinition((string)p["ProjectId"], ids.Select(t => new TaskDefinition
                { TaskId = (string)t, InstructionKey = (string)p["InstructionKeys"]?[(string)t] }));
            }));
            var grades = locked.ToObject<List<ExamTaskGrade>>();
            var score = new ExamScorer(s => AppLogger.Write(s)).Score(definition, grades);
            if ((int?)data["result"]?["Total"] != score.Total)
                throw new InvalidDataException("Stored result total does not match its grades.");
            var byTask = grades.ToDictionary(g => g.ProjectId + "/" + g.TaskId, StringComparer.Ordinal);
            int[] weights = ScoreAllocator.Allocate(definition.N, definition.Seed); int index = 0;
            var tasks = new JArray();
            foreach (var project in definition.Projects)
                foreach (string id in project.TaskIds)
                {
                    ExamTaskGrade grade; byTask.TryGetValue(project.ProjectId + "/" + id, out grade);
                    var outcome = grade?.Result.Outcome ?? TaskGradeOutcome.Error;
                    int points = weights[index++];
                    tasks.Add(new JObject { ["projectId"] = project.ProjectId, ["taskId"] = id, ["instructionKey"] = project.InstructionKeys[id],
                        ["outcome"] = outcome.ToString(), ["points"] = points, ["awardedPoints"] = outcome == TaskGradeOutcome.Pass ? points : 0 });
                }
            // Deliberately exclude grader messages, working paths and ownership/COM recovery details.
            return new JObject
            {
                ["version"] = 1, ["sessionId"] = data["sessionId"].DeepClone(), ["seed"] = definition.Seed,
                ["machine"] = data["onlineMachineId"]?.Type == JTokenType.String ? data["onlineMachineId"].DeepClone() : data["machine"]?.DeepClone(), ["language"] = data["language"]?.DeepClone(),
                ["definition"] = JToken.FromObject(definition), ["contentHashes"] = data["contentHashes"]?.DeepClone(),
                ["startedUtc"] = data["startedUtc"]?.DeepClone(), ["completedUtc"] = data["completedUtc"]?.DeepClone(),
                ["lastHeartbeatUtc"] = data["lastHeartbeatUtc"]?.DeepClone(), ["lastSaveUtc"] = data["lastSaveUtc"]?.DeepClone(),
                ["elapsedTicks"] = data["elapsedTicks"]?.DeepClone(), ["offlinePeriods"] = data["offlinePeriods"]?.DeepClone() ?? new JArray(),
                ["historyIncomplete"] = (bool?)data["historyIncomplete"] == true || data["startedUtc"]?.Type != JTokenType.Date,
                ["clockRollbackDetected"] = (bool?)data["clockRollbackDetected"] ?? false,
                ["finalizeReason"] = data["finalizeReason"]?.DeepClone(), ["tasks"] = tasks, ["total"] = score.Total,
                ["passed"] = score.Passed, ["perProject"] = JToken.FromObject(score.PerProject), ["pendingUpload"] = true
            };
        }
    }
}
