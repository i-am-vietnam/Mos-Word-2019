namespace MosWord2019.Core.Models
{
    public sealed class TaskGradeResult
    {
        public TaskGradeOutcome Outcome { get; }
        public string Message { get; }
        public string AssertionType { get; }
        public string TaskId { get; }

        public TaskGradeResult(TaskGradeOutcome outcome, string message, string assertionType, string taskId)
        {
            Outcome = outcome;
            Message = message ?? "";
            AssertionType = assertionType ?? "";
            TaskId = taskId ?? "";
        }
    }
}
