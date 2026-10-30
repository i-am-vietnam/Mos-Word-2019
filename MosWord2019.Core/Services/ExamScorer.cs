using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MosWord2019.Core.Models;

namespace MosWord2019.Core.Services
{
    public sealed class ExamScorer
    {
        private readonly Action<string> logError;
        // The session supplies its internal logger. No diagnostics enter the learner result model.
        public ExamScorer(Action<string> logError)
        {
            this.logError = logError ?? throw new ArgumentNullException(nameof(logError));
        }

        public ExamResult Score(ExamDefinition definition, IEnumerable<ExamTaskGrade> results)
        {
            if (definition == null) throw new ArgumentNullException(nameof(definition));
            if (results == null) throw new ArgumentNullException(nameof(results));
            var expected = definition.Projects.ToDictionary(p => p.ProjectId, StringComparer.Ordinal);
            var grades = new Dictionary<string, TaskGradeResult>(StringComparer.Ordinal);
            foreach (var grade in results)
            {
                ExamProjectDefinition project;
                if (grade == null || grade.ProjectId == null || grade.TaskId == null || grade.Result == null ||
                    !expected.TryGetValue(grade.ProjectId, out project) || !project.TaskIds.Contains(grade.TaskId) ||
                    grade.Result.TaskId != grade.TaskId || !Enum.IsDefined(typeof(TaskGradeOutcome), grade.Result.Outcome))
                    throw new InvalidDataException("Exam result does not match a selected task.");
                string key = grade.ProjectId + "/" + grade.TaskId;
                if (grades.ContainsKey(key)) throw new InvalidDataException("Duplicate exam task result: " + key);
                grades.Add(key, grade.Result);
            }
            int[] points = ScoreAllocator.Allocate(definition.N, definition.Seed);
            int index = 0, total = 0;
            var perProject = new List<ExamProjectScore>();
            var wrong = new List<ExamWrongTask>();
            foreach (var project in definition.Projects)
            {
                int earned = 0, maximum = 0;
                foreach (string id in project.TaskIds)
                {
                    int value = points[index++];
                    maximum += value;
                    TaskGradeResult grade;
                    if (grades.TryGetValue(project.ProjectId + "/" + id, out grade) && grade.Outcome == TaskGradeOutcome.Pass)
                        earned += value;
                    else
                    {
                        wrong.Add(new ExamWrongTask(project.ProjectId, id, project.InstructionKeys[id]));
                        if (grade != null && grade.Outcome == TaskGradeOutcome.Error)
                            logError(project.ProjectId + "/" + id + ": " + grade.Message);
                    }
                }
                total += earned;
                perProject.Add(new ExamProjectScore(project.ProjectId, earned, maximum));
            }
            return new ExamResult(total, perProject, wrong);
        }
    }
}
