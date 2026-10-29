using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Projects;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class ApplicationLifetimeTests
    {
        [TestMethod]
        public void SingleInstanceLockRejectsAnotherThreadAndReleasesAfterExit()
        {
            string name = @"Local\MosWord2019.Tests." + Guid.NewGuid().ToString("N");
            using (var first = new TrainerInstanceLock(name))
            {
                Assert.IsTrue(first.Acquired);
                Assert.IsFalse(Task.Run(() => { using (var second = new TrainerInstanceLock(name)) return second.Acquired; }).Result);
            }
            using (var next = new TrainerInstanceLock(name)) Assert.IsTrue(next.Acquired);
        }

        [TestMethod]
        public void AbandonedInstanceCanBeRecovered()
        {
            string name = @"Local\MosWord2019.Tests." + Guid.NewGuid().ToString("N");
            Mutex abandoned = null;
            var thread = new Thread(() => { abandoned = new Mutex(false, name); abandoned.WaitOne(); });
            thread.Start();
            Assert.IsTrue(thread.Join(5000));
            using (var next = new TrainerInstanceLock(name)) Assert.IsTrue(next.Acquired);
            abandoned.Dispose();
        }

        [DataTestMethod]
        [DataRow("none")]
        [DataRow("close")]
        [DataRow("working")]
        [DataRow("recovery")]
        public void FatalRecoveryAttemptsSafeStepsAndLogsFailures(string failingStep)
        {
            var calls = new List<string>();
            var errors = new List<Exception>();
            Action<string> step = name => { calls.Add(name); if (name == failingStep) throw new InvalidOperationException(name); };
            FatalRecovery.Run(() => step("close"), () => step("working"), () => step("recovery"), errors.Add);
            CollectionAssert.AreEqual(failingStep == "close" ? new[] { "close", "recovery" } : new[] { "close", "working", "recovery" }, calls);
            Assert.AreEqual(failingStep == "none" ? 0 : 1, errors.Count);
            if (errors.Count != 0) Assert.AreEqual(failingStep, errors[0].Message);
        }
    }
}
