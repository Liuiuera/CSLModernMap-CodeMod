using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace CSLModernMap
{
    internal enum PackageStatus { Missing, Update, Ready }

    internal sealed class WindowsRenderer
    {
        internal const string PackageFile = "CSLModernMapRenderer.msix";
        private readonly string directory;
        private readonly string appId;
        private readonly Version version;

        internal WindowsRenderer(string directory)
        {
            this.directory = directory;
            string[] identity = File.ReadAllLines(Path.Combine(directory, "renderer-package.txt"));
            if (identity.Length != 2 || identity[0].IndexOf('!') <= 0 || !identity[0].EndsWith("!Renderer", StringComparison.Ordinal))
                throw new InvalidDataException("Invalid renderer-package.txt");
            appId = identity[0];
            version = new Version(identity[1]);
        }

        internal PackageStatus Status()
        {
            string family = appId.Substring(0, appId.IndexOf('!'));
            uint count = 0, length = 0;
            int result = GetPackagesByPackageFamily(family, ref count, null, ref length, IntPtr.Zero);
            while (result == 122) // ERROR_INSUFFICIENT_BUFFER, including a concurrent package update
            {
                IntPtr[] names = new IntPtr[count];
                IntPtr buffer = Marshal.AllocHGlobal(checked((int)length * 2));
                try
                {
                    result = GetPackagesByPackageFamily(family, ref count, names, ref length, buffer);
                    if (result == 0)
                    {
                        PackageStatus status = PackageStatus.Missing;
                        for (int i = 0; i < count; i++)
                        {
                            string[] parts = Marshal.PtrToStringUni(names[i]).Split('_');
                            if (parts.Length != 5 || parts[3].Length != 0) continue;
                            if (new Version(parts[1]) >= version) return PackageStatus.Ready;
                            status = PackageStatus.Update;
                        }
                        return status;
                    }
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            if (result != 0) throw new Win32Exception(result);
            return PackageStatus.Missing;
        }

        internal void Install()
        {
            string path = Path.Combine(directory, PackageFile);
            if (!File.Exists(path)) throw new FileNotFoundException(PackageFile, path);
            using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })) { }
        }

        internal bool Open(string mapPath)
        {
            if (Status() == PackageStatus.Missing) return false;
            Activate(appId, string.IsNullOrEmpty(mapPath) ? "" : "\"" + Path.GetFullPath(mapPath) + "\"");
            return true;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int GetPackagesByPackageFamily(string family, ref uint count, [Out] IntPtr[] names,
            ref uint length, IntPtr buffer);

        // Call the native interface directly so both Unity Mono runtimes can activate packaged apps.
        [UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        private delegate int ActivateApplication(IntPtr self, [MarshalAs(UnmanagedType.LPWStr)] string appId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint mode);
        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();
        [DllImport("ole32.dll")]
        private static extern int CoCreateInstance(ref Guid clsid, IntPtr outer, uint context, ref Guid iid, out IntPtr instance);

        private static void Activate(string appId, string arguments)
        {
            int init = CoInitializeEx(IntPtr.Zero, 2);
            if (init != unchecked((int)0x80010106)) Marshal.ThrowExceptionForHR(init); // Existing apartment is usable.
            try
            {
                Guid clsid = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");
                Guid iid = new Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D");
                IntPtr manager;
                Marshal.ThrowExceptionForHR(CoCreateInstance(ref clsid, IntPtr.Zero, 5, ref iid, out manager));
                try
                {
                    IntPtr slot = Marshal.ReadIntPtr(Marshal.ReadIntPtr(manager), 3 * IntPtr.Size);
                    var activate = (ActivateApplication)Marshal.GetDelegateForFunctionPointer(slot, typeof(ActivateApplication));
                    uint pid;
                    Marshal.ThrowExceptionForHR(activate(manager, appId, arguments, 0, out pid));
                }
                finally { Marshal.Release(manager); }
            }
            finally { if (init >= 0) CoUninitialize(); }
        }
    }
}
