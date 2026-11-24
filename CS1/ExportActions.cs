using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using ColossalFramework;
using ColossalFramework.Plugins;
using ColossalFramework.UI;
using AlgernonCommons.Translation;
using AlgernonCommons.Notifications;
using CSLModernMap.Config;
using CSLModernMap.Exporter;
using CSLModernMap.Traffic;
using UnityEngine;

namespace CSLModernMap.UI
{
    /// <summary>导出完成后的渲染器动作；平台由按钮显式选择，不自动路由。</summary>
    internal enum RendererTarget
    {
        None,
        Windows,
        MacOS,
    }

    /// <summary>
    /// 与 UI 无关的导出动作逻辑（从旧版 CSLModernMapConfigUI 提取）：
    /// 选项面板只负责布局与事件绑定，点击后调用本类。
    /// </summary>
    internal static class ExportActions
    {
        private static void LogExportFailure(Exception error)
        {
            Assembly mod = typeof(ExportActions).Assembly;
            System.Text.StringBuilder context = new System.Text.StringBuilder("CS1 export failed. Assemblies:\n");
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly != mod && assembly.GetName().Name != "Newtonsoft.Json") continue;
                context.Append(assembly.FullName).Append(" | ");
                try { context.Append(assembly.Location); }
                catch (NotSupportedException) { context.Append("<dynamic assembly>"); }
                context.Append('\n');
            }
            // ToString includes the stack and inner exception chain, including type initializers.
            AlgernonCommons.Logging.Error(context.ToString(), error.ToString());
        }

        // 防止重复启动导出。
        private static volatile bool _isExporting;

        // 面板可能重建；后台线程不访问这些控件。
        private static UIButton _exportButton;
        private static UIButton _rendererButton;
        private static UIButton _openRendererButton;
        private static UIButton _installRendererButton;
        private static UILabel _lastExportLabel, _countsLabel, _rendererStatusLabel;
        private static ExportSummary _lastExport;
        private static UILabel _statusLabel;
        private static string _statusMessage;
        private static Color32 _statusColor;
        private static UIProgressBar _progressBar;
        private static UILabel _progressLabel;

        // 后台线程只写状态，游戏 UI 在主线程读取。
        private static readonly object ProgressLock = new object();
        private static int _progressPercent;
        private static string _progressStageKey;
        private static bool _progressVisible;
        private static string _rendererStatusMessage;
        private static string _rendererRetryMapPath;
        private static float _hideProgressAt = -1f;

        /// <summary>注册当前面板的按钮与状态标签（面板每次创建时调用）。</summary>
        public static void Register(UIButton exportButton, UIButton rendererButton,
                                    UIButton openRendererButton, UIButton installRendererButton,
                                    UILabel lastExportLabel, UILabel countsLabel, UILabel rendererStatusLabel,
                                    UIProgressBar progressBar, UILabel progressLabel,
                                    UILabel statusLabel)
        {
            _exportButton = exportButton;
            _rendererButton = rendererButton;
            _openRendererButton = openRendererButton;
            _installRendererButton = installRendererButton;
            _lastExportLabel = lastExportLabel; _countsLabel = countsLabel; _rendererStatusLabel = rendererStatusLabel;
            RefreshRendererStatus();
            _progressBar = progressBar;
            _progressLabel = progressLabel;
            _statusLabel = statusLabel;
            SetButtonsEnabled(!_isExporting);
            if (_statusMessage == null) SetStatusNeutral(Translations.Translate("EXPORT_IDLE"));
            else RefreshStatusUI();
            RefreshProgressUI();
        }

        /// <summary>由常驻任务组件在游戏主线程刷新 UI。</summary>
        public static void RefreshProgressUI()
        {
            int percent;
            string stageKey;
            bool visible;
            lock (ProgressLock)
            {
                percent = _progressPercent;
                stageKey = _progressStageKey;
                visible = _progressVisible;
            }

            try
            {
                if (!object.ReferenceEquals(_progressBar, null))
                {
                    _progressBar.value = percent;
                    _progressBar.isVisible = visible;
                }
                if (!object.ReferenceEquals(_progressLabel, null))
                {
                    _progressLabel.isVisible = visible;
                    _progressLabel.text = visible && !string.IsNullOrEmpty(stageKey)
                        ? Translations.Translate(stageKey) + " " + percent + "%"
                        : string.Empty;
                }
            }
            catch
            {
            }

            if (_hideProgressAt >= 0f && Time.realtimeSinceStartup >= _hideProgressAt)
            {
                lock (ProgressLock)
                {
                    _progressVisible = false;
                }
                _hideProgressAt = -1f;
                try
                {
                    if (!object.ReferenceEquals(_progressBar, null))
                    {
                        _progressBar.isVisible = false;
                    }
                    if (!object.ReferenceEquals(_progressLabel, null))
                    {
                        _progressLabel.isVisible = false;
                    }
                }
                catch
                {
                }
            }
        }

        internal static void ShowExportProgressHint(string stageKey)
        {
            lock (ProgressLock)
            {
                if (!_progressVisible) return;
                _progressStageKey = stageKey;
            }
        }

        /// <summary>TM:PE 是否就绪（任何异常视为未就绪）。</summary>
        public static bool SafeTmpeAvailable()
        {
            try
            {
                return TMPEBridge.IsAvailable;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>打开输出目录（失败静默，不影响游戏）。</summary>
        public static void OpenOutputDir()
        {
            try
            {
                string dir = CSLModernMapConfig.LoadOrDefault().GetOutputDirPath();
                if (!Directory.Exists(dir))
                {
                    Directory.CreateDirectory(dir);
                }
                ProcessStartInfo psi;
                if (Application.platform == RuntimePlatform.OSXPlayer)
                {
                    psi = new ProcessStartInfo("/usr/bin/open", QuoteArgument(dir));
                    psi.UseShellExecute = false;
                }
                else
                {
                    psi = new ProcessStartInfo("explorer.exe", QuoteArgument(dir));
                    psi.UseShellExecute = false;
                }
                Process.Start(psi);
            }
            catch
            {
                // 打开资源管理器失败不影响游戏运行
            }
        }

        public static void InstallRenderer()
        { OpenRenderer(offerInstallation: true); }

        public static void OpenRenderer(bool offerInstallation = false)
        {
            if (_isExporting) return;
            ExportTaskRunner.Ensure();
            if (IsMacClient) { LaunchMacRendererCore(_rendererRetryMapPath, offerInstallation); return; }
            OpenWindowsRenderer(offerInstallation);
        }

        public static void OpenMacApplications() { OpenFolder("/Applications"); }

        public static void CopyOutputDir()
        { GUIUtility.systemCopyBuffer = CSLModernMapConfig.LoadOrDefault().GetOutputDirPath(); SetStatus(Translations.Translate("OUTPUT_COPIED"), false); }

        private static void OpenFolder(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                Process.Start(new ProcessStartInfo(IsMacClient ? "/usr/bin/open" : "explorer.exe", QuoteArgument(directory)) { UseShellExecute = false });
            }
            catch (Exception ex) { SetStatus(ex.Message, true); }
        }

        /// <summary>
        /// 客户端是否为 macOS 版本。读取 Unity 的运行平台，不依赖 Mod 自身是否被 Mac 编译。
        /// 任何异常按 Windows 处理（游戏主机只有这两个平台）。
        /// </summary>
        public static bool IsMacClient
        {
            get
            {
                try
                {
                    return Application.platform == RuntimePlatform.OSXPlayer;
                }
                catch
                {
                    return false;
                }
            }
        }

        public static void DoExportFromRendererButton()
        { DoExport(IsMacClient ? RendererTarget.MacOS : RendererTarget.Windows); }

        public static void DoExport(RendererTarget rendererTarget)
        {
            if (!CanStartExport()) return;
            if (!CSLModernMapConfig.LoadOrDefault().SatelliteEnabled)
            {
                StartExport(rendererTarget, false);
                return;
            }

            var requestedCity = Singleton<SimulationManager>.instance.m_metaData;
            var notification = NotificationBase.ShowNotification<YesNoNotification>();
            if (notification == null)
            {
                SetStatus(Translations.Translate("SATELLITE_SAVE_MESSAGE"), true);
                return;
            }
            notification.Title = Translations.Translate("SATELLITE_SAVE_TITLE");
            notification.AddParas(Translations.Translate("SATELLITE_SAVE_MESSAGE"));
            notification.NoButton.text = Translations.Translate("SATELLITE_SAVE_BACK");
            notification.YesButton.text = Translations.Translate("SATELLITE_SAVE_CONTINUE");
            notification.YesButton.eventClick += (component, click) =>
            {
                if (CanStartExport() && ReferenceEquals(requestedCity, Singleton<SimulationManager>.instance.m_metaData))
                    StartExport(rendererTarget, true);
            };
        }

        private static bool CanStartExport()
        {
            if (_isExporting) return false;
            if (!CSLModernMapLoadingExtension.IsLevelLoaded)
            { SetStatus(Translations.Translate("LOAD_SAVE_FIRST"), true); return false; }
            return true;
        }

        internal static void RegisterExportDirectory(string directory = null)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) return;

            try
            {
                GameExportDirectories.Register(GameExportDirectories.RegistryDirectory, ExportGame.CS1,
                    directory ?? CSLModernMapConfig.LoadOrDefault().GetOutputDirPath());
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            {
                AlgernonCommons.Logging.Error("[export] Could not register the export directory", error.ToString());
            }
        }

        private static void StartExport(RendererTarget rendererTarget, bool withSatellite)
        {
            _isExporting = true; SetButtonsEnabled(false); _hideProgressAt = -1f;
            lock (ProgressLock) { _progressPercent = 0; _progressVisible = true; _progressStageKey = "EXPORT_PROGRESS_WAITING"; }
            SetStatusNeutral(Translations.Translate("EXPORTING"));
            try
            {
                CSLModernMapExporter exporter = new CSLModernMapExporter {
                    TargetDirectory = CSLModernMapConfig.LoadOrDefault().GetOutputDirPath() };
                ExportCompleted onCompleted = delegate(ExportSummary summary, Exception error)
                {
                    if (error != null)
                    {
                        LogExportFailure(error);
                        _isExporting = false; SetButtonsEnabled(true);
                        SetStatusNeutral(Translations.Translate("EXPORT_FAILED") + error.Message);
                        _hideProgressAt = Time.realtimeSinceStartup + 2f;
                        return;
                    }
                    RegisterExportDirectory(Path.GetDirectoryName(summary.Path));
                    _lastExport = summary; RefreshSummaryUI();
                    string message = Translations.Translate("EXPORT_SUCCESS_STATUS");
                    if (summary.Issues > 0) message += "\n" + string.Format(Translations.Translate("EXPORT_WARNINGS"), summary.Issues, summary.FirstIssue);
                    SetStatus(message, false);
                    if (rendererTarget == RendererTarget.Windows)
                    {
                        _rendererRetryMapPath = summary.Path;
                        OpenWindowsRenderer();
                    }
                    if (rendererTarget == RendererTarget.MacOS) LaunchMacRendererCore(summary.Path);
                    _isExporting = false; SetButtonsEnabled(true); _hideProgressAt = Time.realtimeSinceStartup + 2f;
                };
                if (withSatellite)
                    SatelliteExport.Begin(exporter.TargetDirectory, onCompleted, ReportExportProgress);
                else ExportTaskRunner.Begin(exporter, ReportExportProgress, onCompleted);
            }
            catch (Exception ex)
            {
                _isExporting = false; SetButtonsEnabled(true); _progressVisible = false;
                LogExportFailure(ex);
                SetStatusNeutral(Translations.Translate("EXPORT_FAILED") + ex.Message);
            }
        }

        private static string ExportWarningText()
        { return _lastExport == null || _lastExport.Issues == 0 ? "" : "\n" + string.Format(Translations.Translate("EXPORT_WARNINGS"), _lastExport.Issues, _lastExport.FirstIssue); }

        private static void RefreshSummaryUI()
        {
            if (_lastExportLabel != null) _lastExportLabel.text = Translations.Translate("LAST_EXPORT") + (_lastExport == null
                ? Translations.Translate("NO_EXPORT_YET") : _lastExport.Time.ToString("yyyy-MM-dd HH:mm:ss") + "  " + Path.GetFileName(_lastExport.Path));
            if (_lastExportLabel != null && _lastExport != null) _lastExportLabel.tooltip = _lastExport.Path;
            if (_countsLabel != null) _countsLabel.text = Translations.Translate("LAST_COUNTS") + (_lastExport == null
                ? Translations.Translate("NO_EXPORT_YET") : string.Format(Translations.Translate("EXPORT_COUNTS"),
                    _lastExport.Nodes, _lastExport.Networks, _lastExport.Buildings, _lastExport.Stops, _lastExport.Lines));
            if (_rendererStatusLabel != null)
            {
                _rendererStatusLabel.text = Translations.Translate("RENDERER_STATUS") + _rendererStatusMessage;
                _rendererStatusLabel.tooltip = _rendererStatusMessage;
            }
        }

        private static void RefreshRendererStatus()
        {
            try
            {
                if (IsMacClient)
                {
                    var app = MacRenderer.Find(CSLModernMapConfig.LoadOrDefault().GetOutputDirPath());
                    _rendererStatusMessage = Translations.Translate(app == null ? "RENDERER_NOT_INSTALLED" :
                        app.NeedsUpdate(CSLModernMap.MOD_VERSION) ? "RENDERER_UPDATE_AVAILABLE" : "RENDERER_INSTALLED");
                }
                else
                {
                    PackageStatus status = new WindowsRenderer(GetModDirPath()).Status();
                    _rendererStatusMessage = Translations.Translate(status == PackageStatus.Ready ? "RENDERER_INSTALLED"
                        : status == PackageStatus.Update ? "RENDERER_UPDATE_AVAILABLE" : "RENDERER_NOT_INSTALLED");
                }
            }
            catch (Exception error) { _rendererStatusMessage = error.Message; }
            RefreshSummaryUI();
        }

        private static void OpenWindowsRenderer(bool offerInstallation = true)
        {
            try
            {
                var package = new WindowsRenderer(GetModDirPath());
                bool opened = package.Open(_rendererRetryMapPath, offerInstallation);
                string key = opened ? (_rendererRetryMapPath == null ? "RENDERER_OPENED" : "EXPORT_OPEN_SUCCEEDED")
                    : (offerInstallation ? "MSIX_INSTALL_HINT" : "RENDERER_NOT_INSTALLED");
                if (opened) _rendererRetryMapPath = null;
                SetStatus((!opened && _rendererRetryMapPath != null ? Translations.Translate("EXPORT_SUCCESS_STATUS") + "\n" : "")
                    + Translations.Translate(key) + ExportWarningText(), false);
                RefreshRendererStatus();
            }
            catch (Exception error)
            {
                AlgernonCommons.Logging.Error("[renderer] MSIX", error.ToString());
                SetStatus((_rendererRetryMapPath == null ? "" : Translations.Translate("EXPORT_SUCCESS_STATUS") + "\n")
                    + error.Message, true);
            }
        }

        private static void ReportExportProgress(int percent, string stageKey)
        {
            lock (ProgressLock)
            {
                if (percent > _progressPercent)
                {
                    _progressPercent = Math.Min(100, percent);
                }
                _progressStageKey = stageKey;
                _progressVisible = true;
            }
        }

        /// <summary>内嵌的 macOS 安装指南资源名。</summary>
        private const string MAC_GUIDE_RESOURCE = "CSLModernMap.mac-guide.html";

        private static void LaunchMacRendererCore(string mapPath, bool offerInstallation = true)
        {
            _rendererRetryMapPath = mapPath;
            string outputDir = string.IsNullOrEmpty(mapPath) ? CSLModernMapConfig.LoadOrDefault().GetOutputDirPath() : Path.GetDirectoryName(mapPath);
            try
            {
                var app = MacRenderer.Find(outputDir);
                if (app == null)
                {
                    if (offerInstallation) OpenMacGuide(outputDir, mapPath);
                    else SetStatusNeutral(Translations.Translate("RENDERER_NOT_INSTALLED"));
                    return;
                }
                if (offerInstallation && app.NeedsUpdate(CSLModernMap.MOD_VERSION))
                {
                    string message = string.Format(Translations.Translate("MAC_UPDATE_PROMPT"),
                        string.IsNullOrEmpty(app.DisplayVersion) ? "?" : app.DisplayVersion, CSLModernMap.MOD_VERSION);
                    SetStatusNeutral(message);
                    var dialog = NotificationBase.ShowNotification<YesNoNotification>();
                    if (dialog == null) { OpenMacGuide(outputDir, mapPath); return; }
                    dialog.Title = Translations.Translate("RENDERER_UPDATE_AVAILABLE");
                    dialog.AddParas(message);
                    dialog.YesButton.text = Translations.Translate("MAC_DOWNLOAD");
                    dialog.NoButton.text = Translations.Translate("NOTE_CLOSE");
                    dialog.YesButton.eventClick += (component, click) => OpenMacGuide(outputDir, mapPath);
                    return;
                }
                var arguments = "-n " + QuoteArgument(app.PathName);
                if (!string.IsNullOrEmpty(mapPath)) arguments += " --args gui " + QuoteArgument(mapPath);
                using (Process.Start(new ProcessStartInfo("/usr/bin/open", arguments) { UseShellExecute = false })) { }
                _rendererRetryMapPath = null;
                RefreshRendererStatus();
                SetStatus(Translations.Translate("RENDERER_OPENED")
                    + (string.IsNullOrEmpty(mapPath) ? "" : "\n" + Translations.Translate("SAVE_PATH") + mapPath), false);
            }
            catch (Exception ex) { SetStatus(ex.Message, true); }
        }

        private static void OpenMacGuide(string outputDir, string mapPath)
        {
            try
            {
                Directory.CreateDirectory(outputDir);
                string page = MaterializeMacGuide(outputDir);
                using (Process.Start(new ProcessStartInfo("/usr/bin/open", QuoteArgument(page)) { UseShellExecute = false })) { }
                SetStatus(Translations.Translate("MAC_DOWNLOAD")
                    + (string.IsNullOrEmpty(mapPath) ? "" : "\n" + Translations.Translate("SAVE_PATH") + mapPath), false);
            }
            catch (Exception error) { SetStatus(error.Message, true); }
        }

        /// <summary>把内嵌的安装指南按当前语言写到输出目录，返回本地文件路径。</summary>
        private static string MaterializeMacGuide(string outputDir)
        {
            string target = Path.Combine(outputDir, "mac-guide.html");
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(MAC_GUIDE_RESOURCE))
            {
                if (stream == null)
                {
                    throw new InvalidOperationException("缺少内嵌安装指南资源：" + MAC_GUIDE_RESOURCE);
                }
                using (StreamReader reader = new StreamReader(stream))
                {
                    string html = reader.ReadToEnd().Replace("{{LANG}}", GuideLanguageCode());
                    File.WriteAllText(target, html);
                }
            }
            return target;
        }

        /// <summary>
        /// 安装指南页面用的语言码：页面只备了中英两套文案，所以这里也只归成两种——
        /// 中文落中文，其余语言（含日、德等第三种语言）一律落英文。
        /// 取“当前生效语言”而非用户存的设置值：用户选“跟随系统”时 CurrentLanguage 会是
        /// "default"，拿它判断会让英文用户也看到中文页面。
        /// </summary>
        private static string GuideLanguageCode() =>
            Translations.ActiveLanguageCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "zh-CN"
                : "en";

        /// <summary>为 ProcessStartInfo.Arguments 编码单个参数（不经 shell 拼接执行）。</summary>
        private static string QuoteArgument(string value)
        {
            if (value == null)
            {
                return "\"\"";
            }
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        /// <summary>
        /// 定位 Mod 安装目录。参考 CSLMapView：用 PluginManager 找 modPath，
        /// 比 Assembly.GetExecutingAssembly().Location 可靠（游戏内 Location 可能不指向安装目录）。
        /// </summary>
        private static string GetModDirPath()
        {
            try
            {
                foreach (PluginManager.PluginInfo plugin in Singleton<PluginManager>.instance.GetPluginsInfo())
                {
                    string assemblies = plugin.assembliesString;
                    if (assemblies != null
                        && assemblies.IndexOf("CSLModernMap", StringComparison.OrdinalIgnoreCase) >= 0
                        && !string.IsNullOrEmpty(plugin.modPath))
                    {
                        return plugin.modPath;
                    }
                }
            }
            catch
            {
            }

            throw new DirectoryNotFoundException("CSLModernMap installation directory was not found");
        }

        /// <summary>状态标签：成功绿 / 失败红。</summary>
        public static void SetStatus(string message, bool error)
        {
            _statusMessage = message;
            _statusColor = error ? new Color32(255, 96, 96, 255) : new Color32(0, 255, 128, 255);
            RefreshStatusUI();
        }

        /// <summary>状态标签：中性提示（金色，如「导出中…」）。</summary>
        private static void SetStatusNeutral(string message)
        {
            _statusMessage = message; _statusColor = new Color32(255, 215, 0, 255);
            RefreshStatusUI();
        }

        private static void RefreshStatusUI()
        {
            if (_statusLabel == null) return;
            try
            {
                _statusLabel.text = _statusMessage.Length > 320 ? _statusMessage.Substring(0, 320) + "…" : _statusMessage;
                _statusLabel.tooltip = _statusMessage;
                _statusLabel.textColor = _statusColor;
            }
            catch
            {
            }
        }

        /// <summary>任务期间禁用导出及安装按钮。</summary>
        private static void SetButtonsEnabled(bool enabled)
        {
            try
            {
                if (!object.ReferenceEquals(_exportButton, null))
                {
                    _exportButton.isEnabled = enabled;
                }
                if (!object.ReferenceEquals(_rendererButton, null))
                {
                    _rendererButton.isEnabled = enabled;
                }
                if (!object.ReferenceEquals(_openRendererButton, null))
                {
                    _openRendererButton.isEnabled = enabled;
                }
                if (!object.ReferenceEquals(_installRendererButton, null))
                {
                    _installRendererButton.isEnabled = enabled;
                }
            }
            catch
            {
            }
        }

    }
}
