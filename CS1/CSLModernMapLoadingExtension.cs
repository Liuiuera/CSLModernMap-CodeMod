using ICities;

namespace CSLModernMap
{
    /// <summary>
    /// 跟踪游戏加载状态。
    /// 派生 AlgernonCommons.LoadingBase：OnLevelLoaded 基类会在 LoadedActions 之后
    /// 自动重挂选项面板事件钩子（进游戏后游戏会重置选项 UI，不重挂则游戏内看不到
    /// 面板）并自动调用 WhatsNew.ShowWhatsNew()，因此这里不再手动触发更新通知。
    /// </summary>
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
