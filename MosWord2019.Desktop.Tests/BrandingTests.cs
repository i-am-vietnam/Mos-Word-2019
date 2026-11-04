using System;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class BrandingTests
    {
        private static void Sta(Action test)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { test(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)));
            if (failure != null) throw failure;
        }

        [TestMethod]
        public void LoginFooterUsesAssemblyProductVersionInBothLanguages() => Sta(() =>
        {
            var assembly = typeof(LoginForm).Assembly;
            string version = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>().InformationalVersion;
            Assert.AreEqual("1.0.0", version);
            Assert.AreEqual(new Version(version + ".0"), assembly.GetName().Version);
            using (var form = new LoginForm(new OfflineAuthenticationService(), null, _ => Assert.Fail("Must not open a session.")))
            {
                form.Show();
                var footer = form.Controls.Find("ProductVersion", true).OfType<Label>().Single();
                Assert.AreEqual("v" + version, footer.Text);
                Assert.IsTrue(footer.Visible);
                var layout = (TableLayoutPanel)footer.Parent;
                Assert.AreEqual(layout.RowCount - 1, layout.GetRow(footer));
                form.Controls.Find("Vietnamese", true).OfType<RadioButton>().Single().Checked = true;
                Assert.AreEqual("v" + version, footer.Text);
                Assert.IsTrue(form.ClientRectangle.Contains(form.RectangleToClient(footer.RectangleToScreen(footer.ClientRectangle))));
            }
        });

        [TestMethod]
        public void SharedFormIconMatchesExecutableAndSurvivesAnotherFormDisposal() => Sta(() =>
        {
            using (var executable = Icon.ExtractAssociatedIcon(typeof(LoginForm).Assembly.Location))
            using (var first = new Form())
            using (var second = new Form())
            {
                UiLayout.ScaleForm(first);
                UiLayout.ApplyIcon(second);
                Assert.AreSame(first.Icon, second.Icon);
                first.Dispose();
                using (var actual = second.Icon.ToBitmap())
                using (var expected = executable.ToBitmap())
                {
                    Assert.AreEqual(expected.Size, actual.Size);
                    for (int y = 0; y < expected.Height; y++)
                        for (int x = 0; x < expected.Width; x++) Assert.AreEqual(expected.GetPixel(x, y), actual.GetPixel(x, y));
                }
            }
        });
    }
}
