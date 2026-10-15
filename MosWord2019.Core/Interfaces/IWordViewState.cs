using MosWord2019.Core.Models;

namespace MosWord2019.Core.Interfaces
{
    /// <summary>Optional read-only access to the existing owned document's view on its STA thread.</summary>
    public interface IWordViewState
    {
        WordFormattingMarksSnapshot ReadFormattingMarks();
    }
}
