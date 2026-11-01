using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019;
namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class WordWarmupTests
    {
        [TestMethod]
        public void GoDuringWarmingJoinsTheSameActivation()
        {
            var gate = new TaskCompletionSource<bool>(); int starts = 0;
            var warm = new WordWarmup(() => { Interlocked.Increment(ref starts); return gate.Task; }, () => Task.FromResult(true));
            Task first = warm.EnsureAsync(), go = warm.EnsureAsync();
            Assert.AreEqual(WordWarmupState.Warming, warm.State); Assert.AreSame(first, go);
            gate.SetResult(true); go.GetAwaiter().GetResult();
            Assert.AreEqual(1, starts); Assert.AreEqual(WordWarmupState.Ready, warm.State);
            warm.EnsureAsync().GetAwaiter().GetResult(); Assert.AreEqual(1, starts);
        }
        [TestMethod]
        public void FailureIsObservedWithoutAutomaticRetryAndGoCanFallBack()
        {
            int starts = 0;
            var warm = new WordWarmup(() => { starts++; return Task.FromException(new InvalidOperationException("activation")); }, () => Task.FromResult(false));
            Assert.ThrowsException<InvalidOperationException>(() => warm.EnsureAsync().GetAwaiter().GetResult());
            Assert.AreEqual(WordWarmupState.Failed, warm.State);
            warm.CheckAsync().GetAwaiter().GetResult(); Assert.AreEqual(1, starts);
            // Go observes the failure and uses the same controller's ordinary OpenDocument path.
            Assert.ThrowsException<InvalidOperationException>(() => warm.EnsureAsync().GetAwaiter().GetResult());
            Assert.AreEqual(1, starts);
        }
        [TestMethod]
        public void ClosedOwnedApplicationWarmsAgainAndHealthyApplicationIsReused()
        {
            int starts = 0; bool alive = false;
            var warm = new WordWarmup(() => { starts++; alive = true; return Task.CompletedTask; }, () => Task.FromResult(alive));
            warm.EnsureAsync().GetAwaiter().GetResult(); warm.CheckAsync().GetAwaiter().GetResult(); Assert.AreEqual(1, starts);
            alive = false; warm.CheckAsync().GetAwaiter().GetResult();
            Assert.AreEqual(2, starts); Assert.AreEqual(WordWarmupState.Ready, warm.State);
        }
    }
}
