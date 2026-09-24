namespace MosWord2019.Core.Interfaces
{
    /// <summary>Optional placement of the owned document window in screen coordinates.</summary>
    public interface IWordWindowLayout
    {
        void SetWindowBounds(int left, int top, int width, int height);
    }
}
