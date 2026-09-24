using System;
using UnityEngine;

namespace CSLModernMap.Systems
{
    /// <summary>打开导出文件所在文件夹</summary>
    internal static class ShellFolder
    {
        internal static bool TryReveal(string directory) =>
            RendererLauncher.TryOpenInFileManager(directory);

        internal static bool TryCopyToClipboard(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            try
            {
                GUIUtility.systemCopyBuffer = text;
                return true;
            }
            catch (Exception e)
            {
                Mod.log.Warn("[export] 无法写入剪贴板: " + e.Message);
                return false;
            }
        }

        internal static void RevealOrCopy(string directory, Action<string> notify)
        {
            if (TryReveal(directory))
            {
                return;
            }

            var copied = TryCopyToClipboard(directory);
            if (notify != null)
            {
                notify(copied ? ExportReport.OpenFailedCopied(directory) : ExportReport.OpenFailed(directory));
            }
        }

        internal static void CopyOrComplain(string directory, Action<string> notify)
        {
            var copied = TryCopyToClipboard(directory);
            if (notify == null)
            {
                return;
            }

            notify(copied ? ExportReport.PathCopied(directory) : ExportReport.CopyFailed(directory));
        }
    }
}
