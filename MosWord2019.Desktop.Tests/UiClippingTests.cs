using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019.Core.Interfaces;
using MosWord2019.Core.Models;
using MosWord2019.Core.Services;
using MosWord2019.Projects;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class UiClippingTests
    {
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        internal static float GraphicsScale { get { using (var graphics = Graphics.FromHwnd(IntPtr.Zero)) return graphics.DpiY / 96f; } }
        internal static IntPtr EnterDpiContext() => SetThreadDpiAwarenessContext(new IntPtr(-4));
        internal static void RestoreDpiContext(IntPtr previous) { if (previous != IntPtr.Zero) SetThreadDpiAwarenessContext(previous); }
        [DataTestMethod]
        [DataRow("en", 1f)] [DataRow("vi", 1f)]
        [DataRow("en", 1.25f)] [DataRow("vi", 1.25f)]
        [DataRow("en", 1.5f)] [DataRow("vi", 1.5f)]
        [DataRow("en", 1.75f)] [DataRow("vi", 1.75f)]
        public void AllVisibleTextAndBarSiblingsFit(string language, float scale)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                IntPtr previous = EnterDpiContext();
                try
                {
                    foreach (string screen in new[] { "Login", "Testing", "Training", "Result" })
                    foreach (int width in new[] { 1366, 1920 })
                    using (var surface = new Surface(screen, language))
                    {
                        Form form = surface.Form; form.Show(); Application.DoEvents();
                        SuspendArrangement(form);
                        form.AutoScaleMode = AutoScaleMode.None;
                        ScaleFonts(form, scale);
                        if (screen != "Login") form.Size = new Size(width, screen == "Result" ? 700 : UiLayout.PanelHeight(form, new Rectangle(0, 0, width, 728)));
                        form.PerformLayout(); Application.DoEvents();
                        CheckText(form); CheckBars(form); CheckAncestors(form);
                        if (screen == "Login")
                        {
                            ((Button)form.Controls.Find("Continue", true).Single()).PerformClick();
                            Application.DoEvents(); CheckText(form); CheckBars(form); CheckAncestors(form);
                        }
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally { RestoreDpiContext(previous); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        [DataTestMethod]
        [DataRow("Training")] [DataRow("Testing")]
        public void StatusAppearingAtLargeFontKeepsQuestionVisible(string screen)
        {
            Exception failure = null;
            var thread = new Thread(() =>
            {
                IntPtr previous = EnterDpiContext();
                try
                {
                    using (var surface = new Surface(screen, "vi"))
                    {
                        var form = surface.Form; form.Show(); Application.DoEvents();
                        SuspendArrangement(form); form.AutoScaleMode = AutoScaleMode.None; ScaleFonts(form, 1.75f);
                        var area = Screen.FromControl(form).WorkingArea;
                        form.Size = new Size(area.Width, UiLayout.PanelHeight(form, area)); Application.DoEvents();
                        form.GetType().GetField("arranging", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, false);
                        var status = (Label)form.Controls.Find(screen == "Testing" ? "ExamStatus" : "Status", true).Single();
                        status.Text = "Hãy đóng hộp thoại đang mở trong Word rồi bấm lại.";
                        Application.DoEvents(); CheckText(form); CheckBars(form); CheckAncestors(form);
                    }
                }
                catch (Exception ex) { failure = ex; }
                finally { RestoreDpiContext(previous); }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        private static void ScaleFonts(Control root, float scale)
        {
            // Snapshot inherited fonts first; changing a parent must not multiply a child's scale twice.
            var controls = Descendants(root).Prepend(root).ToArray();
            var fonts = controls.Select(c => new Font(c.Font.FontFamily, c.Font.Size * scale, c.Font.Style)).ToArray();
            for (int i = 0; i < controls.Length; i++) controls[i].Font = fonts[i];
        }
        private static System.Collections.Generic.IEnumerable<Control> Descendants(Control root)
        {
            foreach (Control child in root.Controls) { yield return child; foreach (var c in Descendants(child)) yield return c; }
        }
        private static void CheckText(Control root)
        {
            foreach (Control c in Descendants(root).Where(c => c.Visible && !string.IsNullOrEmpty(c.Text)))
            {
                if (c is TabPage) continue; // Headers are checked on the owning native tab strip below.
                if (!(c is Label || c is Button || c is CheckBox || c is RadioButton)) continue;
                var available = c.ClientSize - c.Padding.Size;
                if (c is CheckBox || c is RadioButton) available.Width -= SystemInformation.MenuCheckSize.Width + 3;
                var flags = TextFormatFlags.NoPrefix;
                if (c is Label && ((Label)c).MaximumSize.Width > 0) flags |= TextFormatFlags.WordBreak;
                Size text = TextRenderer.MeasureText(c.Text, c.Font, new Size(Math.Max(1, available.Width), int.MaxValue), flags);
                Assert.IsTrue(text.Width <= available.Width && text.Height <= available.Height,
                    root.GetType().Name + "/" + c.Name + " [" + c.Text + "] text=" + text + " available=" + available);
            }
            foreach (var tabs in Descendants(root).OfType<TabControl>().Where(t => t.Visible))
            for (int i = 0; i < tabs.TabCount; i++)
            {
                Rectangle rect = tabs.GetTabRect(i);
                if (!tabs.ClientRectangle.Contains(rect)) continue; // Offscreen native overflow headers are not visible.
                Size text = TextRenderer.MeasureText(tabs.TabPages[i].Text, tabs.Font);
                Assert.IsTrue(text.Width <= rect.Width && text.Height <= rect.Height, "Tab header clipped: " + tabs.TabPages[i].Text);
            }
        }
        private static void CheckAncestors(Form form)
        {
            foreach (Control control in Descendants(form).Where(c => c.Visible && (c is Label || c is Button || c is CheckBox || c is RadioButton)))
            {
                var bounds = control.RectangleToScreen(control.ClientRectangle);
                for (Control ancestor = control.Parent; ancestor != null; ancestor = ancestor.Parent)
                    Assert.IsTrue(ancestor.RectangleToScreen(ancestor.ClientRectangle).Contains(bounds), control.Name + " clipped by ancestor " + ancestor.Name);
            }
            foreach (var tabs in Descendants(form).OfType<TabControl>().Where(c => c.Visible && c.TabCount > 0))
            {
                Assert.IsTrue(tabs.ClientSize.Height > tabs.ItemSize.Height, "Question viewport hidden by bars.");
                var question = tabs.SelectedTab.Controls.OfType<RichTextBox>().First();
                Assert.IsTrue(question.ClientSize.Height >= question.Font.Height, "A complete question line must remain visible.");
            }
        }
        private static void CheckBars(Control root)
        {
            foreach (Control bar in Descendants(root).Where(c => c.Name.Contains("Bar") || c.Name.Contains("Footer"))) CheckSiblings(bar);
        }
        private static void CheckSiblings(Control parent)
        {
            var children = parent.Controls.Cast<Control>().Where(c => c.Visible).ToArray();
            for (int i = 0; i < children.Length; i++)
            {
                Assert.IsTrue(parent.ClientRectangle.Contains(children[i].Bounds), children[i].Name + " outside " + parent.Name);
                for (int j = i + 1; j < children.Length; j++)
                    Assert.IsFalse(children[i].Bounds.IntersectsWith(children[j].Bounds), "Overlapping bar siblings: " + children[i].Name + "/" + children[j].Name);
                CheckSiblings(children[i]);
            }
        }
        private static void SuspendArrangement(Form form)
        {
            form.GetType().GetField("arranging", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(form, true);
        }
        // Standalone STA evidence host uses the actual production forms, never Program or Word.
        public static void RunRealEvidence(string screen, string language, string folder)
        {
            using (var surface = new Surface(screen, language))
            {
                var form = surface.Form;
                form.Shown += (s, e) =>
                {
                    var area = Screen.FromControl(form).Bounds;
                    File.WriteAllText(Path.Combine(folder, "ready.json"), "{\"screen\":\"" + screen + "\",\"language\":\"" + language +
                        "\",\"width\":" + area.Width + ",\"height\":" + area.Height + ",\"dpi\":" + form.DeviceDpi + "}");
                };
                Application.Run(form);
            }
        }
        private sealed class Surface : IDisposable
        {
            private readonly TestWorkspace folder = new TestWorkspace();
            internal Form Form { get; }
            internal Surface(string screen, string language)
            {
                if (screen == "Login")
                {
                    Form = new LoginForm(new OfflineAuthenticationService(), null, _ => { });
                    ((RadioButton)Form.Controls.Find(language == "vi" ? "Vietnamese" : "English", true).Single()).Checked = true; return;
                }
                var packages = new ProjectLoader().LoadAll(TestCaseData.ProjectsRoot, language);
                if (screen == "Training")
                {
                    string empty = Path.Combine(folder.Folder, "Projects"); Directory.CreateDirectory(empty);
                    var workspace = new TrainingWorkspaceService(Path.Combine(folder.Folder, "Working"), Path.Combine(folder.Folder, "Documents"), Path.Combine(folder.Folder, "Pictures"));
                    var form = new MainForm(new AppSession(AppMode.Training, language), empty, workspace, new NoWord(), notify: _ => { });
                    var package = packages.Single(p => p.Meta.ProjectId == "Word2019_P10");
                    var selector = (ComboBox)form.Controls.Find("Projects", true).Single(); selector.Items.Add(package); selector.SelectedIndex = 0;
                    typeof(MainForm).GetField("currentProject", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(form, package);
                    typeof(MainForm).GetMethod("BuildTaskTabs", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
                    typeof(MainForm).GetMethod("ShowTask", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, new object[] { 0 });
                    Form = form; return;
                }
                var prepared = new TestingSessionStateStore(Path.Combine(folder.Folder, "state.json")).Prepare(Path.Combine(TestCaseData.ProjectsRoot, "testing-catalog.json"), packages, language);
                if (screen == "Result") { Form = new ResultForm(new ExamScorer(_ => { }).Score(prepared.Definition, new ExamTaskGrade[0]), packages, language); return; }
                var testing = new TestingForm(prepared, packages);
                testing.Shown += (s, e) => testing.ApplyState(new TestingSessionSnapshot(TestingSessionStatus.Running, TestingFinalizeReason.None, 0, TimeSpan.Zero,
                    Enumerable.Repeat(TestingProjectState.Active, 7), new ExamTaskGrade[0], null)); Form = testing;
            }
            public void Dispose() { Form.Dispose(); folder.Dispose(); }
        }
        private sealed class NoWord : IWordController
        {
            public bool IsOpened => false;
            public void StartWord() { } public void ReleaseClosedApplication() { } public void OpenDocument(string path) { }
            public void Save() { } public void CloseDocument() { } public void Close() { } public void Dispose() { }
        }
    }
}
