using AlgernonCommons.UI;
using AlgernonCommons.Translation;
using ColossalFramework.UI;
using CSLModernMap.Config;
using UnityEngine;

namespace CSLModernMap.UI
{
    public sealed class CSLModernMapOptionsPanel : OptionsPanelBase
    {
        protected override void Setup()
        {
            float width = OptionsPanelManager<CSLModernMapOptionsPanel>.PanelWidth;
            float buttonWidth = Mathf.Min(260f, (width - 12f) / 2f);
            float y = 0f;
            UILabel heading = Label("PANEL_TITLE", width, ref y, 32f);
            heading.font = UIFonts.SemiBold;
            heading.textScale = 1.1f;
            Label("EXPORT_OPEN_HINT", width, ref y, 28f);

            UIButton open = UIButtons.AddButton(this, 0f, y, Translations.Translate("EXPORT_AND_OPEN"), width, 48f, 0.95f);
            open.color = open.focusedColor = new Color32(146, 190, 215, 255);
            open.hoveredColor = new Color32(166, 207, 229, 255);
            open.pressedColor = new Color32(122, 170, 199, 255);
            open.textColor = open.focusedTextColor = open.hoveredTextColor = open.pressedTextColor = new Color32(25, 48, 64, 255);
            open.eventClicked += (component, click) => ExportActions.DoExportFromRendererButton();
            y += 58f;
            float exportWidth = Mathf.Min(200f, buttonWidth);
            UILabel exportHint = Label("EXPORT_FILE_HINT", width - exportWidth - 12f, ref y, 42f);
            exportHint.textColor = new Color32(180, 200, 215, 255);
            UIButton export = Button("EXPORT_MAP", width - exportWidth, y - 42f, exportWidth);
            export.eventClicked += (component, click) => ExportActions.DoExport(RendererTarget.None);
            UILabel hint = Label(ExportActions.SafeTmpeAvailable() ? "TMPE_READY_HINT" : "TMPE_OPTIONAL_HINT", width, ref y, 46f);
            hint.textColor = new Color32(180, 200, 215, 255);

            Title("STATUS_GROUP", width, ref y);
            UILabel status = Label("EXPORT_IDLE", width, ref y, 96f);
            UILabel lastExport = Label("LAST_EXPORT", width, ref y, 40f);
            UILabel counts = Label("LAST_COUNTS", width, ref y, 28f);

            UIProgressBar progress = AddUIComponent<UIProgressBar>();
            progress.atlas = UIView.GetAView().defaultAtlas;
            progress.backgroundSprite = "GenericProgressBar";
            progress.progressSprite = "GenericProgressBarFill";
            progress.progressColor = new Color32(146, 190, 215, 255);
            progress.minValue = 0f; progress.maxValue = 100f;
            progress.size = new Vector2(width, 12f);
            progress.relativePosition = new Vector2(0f, y);
            progress.isVisible = false;
            y += 16f;
            UILabel progressLabel = Label("", width, ref y, 30f);
            progressLabel.isVisible = false;

            Title("OUTPUT_GROUP", width, ref y);
            UILabel path = Label("", width, ref y, 50f);
            path.text = CSLModernMapConfig.LoadOrDefault().GetOutputDirPath();
            path.textColor = new Color32(200, 210, 220, 255);
            path.tooltip = path.text;
            UIButton outputFolder = Button("OPEN_OUTPUT_FOLDER", 0f, y, buttonWidth);
            outputFolder.eventClicked += (component, click) => ExportActions.OpenOutputDir();
            UIButton copy = Button("COPY_OUTPUT_FOLDER", buttonWidth + 12f, y, buttonWidth);
            copy.eventClicked += (component, click) => ExportActions.CopyOutputDir();
            y += 44f;

            Title("RENDERER_GROUP", width, ref y);
            UILabel rendererStatus = Label("RENDERER_STATUS", width, ref y, 30f);
            UIButton install = Button("INSTALL_RENDERER", 0f, y, buttonWidth);
            install.eventClicked += (component, click) => ExportActions.InstallRenderer();
            UIButton rendererFolder = Button("OPEN_RENDERER_FOLDER", buttonWidth + 12f, y, buttonWidth);
            rendererFolder.eventClicked += (component, click) => ExportActions.OpenRendererFolder();
            y += 44f;

            UISpacers.AddOptionsSpacer(this, 0f, y, width);
            y += 14f;
            Label("", width, ref y, 28f).text = "CSLModernMap v" + CSLModernMap.MOD_VERSION + "  ·  CMM";
            height = y;
            ExportTaskRunner.Ensure();
            ExportActions.Register(export, open, install, lastExport, counts, rendererStatus, progress, progressLabel, status);
        }

        private void Title(string key, float width, ref float y)
        {
            UISpacers.AddOptionsSpacer(this, 0f, y, width);
            UILabel title = UILabels.AddLabel(this, 0f, y + 14f, Translations.Translate(key), width, 0.9f);
            title.font = UIFonts.SemiBold;
            y += 42f;
        }

        private UILabel Label(string key, float width, ref float y, float reservedHeight)
        {
            UILabel label = UILabels.AddLabel(this, 0f, y, key.Length == 0 ? "" : Translations.Translate(key), width, 0.75f);
            label.autoHeight = false;
            label.height = reservedHeight - 6f;
            label.clipChildren = true;
            y += reservedHeight;
            return label;
        }

        private UIButton Button(string key, float x, float y, float width)
        {
            UIButton button = UIButtons.AddButton(this, x, y, Translations.Translate(key), width, 32f, 0.8f);
            button.color = button.focusedColor = new Color32(76, 87, 98, 255);
            button.hoveredColor = new Color32(94, 108, 121, 255);
            button.pressedColor = new Color32(61, 71, 82, 255);
            button.textColor = button.focusedTextColor = button.hoveredTextColor = button.pressedTextColor = new Color32(235, 240, 245, 255);
            return button;
        }
    }
}
