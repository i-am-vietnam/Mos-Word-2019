using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Models;
using MosWord2019.Word;

namespace MosWord2019.Desktop.Tests
{
    [TestClass]
    public sealed class OwnedWordRecoveryTests
    {
        [DataTestMethod]
        [DataRow("match", true)]
        [DataRow("pid", false)]
        [DataRow("created", false)]
        [DataRow("image", false)]
        [DataRow("changed", false)]
        [DataRow("absent", false)]
        [DataRow("machine", false)]
        public void RecoveryTerminatesOnlyReverifiedIdentityOnTheSameHandle(string variant, bool expectedStop)
        {
            var expected = new OwnedWordProcessIdentity(123, 100000, @"C:\Office\WINWORD.EXE");
            var wrong = new OwnedWordProcessIdentity(variant == "pid" ? 124 : 123, variant == "created" ? 100001 : 100000,
                variant == "image" ? @"C:\Office\EXCEL.EXE" : @"C:\Other\WINWORD.EXE");
            var fake = new Fake { First = variant == "match" || variant == "changed" ? expected : wrong,
                Second = variant == "changed" ? wrong : expected };
            int opens = 0;
            var recovery = new OwnedWordProcessRecovery(pid => { opens++; Assert.AreEqual(123, pid); return variant == "absent" ? null : fake; });
            Assert.AreEqual(expectedStop, recovery.Stop(expected, variant == "machine" ? "different-machine" : Environment.MachineName));
            Assert.AreEqual(expectedStop, fake.Terminated);
            if (expectedStop) Assert.AreEqual(2, fake.Reads);
            if (variant == "machine") Assert.AreEqual(0, opens);
            else if (variant != "absent") Assert.IsTrue(fake.Disposed);
        }
        private sealed class Fake : IRecoveredWordProcess
        {
            internal OwnedWordProcessIdentity First, Second;
            internal int Reads; internal bool Terminated, Disposed;
            public OwnedWordProcessIdentity ReadIdentity() => ++Reads == 1 ? First : Second;
            public void Terminate() { Assert.AreEqual(2, Reads); Terminated = true; }
            public void Dispose() { Disposed = true; }
        }
    }
}
