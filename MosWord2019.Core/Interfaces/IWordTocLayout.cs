using MosWord2019.Core.Models;

namespace MosWord2019.Core.Interfaces
{
    /// <summary>Optional layout observations from the already-owned document on its STA thread.</summary>
    public interface IWordTocLayout
    {
        WordTocLayoutSnapshot ReadTocLayout();
    }
}
