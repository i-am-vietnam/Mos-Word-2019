using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MosWord2019.Word
{
    internal static class WinApiProcessHelper
    {
        private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder text, int count);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);
        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr window, int command);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);

        internal static HashSet<int> SnapshotWordProcessIds()
        {
            var result = new HashSet<int>();
            // Exclusion snapshot only. Never use this list as termination targets.
            foreach (Process process in Process.GetProcessesByName("WINWORD"))
                using (process) result.Add(process.Id);
            return result;
        }

        internal static OwnedProcess Capture(string uniqueCaption, HashSet<int> preexisting, DateTime activationUtc)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < 5000)
            {
                int found = 0;
                bool ambiguous = false;
                if (!EnumWindows((window, parameter) =>
                {
                    var title = new StringBuilder(512);
                    var className = new StringBuilder(64);
                    GetWindowText(window, title, title.Capacity);
                    GetClassName(window, className, className.Capacity);
                    if (className.ToString() == "OpusApp" && title.ToString() == uniqueCaption)
                    {
                        uint pid;
                        GetWindowThreadProcessId(window, out pid);
                        if (found != 0 && found != (int)pid) ambiguous = true;
                        found = (int)pid;
                    }
                    return true;
                }, IntPtr.Zero)) throw new Win32Exception(Marshal.GetLastWin32Error());

                if (ambiguous) throw new InvalidOperationException("Word ownership is ambiguous; no process may be terminated.");
                if (found != 0)
                {
                    if (preexisting.Contains(found))
                        throw new OwnershipRejectedException("Word activation resolved to a pre-existing process; ownership refused.");
                    // Keep this kernel handle for the entire session. Never reopen by PID to kill.
                    SafeProcessHandle handle = OpenProcess(0x00100000 | 0x1000 | 0x0001, false, found);
                    try
                    {
                        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                        var path = new StringBuilder(32768);
                        int length = path.Capacity;
                        if (!QueryFullProcessImageName(handle, 0, path, ref length))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        long created, exited, kernel, user;
                        if (!GetProcessTimes(handle, out created, out exited, out kernel, out user))
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                        if (!string.Equals(Path.GetFileName(path.ToString()), "WINWORD.EXE", StringComparison.OrdinalIgnoreCase) ||
                            DateTime.FromFileTimeUtc(created) < activationUtc)
                            throw new OwnershipRejectedException("The Word window does not identify a newly created WINWORD process.");
                        var result = new OwnedProcess(found, handle);
                        handle = null;
                        return result;
                    }
                    finally { if (handle != null) handle.Dispose(); }
                }
                Thread.Sleep(50);
            }
            throw new InvalidOperationException("Could not identify the newly created Word window. No process termination is permitted.");
        }

        internal sealed class OwnedProcess : IDisposable
        {
            private readonly SafeProcessHandle handle;
            internal int Id { get; }
            internal OwnedProcess(int id, SafeProcessHandle handle) { Id = id; this.handle = handle; }

            internal void PositionWindow(IntPtr window, int left, int top, int width, int height)
            {
                if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
                uint pid;
                GetWindowThreadProcessId(window, out pid);
                // The retained handle proves the original process is still alive (no PID reuse).
                // The window comes from our held Document, never a global Word-window search.
                if (window == IntPtr.Zero || pid != Id || WaitForExit(0))
                    throw new InvalidOperationException("The document window is not owned by this live Word session.");
                ShowWindow(window, 9); // Restore a maximized/minimized window before setting bounds.
                GetWindowThreadProcessId(window, out pid);
                if (pid != Id || WaitForExit(0)) throw new InvalidOperationException("Word window ownership changed.");
                if (!SetWindowPos(window, IntPtr.Zero, left, top, width, height, 0x0004 | 0x0010))
                    throw new Win32Exception(Marshal.GetLastWin32Error()); // No Z-order/focus change.
            }

            internal bool WaitForExit(uint milliseconds)
            {
                uint result = WaitForSingleObject(handle, milliseconds);
                if (result == 0) return true;
                if (result == 258) return false;
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            internal void EnsureExited()
            {
                if (WaitForExit(3000)) return;
                if (!TerminateProcess(handle, 1))
                {
                    int error = Marshal.GetLastWin32Error();
                    // TerminateProcess can deny access once natural exit has already begun.
                    // Wait for that exit to finish before reporting a cleanup failure.
                    if (WaitForExit(3000)) return;
                    throw new Win32Exception(error, "Could not stop the verified owned Word process.");
                }
                if (!WaitForExit(3000)) throw new TimeoutException("The owned Word process did not exit after termination.");
            }

            public void Dispose() { handle.Dispose(); }
        }

        internal sealed class OwnershipRejectedException : InvalidOperationException
        {
            internal OwnershipRejectedException(string message) : base(message) { }
        }
    }
}
