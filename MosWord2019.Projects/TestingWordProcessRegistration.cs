using System;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;

namespace MosWord2019.Projects
{
    /// <summary>Install before Testing starts its owned Word; persistence failure cancels startup.</summary>
    public sealed class TestingWordProcessRegistration : IDisposable
    {
        private readonly IWordProcessIdentity source;
        private readonly PreparedTestingSession prepared;
        private readonly TestingSessionStateStore store;
        public TestingWordProcessRegistration(IWordProcessIdentity source, PreparedTestingSession prepared, TestingSessionStateStore store)
        {
            this.source = source ?? throw new ArgumentNullException(nameof(source));
            this.prepared = prepared ?? throw new ArgumentNullException(nameof(prepared));
            this.store = store ?? throw new ArgumentNullException(nameof(store));
            source.OwnedProcessStarted += Started;
        }
        private void Started(OwnedWordProcessIdentity identity) { store.RecordOwnedWord(prepared, identity); }
        public void Dispose() { source.OwnedProcessStarted -= Started; }
    }
}
