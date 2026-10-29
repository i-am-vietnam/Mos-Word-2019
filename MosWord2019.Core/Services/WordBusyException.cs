using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MosWord2019.Core.Services
{
    /// <summary>A recoverable Word rejection after the bounded STA retry window.</summary>
    public sealed class WordBusyException : InvalidOperationException
    {
        public WordBusyException(Exception innerException)
            : base("Word is busy. Close any Word dialog and try again.", innerException) { }

        public static bool IsBusy(Exception exception)
        {
            if (exception == null) return false;
            var pending = new Stack<Exception>();
            var seen = new HashSet<Exception>();
            pending.Push(exception);
            while (pending.Count != 0)
            {
                Exception current = pending.Pop();
                if (!seen.Add(current)) continue;
                if (current is WordBusyException) return true;
                var com = current as COMException;
                if (com != null && (com.ErrorCode == unchecked((int)0x80010001) ||
                    com.ErrorCode == unchecked((int)0x8001010A))) return true;
                var aggregate = current as AggregateException;
                if (aggregate != null)
                    foreach (Exception inner in aggregate.InnerExceptions) pending.Push(inner);
                if (current.InnerException != null) pending.Push(current.InnerException);
            }
            return false;
        }
    }
}
