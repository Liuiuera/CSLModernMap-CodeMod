using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace CSLModernMap
{
    internal static class RendererLaunchDiagnostics
    {
        private const string LayersKey = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers";

        internal static string Failure(string api, Exception error)
        {
            Win32Exception native = error as Win32Exception;
            ExternalException external = error as ExternalException;
            string code = native != null ? "Win32=" + native.NativeErrorCode
                : external != null ? "HRESULT=0x" + external.ErrorCode.ToString("X8")
                : error.GetType().Name;
            return "stage=launch; api=" + api + "; " + code + "; " + OneLine(error.Message);
        }

        internal static string Context(string executable, string version, string workingDirectory)
        {
            return "executable=" + OneLine(executable)
                + "; payloadVersion=" + OneLine(version)
                + "; fileVersion=" + ReadFileVersion(executable)
                + "; workingDirectory=" + OneLine(workingDirectory)
                + "; __COMPAT_LAYER=" + OneLine(Environment.GetEnvironmentVariable("__COMPAT_LAYER"))
                + "; HKCU.Layers=" + ReadLayer(Registry.CurrentUser, executable)
                + "; HKLM.Layers=" + ReadLayer(Registry.LocalMachine, executable);
        }

        private static string ReadFileVersion(string executable)
        {
            try { return OneLine(FileVersionInfo.GetVersionInfo(executable).FileVersion); }
            catch (Win32Exception error) { return "<unreadable: Win32=" + error.NativeErrorCode + ">"; }
            catch (IOException error) { return "<unreadable: " + OneLine(error.Message) + ">"; }
            catch (UnauthorizedAccessException error) { return "<unreadable: " + OneLine(error.Message) + ">"; }
            catch (SecurityException error) { return "<unreadable: " + OneLine(error.Message) + ">"; }
        }

        private static string ReadLayer(RegistryKey hive, string executable)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(LayersKey))
                {
                    return key == null ? "<absent>"
                        : OneLine(Convert.ToString(key.GetValue(executable, null,
                            RegistryValueOptions.DoNotExpandEnvironmentNames)));
                }
            }
            // 诊断不可读取时保留原启动错误，不把“无法读取”当作“没有设置”。
            catch (SecurityException error) { return "<unreadable: " + OneLine(error.Message) + ">"; }
            catch (UnauthorizedAccessException error) { return "<unreadable: " + OneLine(error.Message) + ">"; }
            catch (IOException error) { return "<unreadable: " + OneLine(error.Message) + ">"; }
        }

        private static string OneLine(string value)
        {
            return string.IsNullOrEmpty(value) ? "<absent>" : value.Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}
