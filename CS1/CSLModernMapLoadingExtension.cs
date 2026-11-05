using ICities;

namespace CSLModernMap
{
    /// <summary>跟踪加载状态；基类负责重挂选项面板及显示更新通知。</summary>
    public class CSLModernMapLoadingExtension : AlgernonCommons.LoadingBase<UI.CSLModernMapOptionsPanel>
    {
        public static bool IsLevelLoaded { get; private set; }

        /// <summary>加载完成后的模组动作（由基类在冲突检查通过后调用）。</summary>
        protected override void LoadedActions(LoadMode mode)
        {
            base.LoadedActions(mode);
            IsLevelLoaded = true;
        }

        public override void OnLevelUnloading()
        {
            IsLevelLoaded = false;
            UI.ExportTaskRunner.CancelCapture();
            base.OnLevelUnloading();
        }
    }
}
