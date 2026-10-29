using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Services;
using System;
using System.IO;

namespace MosWord2019.Tests
{
    [TestClass]
    public class TemplateFolderResolverTests
    {
        [TestMethod]
        public void SharedResolverIsAvailableBeforeWordOpens()
        {
            Assert.IsNotNull(typeof(TemplateOutputLocation).Assembly.GetType(
                "MosWord2019.Core.Services.TemplateFolderResolver"),
                "Template paths need a shared resolver usable before Word opens.");
            var resolve = typeof(TemplateFolderResolver).GetMethod("Resolve", new[] { typeof(string) });
            Assert.IsNotNull(resolve);
            Assert.AreEqual("16.0", resolve.GetParameters()[0].DefaultValue);
        }

        [TestMethod]
        public void RedirectedFolderAndOfficeVersionUseTheExactPersonalTemplatesKey()
        {
            string actualKey = null;
            string folder = TemplateFolderResolver.Resolve("15.0", key =>
            {
                actualKey = key;
                return @"D:\Redirected\Templates";
            }, @"C:\IsolatedDocuments");
            Assert.AreEqual(@"Software\Microsoft\Office\15.0\Word\Options", actualKey);
            Assert.AreEqual(@"D:\Redirected\Templates", folder);
            Assert.AreEqual(@"D:\Redirected\Templates\Notes.dotx", TemplateOutputLocation.ResolveInFolder("Notes.dotx", folder));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("  \t")]
        public void UnsetSettingUsesTheInjectedDocumentsRoot(string configured)
        {
            Assert.AreEqual(@"C:\IsolatedDocuments\Custom Office Templates",
                TemplateFolderResolver.Resolve("16.0", _ => configured, @"C:\IsolatedDocuments"));
            Assert.AreEqual(@"C:\IsolatedDocuments\Custom Office Templates\Notes.dotx",
                TemplateOutputLocation.Resolve("Notes.dotx", @"C:\IsolatedDocuments"));
        }

        [TestMethod]
        public void EnvironmentExpansionAndTrailingSeparatorsAreNormalized()
        {
            const string variable = "MOSWORD_A3A_TEMPLATE_ROOT_TEST";
            string previous = Environment.GetEnvironmentVariable(variable, EnvironmentVariableTarget.Process);
            try
            {
                Environment.SetEnvironmentVariable(variable, @"C:\Redirected", EnvironmentVariableTarget.Process);
                Assert.AreEqual(@"C:\Redirected\Templates",
                    TemplateFolderResolver.Resolve("16.0", _ => "%" + variable + @"%\Templates\", @"C:\IsolatedDocuments"));
            }
            finally
            {
                Environment.SetEnvironmentVariable(variable, previous, EnvironmentVariableTarget.Process);
            }
        }

        [DataTestMethod]
        [DataRow("relative\\Templates")]
        [DataRow("C:Templates")]
        [DataRow("\\Templates")]
        [DataRow("C:\\Bad|Folder")]
        [DataRow("C:\\Bad\0Folder")]
        public void MalformedConfiguredFoldersFailClosed(string configured)
        {
            Assert.ThrowsException<InvalidDataException>(() =>
                TemplateFolderResolver.Resolve("16.0", _ => configured, @"C:\IsolatedDocuments"));
            Assert.ThrowsException<InvalidDataException>(() => TemplateOutputLocation.ResolveInFolder("Notes.dotx", configured));
        }

        [DataTestMethod]
        [DataRow(null)]
        [DataRow("")]
        [DataRow("..\\Notes.dotx")]
        [DataRow("nested/Notes.dotx")]
        [DataRow("C:\\Notes.dotx")]
        [DataRow("Notes.dotm")]
        [DataRow("Notes?.dotx")]
        public void OutputNameMustBeASingleRootLevelDotx(string fileName)
        {
            Assert.ThrowsException<InvalidDataException>(() => TemplateOutputLocation.ResolveInFolder(fileName, @"C:\Templates"));
        }

        [TestMethod]
        public void RootFolderAndUppercaseExtensionRemainValid()
        {
            Assert.AreEqual(@"C:\Notes.DOTX", TemplateOutputLocation.ResolveInFolder("Notes.DOTX", @"C:\"));
        }
    }
}
