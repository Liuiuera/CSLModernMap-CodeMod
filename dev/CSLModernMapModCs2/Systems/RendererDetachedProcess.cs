using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace CSLModernMap.Systems
{
    /// <summary>通过桌面进程独立启动查看器</summary>
    internal static class RendererDetachedProcess
    {
        private const int ProcessCreateProcess = 0x0080;
        private const int ExtendedStartupInfoPresent = 0x00080000;
        private const int ParentProcessAttribute = 0x00020000;

        internal static int Start(string executable, string arguments, string workingDirectory)
        {
            var shellWindow = GetShellWindow();
            GetWindowThreadProcessId(shellWindow, out var shellProcessId);
            if (shellWindow == IntPtr.Zero || shellProcessId == 0)
            {
                throw new InvalidOperationException("Windows desktop shell is unavailable.");
            }

            var parent = OpenProcess(ProcessCreateProcess, false, shellProcessId);
            if (parent == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var attributes = IntPtr.Zero;
            var parentValue = IntPtr.Zero;
            var initialized = false;
            try
            {
                var size = IntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
                if (size == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                attributes = Marshal.AllocHGlobal(size);
                if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                initialized = true;
                parentValue = Marshal.AllocHGlobal(IntPtr.Size);
                Marshal.WriteIntPtr(parentValue, parent);
                if (!UpdateProcThreadAttribute(attributes, 0,
                    (IntPtr)ParentProcessAttribute, parentValue, (IntPtr)IntPtr.Size,
                    IntPtr.Zero, IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                var startup = new StartupInfoEx
                {
                    StartupInfo = new StartupInfo { Size = Marshal.SizeOf(typeof(StartupInfoEx)) },
                    AttributeList = attributes,
                };
                var commandLine = new StringBuilder("\"" + executable + "\" " + arguments);
                if (!CreateProcess(executable, commandLine, IntPtr.Zero, IntPtr.Zero,
                    false, ExtendedStartupInfoPresent, IntPtr.Zero, workingDirectory,
                    ref startup, out var process))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                CloseHandle(process.Thread);
                CloseHandle(process.Process);
                return process.ProcessId;
            }
            finally
            {
                if (initialized)
                {
                    DeleteProcThreadAttributeList(attributes);
                }

                if (parentValue != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(parentValue);
                }

                if (attributes != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(attributes);
                }

                CloseHandle(parent);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfo
        {
            internal int Size;
            private IntPtr m_Reserved;
            private IntPtr m_Desktop;
            private IntPtr m_Title;
            private int m_X;
            private int m_Y;
            private int m_Width;
            private int m_Height;
            private int m_XChars;
            private int m_YChars;
            private int m_Fill;
            private int m_Flags;
            private short m_ShowWindow;
            private short m_ReservedBytes;
            private IntPtr m_ReservedPointer;
            private IntPtr m_StandardInput;
            private IntPtr m_StandardOutput;
            private IntPtr m_StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            internal StartupInfo StartupInfo;
            internal IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInfo
        {
            internal IntPtr Process;
            internal IntPtr Thread;
            internal int ProcessId;
            internal int ThreadId;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr window, out int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(int access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr attributes, int count, int flags, ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr attributes, int flags, IntPtr attribute, IntPtr value,
            IntPtr size, IntPtr previousValue, IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributes);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool CreateProcess(
            string application, StringBuilder commandLine, IntPtr processAttributes,
            IntPtr threadAttributes, bool inheritHandles, int flags,
            IntPtr environment, string workingDirectory,
            ref StartupInfoEx startup, out ProcessInfo process);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}
