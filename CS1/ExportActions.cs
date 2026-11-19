using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
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

        // 防止导出与安装任务重复启动。
        private static volatile bool _isExporting;



        // 面板可能重建；后台线程不访问这些控件。
        private static UIButton _exportButton;
        private static UIButton _rendererButton;
        private static UIButton _installRendererButton;
        private static UILabel _lastExportLabel, _countsLabel, _rendererStatusLabel;
        private static ExportSummary _lastExport;
        private static bool _rendererInstallOnly;
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
        private static bool _completionPending;
        private static bool _completionSuccess;
        private static string _completionErrorKey;
        private static string _completionDetail;
        private static string _manualInstallDirectory;
        private static string _manualInstallModDirectory;
        private static string _manualHelpDirectory;
        private static string _manualHelpError;
        private static string _rendererStatusMessage;
        private static string _lastRendererInstallationError;
        private static string _rendererRetryMapPath;
        private static float _hideProgressAt = -1f;

        /// <summary>注册当前面板的按钮与状态标签（面板每次创建时调用）。</summary>
        public static void Register(UIButton exportButton, UIButton rendererButton,
                                    UIButton installRendererButton,
                                    UILabel lastExportLabel, UILabel countsLabel, UILabel rendererStatusLabel,
                                    UIProgressBar progressBar, UILabel progressLabel,
                                    UILabel statusLabel)
        {
            _exportButton = exportButton;
            _rendererButton = rendererButton;
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
            bool completed;
            bool success;
            string errorKey;
            string detail;
            lock (ProgressLock)
            {
                percent = _progressPercent;
                stageKey = _progressStageKey;
                visible = _progressVisible;
                completed = _completionPending;
                success = _completionSuccess;
                errorKey = _completionErrorKey;
                detail = _completionDetail;
                _completionPending = false;
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

            if (completed)
            {
                _isExporting = false;
                SetButtonsEnabled(true);
                if (success)
                {
                    _lastRendererInstallationError = null;
                    SetStatus(_rendererInstallOnly ? Translations.Translate("RENDERER_INSTALL_SUCCEEDED")
                        : Translations.Translate("EXPORT_OPEN_SUCCEEDED") + ExportWarningText(), false);
                    RefreshRendererStatus();
                }
                else
                {
                    if (_manualInstallDirectory != null)
                    {
                        _lastRendererInstallationError = detail;
                        _rendererStatusMessage = string.Format(Translations.Translate("RENDERER_WAITING_FAILED"), detail);
                        RefreshSummaryUI();
                        SetStatusNeutral(_rendererStatusMessage);
                        ShowManualInstallPrompt(_manualInstallModDirectory, _manualInstallDirectory);
                    }
                    else
                    {
                        SetStatus(Translations.Translate(string.IsNullOrEmpty(errorKey)
                            ? "RENDERER_PREPARE_FAILED" : errorKey) + detail, true);
                    }
                }
                // 至少保留短时间的 100%，让最后一帧能实际显示出来。
                _hideProgressAt = Time.realtimeSinceStartup + 0.75f;
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
        {
            if (_isExporting) return;
            ExportTaskRunner.Ensure();
            if (IsMacClient) { LaunchMacRendererCore(null); return; }
            _isExporting = true; SetButtonsEnabled(false);
            try { BeginWindowsRendererTask(null, GetModDirPath(), true); }
            catch (Exception ex) { _isExporting = false; SetButtonsEnabled(true); SetStatus(ex.Message, true); }
        }

        public static void OpenRendererFolder()
        {
            string outputDirectory = CSLModernMapConfig.LoadOrDefault().GetOutputDirPath();
            string root = Path.Combine(outputDirectory, "Renderer");
            string manual = RendererPayloadInstaller.FindManualExecutable(root)
                ?? RendererPayloadInstaller.FindManualExecutable(outputDirectory);
            string directory = IsMacClient ? "/Applications" : manual != null ? Path.GetDirectoryName(manual) : root;
            OpenFolder(directory);
        }

        private static void ShowManualInstallPrompt(string modDirectory, string exportDirectory)
        {
            _manualInstallModDirectory = modDirectory;
            _manualHelpDirectory = Path.Combine(exportDirectory, "Renderer");
            _manualHelpError = _lastRendererInstallationError;
            string payload = Path.Combine(modDirectory, RendererPayloadInstaller.PayloadFileName);
            try
            {
                _manualHelpDirectory = RendererManualInstall.PrepareDirectory(payload,
                    _manualHelpDirectory, exportDirectory);
            }
            catch (Exception error)
            {
                UnityEngine.Debug.LogError("[CSLModernMap] " + error);
                _manualHelpError += "\n\n" + error;
            }
            OpenManualInstallHelp();
        }

        private static void OpenManualInstallHelp()
        {
            try
            {
                RendererManualInstall.OpenHelp(Path.Combine(_manualInstallModDirectory, RendererPayloadInstaller.PayloadFileName),
                    _manualHelpDirectory, "CS1", CSLModernMap.MOD_VERSION, _manualHelpError);
                ShowRendererInstallConfirmation();
            }
            catch (Exception error) { SetStatus(error.Message, true); }
        }

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
                    _lastExport = summary; RefreshSummaryUI();
                    string message = Translations.Translate("EXPORT_SUCCESS_STATUS");
                    if (summary.Issues > 0) message += "\n" + string.Format(Translations.Translate("EXPORT_WARNINGS"), summary.Issues, summary.FirstIssue);
                    SetStatus(message, false);
                    if (rendererTarget == RendererTarget.Windows)
                    {
                        try { BeginWindowsRendererTask(summary.Path, GetModDirPath(), false); }
                        catch (Exception ex) { _isExporting = false; SetButtonsEnabled(true); SetStatus(Translations.Translate("RENDERER_PREPARE_FAILED") + ex.Message, true); }
                        return;
                    }
                    if (rendererTarget == RendererTarget.MacOS) LaunchMacRendererCore(summary.Path);
                    _isExporting = false; SetButtonsEnabled(true); _hideProgressAt = Time.realtimeSinceStartup + 2f;
                };
                if (withSatellite)
                    SatelliteExport.Begin(exporter.TargetDirectory, onCompleted, ReportRendererProgress);
                else ExportTaskRunner.Begin(exporter, ReportRendererProgress, onCompleted);
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

        private static void ShowRendererInstallConfirmation()
        {
            if (_isExporting) return;
            var notification = NotificationBase.ShowNotification<YesNoNotification>();
            if (notification == null) return;
            notification.Title = Translations.Translate("RENDERER_EXTRACT_CONFIRM");
            notification.AddParas(Translations.Translate("RENDERER_EXTRACT_CONFIRM_GUIDE"));
            notification.NoButton.text = Translations.Translate("RENDERER_OPEN_HELP");
            notification.NoButton.eventClicked += (component, click) => OpenManualInstallHelp();
            notification.YesButton.text = Translations.Translate("RENDERER_CHECK_AND_OPEN");
            notification.YesButton.eventClicked += (component, click) => CheckAndOpenRenderer();
        }

        private static void CheckAndOpenRenderer()
        {
            if (_isExporting) return;
            if (!RefreshRendererStatus())
            {
                SetStatusNeutral(Translations.Translate("RENDERER_NOT_FOUND"));
                var notification = NotificationBase.ShowNotification<YesNoNotification>();
                if (notification == null) return;
                notification.Title = Translations.Translate("CHECK_RENDERER_STATUS");
                notification.AddParas(Translations.Translate("RENDERER_NOT_FOUND"));
                notification.NoButton.text = Translations.Translate("NOTE_CLOSE");
                notification.YesButton.text = Translations.Translate("RENDERER_OPEN_HELP");
                notification.YesButton.eventClicked += (component, click) => OpenManualInstallHelp();
                return;
            }
            SetStatusNeutral(_rendererStatusMessage);
            try
            {
                string output = CSLModernMapConfig.LoadOrDefault().GetOutputDirPath();
                string root = Path.Combine(output, "Renderer");
                string executable = RendererPayloadInstaller.FindManualExecutable(root)
                    ?? RendererPayloadInstaller.FindManualExecutable(output)
                    ?? Path.Combine(Path.Combine(root, CSLModernMap.MOD_VERSION), "CSLModernMapRenderer.exe");
                LaunchWindowsRenderer(executable, _rendererRetryMapPath);
                SetStatus(Translations.Translate("RENDERER_OPENED"), false);
            }
            catch (Exception error)
            {
                AlgernonCommons.Logging.Error("[renderer] Manual installation launch failed", error.ToString());
                SetStatus(error.Message, true);
            }
        }

        private static bool RefreshRendererStatus()
        {
            bool ready;
            string status = ReadRendererStatus(out ready);
            if (ready) _lastRendererInstallationError = null;
            _rendererStatusMessage = !ready && !string.IsNullOrEmpty(_lastRendererInstallationError)
                ? string.Format(Translations.Translate("RENDERER_WAITING_FAILED"), _lastRendererInstallationError)
                : status;
            RefreshSummaryUI();
            return ready;
        }

        private static string ReadRendererStatus(out bool ready)
        {
            ready = false;
            if (IsMacClient)
            {
                string app = FindMacApp(CSLModernMapConfig.LoadOrDefault().GetOutputDirPath());
                if (string.IsNullOrEmpty(app)) return Translations.Translate("RENDERER_NOT_INSTALLED");
                ready = true;
                return Translations.Translate("RENDERER_INSTALLED");
            }
            string outputDirectory = CSLModernMapConfig.LoadOrDefault().GetOutputDirPath();
            string root = Path.Combine(outputDirectory, "Renderer");
            if (RendererPayloadInstaller.FindManualExecutable(root) != null
                || RendererPayloadInstaller.FindManualExecutable(outputDirectory) != null)
            {
                ready = true;
                return Translations.Translate("RENDERER_MANUAL_INSTALLED");
            }
            try
            {
                ready = RendererPayloadInstaller.IsCurrentInstallation(GetModDirPath(), root);
                return Translations.Translate(ready ? "RENDERER_INSTALLED"
                    : Directory.Exists(root) ? "RENDERER_UPDATE_AVAILABLE" : "RENDERER_NOT_INSTALLED");
            }
            catch (Exception ex) { return ex.Message; }
        }

        /// <summary>后台完成载荷校验/解压并启动 renderer；不直接访问任何游戏 UI。</summary>
        private static void BeginWindowsRendererTask(string mapPath, string modDirectory, bool installOnly)
        {
            _rendererRetryMapPath = mapPath;
            string mapOutputDirectory = installOnly ? CSLModernMapConfig.LoadOrDefault().GetOutputDirPath() : Path.GetDirectoryName(mapPath);
            _rendererInstallOnly = installOnly;
            string rendererInstallRoot = string.IsNullOrEmpty(mapOutputDirectory)
                ? null
                : Path.Combine(mapOutputDirectory, "Renderer");
            lock (ProgressLock)
            {
                _progressPercent = 0;
                _progressStageKey = "RENDERER_PROGRESS_CHECKING";
                _progressVisible = true;
                _completionPending = false;
                _completionErrorKey = string.Empty;
                _completionDetail = string.Empty;
                _manualInstallDirectory = null;
                _manualInstallModDirectory = modDirectory;
            }
            _hideProgressAt = -1f;

            Thread worker = new Thread(delegate()
            {
                try
                {
                    string rendererPath = RendererPayloadInstaller.EnsureInstalled(
                        modDirectory, rendererInstallRoot, ReportRendererProgress, installOnly);
                    if (installOnly) { CompleteRendererTask(true, string.Empty, string.Empty); return; }
                    try
                    {
                        LaunchWindowsRenderer(rendererPath, mapPath);
                    }
                    catch (Exception error)
                    {
                        string detail = RendererLaunchDiagnostics.Failure("Process.Start", error);
                        AlgernonCommons.Logging.Error("[renderer] " + detail + "; "
                            + RendererLaunchDiagnostics.Context(rendererPath, CSLModernMap.MOD_VERSION, Path.GetDirectoryName(mapPath)),
                            error.ToString());
                        CompleteRendererTask(false, "RENDERER_PREPARE_FAILED", detail);
                        return;
                    }
                    CompleteRendererTask(true, string.Empty, string.Empty);
                }
                catch (FileNotFoundException ex)
                {
                    AlgernonCommons.Logging.Error("[renderer] Automatic installation failed", ex.ToString());
                    _manualInstallDirectory = mapOutputDirectory;
                    CompleteRendererTask(
                        false, "RENDERER_PAYLOAD_MISSING", ex.Message);
                }
                catch (InvalidDataException ex)
                {
                    AlgernonCommons.Logging.Error("[renderer] Automatic installation failed", ex.ToString());
                    _manualInstallDirectory = mapOutputDirectory;
                    CompleteRendererTask(
                        false, "RENDERER_PAYLOAD_INVALID", ex.Message);
                }
                catch (Exception ex)
                {
                    AlgernonCommons.Logging.Error("[renderer] Automatic installation failed", ex.ToString());
                    _manualInstallDirectory = mapOutputDirectory;
                    CompleteRendererTask(
                        false, "RENDERER_PREPARE_FAILED",
                        ex.GetType().Name + ": " + ex.Message);
                }
            });
            worker.IsBackground = true;
            worker.Name = "CSLModernMap Renderer Installer";
            worker.Start();
        }

        private static void LaunchWindowsRenderer(string executable, string mapPath)
        {
            var psi = new ProcessStartInfo(executable,
                string.IsNullOrEmpty(mapPath) ? "gui" : "gui " + QuoteArgument(mapPath)) { UseShellExecute = false };
            string workDir = string.IsNullOrEmpty(mapPath) ? Path.GetDirectoryName(executable) : Path.GetDirectoryName(mapPath);
            if (!string.IsNullOrEmpty(workDir) && Directory.Exists(workDir)) psi.WorkingDirectory = workDir;
            using (Process process = Process.Start(psi)) { }
        }

        private static void ReportRendererProgress(int percent, string stageKey)
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

        private static void CompleteRendererTask(
            bool success, string errorKey, string detail)
        {
            lock (ProgressLock)
            {
                if (success)
                {
                    _progressPercent = 100;
                    _progressStageKey = "RENDERER_PROGRESS_READY";
                }
                _progressVisible = true;
                _completionSuccess = success;
                _completionErrorKey = errorKey;
                _completionDetail = detail;
                _completionPending = true;
            }
        }

        /// <summary>内嵌的 macOS 安装指南逻辑名（与 csproj 的 LogicalName 一致）。</summary>
        private const string MAC_GUIDE_RESOURCE = "CSLModernMap.mac-guide.html";

        /// <summary>
        /// macOS 渲染器入口：不做任何下载。若已安装了旧版
        /// "CSLModernMap Renderer.app"（优先 /Applications，兼容旧版输出目录位置），
        /// 直接启动；否则把内嵌的安装指南抽取到输出目录并用默认浏览器打开
        /// （本地页面，不依赖外网托管）。
        /// </summary>
        private static void LaunchMacRendererCore(string mapPath)
        {
            string outputDir = string.IsNullOrEmpty(mapPath) ? CSLModernMapConfig.LoadOrDefault().GetOutputDirPath() : Path.GetDirectoryName(mapPath);
            Directory.CreateDirectory(outputDir);
            string appDir = FindMacApp(outputDir);

            if (!string.IsNullOrEmpty(appDir))
            {
                try
                {
                    if (string.IsNullOrEmpty(mapPath)) { SetStatus(Translations.Translate("RENDERER_INSTALL_SUCCEEDED"), false); RefreshRendererStatus(); return; }
                    ProcessStartInfo psi = new ProcessStartInfo(
                        "/usr/bin/open",
                        "-n " + QuoteArgument(appDir) + " --args gui " + QuoteArgument(mapPath));
                    psi.UseShellExecute = false;
                    Process.Start(psi);
                    RefreshRendererStatus();
                    SetStatus(Translations.Translate("MAC_RENDERER_STARTED") + "\n"
                        + Translations.Translate("SAVE_PATH") + mapPath, false);
                    return;
                }
                catch (Exception ex)
                {
                    SetStatus(Translations.Translate("MAC_RENDERER_START_FAILED") + ex.Message, true);
                    return;
                }
            }

            // 未安装时打开本地安装指南。
            try
            {
                string guidePath = MaterializeMacGuide(outputDir);
                Process.Start(new ProcessStartInfo(guidePath) { UseShellExecute = true });
                SetStatus(Translations.Translate("MAC_GUIDE_OPENED") + "\n"
                    + Translations.Translate("SAVE_PATH") + mapPath, false);
            }
            catch (Exception ex)
            {
                SetStatus(Translations.Translate("MAC_GUIDE_FAILED") + ex.Message, true);
            }
        }

        /// <summary>
        /// 定位已安装的 .app：优先 /Applications（Release 的 DMG 拖拽安装目标），
        /// 兼容旧版 Map 输出目录同层位置。返回 .app 绝对路径；未找到返回空串。
        /// </summary>
        private static string FindMacApp(string outputDir)
        {
            string[] candidates = new string[2];
            candidates[0] = "/Applications/CSLModernMap Renderer.app";
            if (!string.IsNullOrEmpty(outputDir))
            {
                candidates[1] = Path.Combine(outputDir, "CSLModernMap Renderer.app");
            }
            foreach (string candidate in candidates)
            {
                if (string.IsNullOrEmpty(candidate))
                {
                    continue;
                }
                string plist = Path.Combine(Path.Combine(candidate, "Contents"), "Info.plist");
                if (File.Exists(plist))
                {
                    return candidate;
                }
            }
            return string.Empty;
        }

        /// <summary>把内嵌的安装指南抽取到输出目录并替换版本占位符，返回本地文件路径。</summary>
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
                    string html = reader.ReadToEnd()
                        .Replace("{{MOD_VERSION}}", CSLModernMap.MOD_VERSION)
                        .Replace("{{LANG}}", GuideLanguageCode());
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
