using System.Collections.Generic;

namespace MosWord2019.Core.Models
{
    public sealed class ExamTaskGrade
    {
        public string ProjectId { get; }
        public string TaskId { get; }
        public TaskGradeResult Result { get; }
        public ExamTaskGrade(string projectId, string taskId, TaskGradeResult result)
        {
            ProjectId = projectId;
            TaskId = taskId;
            Result = result;
        }
    }

    public sealed class ExamProjectScore
    {
        public string ProjectId { get; }
        public int Points { get; }
        public int MaximumPoints { get; }
        internal ExamProjectScore(string projectId, int points, int maximumPoints)
        {
            ProjectId = projectId;
            Points = points;
            MaximumPoints = maximumPoints;
        }
    }

    public sealed class ExamWrongTask
    {
        public string ProjectCode { get; }
        public string TaskId { get; }
        public string InstructionKey { get; }
        internal ExamWrongTask(string projectId, string taskId, string instructionKey)
        {
            ProjectCode = projectId.Substring("Word2019_".Length);
            TaskId = taskId;
            InstructionKey = instructionKey;
        }
    }

    public sealed class ExamResult
    {
        public int Total { get; }
        public bool Passed => Total >= 700;
        public IReadOnlyList<ExamProjectScore> PerProject { get; }
        public IReadOnlyList<ExamWrongTask> WrongTasks { get; }
        internal ExamResult(int total, List<ExamProjectScore> perProject, List<ExamWrongTask> wrongTasks)
        {
            Total = total;
            PerProject = perProject.AsReadOnly();
            WrongTasks = wrongTasks.AsReadOnly();
        }
    }
}
