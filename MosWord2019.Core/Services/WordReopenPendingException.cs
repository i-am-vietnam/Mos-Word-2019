using System;

namespace MosWord2019.Core.Services
{
    /// <summary>Reopen exhausted this observation's retries; checkpoints remain available for the next attempt.</summary>
    public sealed class WordReopenPendingException : InvalidOperationException
    {
        public WordReopenPendingException(Exception inner) : base("Word could not be reopened after three attempts; the checkpoint is retained for retry.", inner) { }
    }
}
