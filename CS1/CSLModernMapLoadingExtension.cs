using ICities;

namespace CSLModernMap
{
    public class CSLModernMapLoadingExtension : AlgernonCommons.LoadingBase<UI.CSLModernMapOptionsPanel>
    {
        public static bool IsLevelLoaded { get; private set; }

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
