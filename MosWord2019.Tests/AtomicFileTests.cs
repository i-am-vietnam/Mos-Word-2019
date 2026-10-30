using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.IO;
namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class AtomicFileTests
    {
        [DataTestMethod]
        [DataRow(32, false)] [DataRow(33, true)] [DataRow(1175, true)] [DataRow(1176, false)] [DataRow(1177, true)]
        public void TransientFilesystemFailureRetriesThenCommits(int code, bool replace)
        {
            int calls = 0, validations = 0; var delays = new List<int>(); var logs = new List<string>();
            AtomicFile.Commit("source", "destination", replace, (s, d, r) => { Assert.AreEqual(replace, r); if (++calls < 4) throw new NativeIOException(code); },
                _ => true, delays.Add, () => validations++, logs.Add);
            Assert.AreEqual(4, calls); Assert.AreEqual(3, delays.Count); Assert.AreEqual(4, validations); Assert.AreEqual(0, logs.Count);
        }
        [DataTestMethod]
        [DataRow(32)] [DataRow(33)] [DataRow(1175)] [DataRow(1176)] [DataRow(1177)]
        public void PersistentFailureHasTenAttemptBudgetAndOneSummary(int code)
        {
            int calls = 0, waited = 0; var logs = new List<string>();
            Assert.ThrowsException<NativeIOException>(() => AtomicFile.Commit("source", "destination", true,
                (s, d, r) => { calls++; throw new NativeIOException(code); }, _ => true, ms => waited += ms, () => { }, logs.Add));
            Assert.AreEqual(10, calls); Assert.IsTrue(waited >= 1800 && waited <= 2100); Assert.AreEqual(1, logs.Count);
        }
        [DataTestMethod]
        [DataRow(5, true)] [DataRow(183, true)] [DataRow(1175, false)]
        public void NonTransientOrConsumedSourceMustNotRetry(int code, bool sourceExists)
        {
            int calls = 0; var logs = new List<string>();
            Assert.ThrowsException<NativeIOException>(() => AtomicFile.Commit("source", "destination", false,
                (s, d, r) => { calls++; throw new NativeIOException(code); }, _ => sourceExists, _ => Assert.Fail("Unsafe retry"), () => { }, logs.Add));
            Assert.AreEqual(1, calls); Assert.AreEqual(1, logs.Count);
        }
        [TestMethod]
        public void RealMoveNeverOverwritesAnExistingFile()
        {
            using (var lab = new TestWorkspace())
            {
                string source = Path.Combine(lab.Folder, "source"), target = Path.Combine(lab.Folder, "target");
                File.WriteAllText(source, "new"); File.WriteAllText(target, "personal");
                Assert.ThrowsException<IOException>(() => AtomicFile.Commit(source, target, false, log: _ => { }));
                Assert.AreEqual("personal", File.ReadAllText(target)); Assert.AreEqual("new", File.ReadAllText(source));
            }
        }
        private sealed class NativeIOException : IOException
        { internal NativeIOException(int code) : base("Injected filesystem error") { HResult = unchecked((int)0x80070000) | code; } }
    }
}
