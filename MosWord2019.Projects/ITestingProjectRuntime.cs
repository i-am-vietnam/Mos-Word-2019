using System;
using System.Collections.Generic;
using MosWord2019.Core.Models;
namespace MosWord2019.Projects
{
    public interface ITestingForceRecovery { void ForceCloseOwnedWord(); }
    public interface ITestingProjectRuntime : IDisposable
    {
        void Enter(string projectId, bool resume);
        void AutosaveCurrent();
        void Observe();
        void SetInputEnabled(bool enabled);
        void LeaveAndGrade(Action<IReadOnlyList<ExamTaskGrade>> persist);
        IReadOnlyList<ExamTaskGrade> GradeSaved(string projectId);
        void Restart(string projectId);
        void Close();
    }
}
