using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MosWord2019;
using MosWord2019.Projects.OnlineSessions;

namespace MosWord2019.Tests
{
    [TestClass]
    public sealed class OnlineLoginTests
    {
        private sealed class Authentication : IOnlineAuthenticationService
        {
            public string LastErrorCode { get; set; }
            public OnlineSessionCredentials Credentials { get; private set; }
            public int Logins, Modes, Cancels;
            public TaskCompletionSource<bool> Pending;
            public bool Authenticate(string username, string password) => throw new AssertFailedException("Online UI must use asynchronous authentication.");
            public async Task<bool> AuthenticateAsync(string username, string password)
            {
                Logins++;
                if (Pending != null) await Pending.Task;
                if (LastErrorCode != null) return false;
                Credentials = new OnlineSessionCredentials { SessionId = Guid.NewGuid(), RemainingUses = 2 };
                return true;
            }
            public Task<OnlineSessionContext> SelectModeAsync(AppMode mode, string language)
            { Modes++; Credentials.Mode = mode.ToString(); return Task.FromResult<OnlineSessionContext>(null); }
            public Task CancelAsync() { Cancels++; return Task.CompletedTask; }
        }
        private static T Find<T>(Control root, string name) where T : Control => root.Controls.Find(name, true).OfType<T>().Single();
        private static void Sta(Action test)
        {
            Exception failure = null;
            var thread = new Thread(() => { try { test(); } catch (Exception ex) { failure = ex; } });
            thread.SetApartmentState(ApartmentState.STA); thread.Start();
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(15)), "The UI operation must finish without blocking the message pump.");
            if (failure != null) throw failure;
        }
        private static void Until(Func<bool> done)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && timer.Elapsed < TimeSpan.FromSeconds(5)) { Application.DoEvents(); Thread.Sleep(5); }
            Assert.IsTrue(done());
        }
        [TestMethod]
        public void SuccessfulLoginShowsRemainingUsesBeforeModeOrShellAndCannotDoubleLogin() => Sta(() =>
        {
            var auth = new Authentication { Pending = new TaskCompletionSource<bool>() };
            int opened = 0; AppSession selected = null;
            using (var form = new LoginForm(auth, null, session => { opened++; selected = session; }))
            {
                form.Show();
                Find<TextBox>(form, "Username").Text = "learner"; Find<TextBox>(form, "Password").Text = "secret";
                Assert.IsFalse(Find<RadioButton>(form, "Testing").Enabled);
                var enter = Find<Button>(form, "Continue"); enter.PerformClick(); enter.PerformClick();
                Assert.AreEqual(1, auth.Logins); Assert.AreEqual(0, auth.Modes); Assert.AreEqual(0, opened);
                Assert.IsFalse(enter.Enabled);
                auth.Pending.SetResult(true);
                Until(() => enter.Enabled);
                Assert.AreEqual("", Find<TextBox>(form, "Password").Text);
                StringAssert.Contains(Find<Label>(form, "LoginStatus").Text, "2");
                Assert.IsFalse(Find<TextBox>(form, "Username").Enabled);
                Find<RadioButton>(form, "Testing").Checked = true;
                enter.PerformClick(); Until(() => opened == 1);
                Assert.AreEqual(1, auth.Logins); Assert.AreEqual(1, auth.Modes); Assert.AreEqual(AppMode.Testing, selected.Mode);
            }
        });
        [DataTestMethod]
        [DataRow("invalid_credentials", "try again later", "thử lại sau")]
        [DataRow("no_uses_left", "No uses left", "hết lượt")]
        [DataRow("account_expired", "expired", "hết hạn")]
        [DataRow("account_locked", "administrator", "quản trị viên")]
        [DataRow("certificate_mismatch", "certificate mismatch", "không khớp")]
        [DataRow("server_unreachable", "unreachable", "Không kết nối")]
        public void FailedLoginIsLocalizedAndNeverSelectsModeOrOpensWord(string error, string english, string vietnamese) => Sta(() =>
        {
            var auth = new Authentication { LastErrorCode = error }; int opened = 0;
            using (var form = new LoginForm(auth, null, _ => opened++))
            {
                form.Show(); Find<Button>(form, "Continue").PerformClick();
                Until(() => auth.Logins == 1);
                StringAssert.Contains(Find<Label>(form, "LoginStatus").Text, english);
                Find<RadioButton>(form, "Vietnamese").Checked = true;
                StringAssert.Contains(Find<Label>(form, "LoginStatus").Text, vietnamese);
                Assert.AreEqual(0, auth.Modes); Assert.AreEqual(0, opened);
            }
        });
        [TestMethod]
        public void ReleaseTrustCannotBeOverriddenByDevelopmentConfiguration()
        {
            string otherPin = Convert.ToBase64String(Enumerable.Repeat((byte)0x71, 32).ToArray());
            var settings = new ServerConnectionSettings("https://untrusted.invalid/", otherPin);
#if DEBUG
            Assert.AreEqual("untrusted.invalid", settings.Origin.Host);
            Assert.AreEqual(otherPin, settings.Pin);
            Assert.IsNotNull(typeof(LoginForm).Assembly.GetType("MosWord2019.OfflineAuthenticationService"));
#else
            Assert.AreEqual(CompiledOrigin(), settings.Origin.AbsoluteUri);
            Assert.AreEqual((string)typeof(LoginForm).Assembly.GetType("MosWord2019.CompiledServerSettings")
                .GetField("SpkiPin", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetRawConstantValue(), settings.Pin);
            Assert.IsNull(typeof(LoginForm).Assembly.GetType("MosWord2019.OfflineAuthenticationService"));
#endif
        }
        [TestMethod]
        public void OnlineClockUsesServerUtcRatherThanTheWindowsWallClock()
        {
            var authority = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);
            var clock = new ServerSessionClock(authority, () => authority);
            Assert.AreEqual(authority.UtcDateTime, clock.UtcNow);
            authority = authority.AddMinutes(2);
            Assert.AreEqual(authority.UtcDateTime, clock.UtcNow);
            authority = authority.AddMinutes(-1);
            Assert.AreEqual(new DateTime(2001, 2, 3, 4, 7, 6, DateTimeKind.Utc), clock.UtcNow);
        }
        [TestMethod]
        public void OnlineFinalizationTimestampCannotPassTheServerHardCap()
        {
            var selected = new DateTimeOffset(2001, 2, 3, 4, 5, 6, TimeSpan.Zero);
            var clock = new ServerSessionClock(selected.AddHours(3), ceiling: selected.AddHours(2));
            Assert.AreEqual(selected.AddHours(2).UtcDateTime, clock.UtcNow);
            Assert.IsTrue(clock.Monotonic >= TimeSpan.Zero);
        }
#if !DEBUG
        private static string CompiledOrigin() => (string)typeof(LoginForm).Assembly.GetType("MosWord2019.CompiledServerSettings")
            .GetField("Origin", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetRawConstantValue();
#endif
    }
}
