using System;

namespace MosWord2019
{
    internal sealed class MissingTranslationException : Exception
    {
        internal MissingTranslationException(string message) : base(message) { }
    }
}
