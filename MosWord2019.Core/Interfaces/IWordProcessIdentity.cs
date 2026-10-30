using System;
using MosWord2019.Core.Models;
namespace MosWord2019.Core.Interfaces
{
    public interface IWordProcessIdentity
    {
        OwnedWordProcessIdentity OwnedProcessIdentity { get; }
        event Action<OwnedWordProcessIdentity> OwnedProcessStarted;
    }
}
