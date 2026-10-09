using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace CodexRateMonitorNative
{
    internal sealed class AppearanceSettingsForm : DpiAwareDialog
    {
        private readonly Action<MonitorSettings> onPreview;
        private readonly Action<MonitorSettings> onSave;
        private readonly Action onCancel;
        private readonly Dictionary<string, Button> colorButtons = new Dictionary<string, Button>();
        private MonitorSettings working;
        private bool loading;
        private bool committed;

        private readonly OverlayPreviewControl preview;
        private readonly RadioButton bottomPosition;
        private readonly RadioButton topPosition;
        private readonly RadioButton showRemaining;
        private readonly RadioButton showUsed;
        private readonly RadioButton desktopModeRadio;
        private readonly RadioButton attachModeRadio;
        private readonly RadioButton oneLineRadio;
        private readonly RadioButton twoLinesRadio;
        private readonly CheckBox showResetCredits;
        private readonly ComboBox language;
        private GroupBox positionGroup;
        private TableLayoutPanel positionLinesRow;
        private readonly ComboBox fontFamily;
        private readonly NumericUpDown fontSize;
        private readonly NumericUpDown resetFontSize;
        private readonly NumericUpDown scale;
        private decimal lastScaleInput;
        private readonly NumericUpDown opacity;
        private readonly NumericUpDown cornerRadius;

        public AppearanceSettingsForm(
            MonitorSettings initial,
            Action<MonitorSettings> previewCallback,
            Action<MonitorSettings> saveCallback,
            Action cancelCallback)
        {
            working = initial.Clone();
            onPreview = previewCallback;
            onSave = saveCallback;
            onCancel = cancelCallback;

            // Capture the complete logical layout before applying monitor DPI.
            SuspendLayout();
            Text = I18n.T("SettingsTitle");
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            Font = CreateUiFont(9.5f);
            ClientSize = new Size(820, 760);
            BackColor = Color.FromArgb(246, 247, 249);

            var content = new Panel();
            content.Dock = DockStyle.Fill;
            content.AutoScroll = true;
            Controls.Add(content);

            var root = new TableLayoutPanel();
            // A top-docked child is excluded from horizontal AutoScroll in
            // WinForms. Keep explicit bounds so narrow screens can scroll too.
            root.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            root.MinimumSize = new Size(640, 0);
            root.Padding = new Padding(20, 16, 20, 8);
            root.ColumnCount = 1;
            root.RowCount = 8;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int row = 0; row < 8; row++)
                root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            content.Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);

            preview = new OverlayPreviewControl();
            preview.Dock = DockStyle.Fill;
            preview.AutoSize = true;
            preview.Margin = new Padding(0, 0, 0, 12);
            root.Controls.Add(preview, 0, 1);

            bottomPosition = new RadioButton();
            bottomPosition.Text = I18n.T("BottomPosition");
            bottomPosition.AutoSize = true;
            bottomPosition.Margin = new Padding(0, 4, 0, 0);
            bottomPosition.CheckedChanged += ControlChanged;

            topPosition = new RadioButton();
            topPosition.Text = I18n.T("TopRecommended");
            topPosition.AutoSize = true;
            topPosition.Margin = new Padding(4, 4, 18, 0);
            topPosition.CheckedChanged += ControlChanged;

            oneLineRadio = new RadioButton();
            oneLineRadio.Text = I18n.T("OneLine");
            oneLineRadio.AutoSize = true;
            oneLineRadio.Margin = new Padding(4, 3, 18, 0);
            oneLineRadio.CheckedChanged += ControlChanged;
            twoLinesRadio = new RadioButton();
            twoLinesRadio.Text = I18n.T("TwoLines");
            twoLinesRadio.AutoSize = true;
            twoLinesRadio.Margin = new Padding(0, 3, 0, 0);
            twoLinesRadio.CheckedChanged += ControlChanged;

            showRemaining = new RadioButton();
            showRemaining.Text = I18n.T("ShowRemaining");
            showRemaining.AutoSize = true;
            showRemaining.Margin = new Padding(4, 3, 18, 0);
            showRemaining.CheckedChanged += ControlChanged;

            showUsed = new RadioButton();
            showUsed.Text = I18n.T("ShowUsed");
            showUsed.AutoSize = true;
            showUsed.Margin = new Padding(0, 3, 0, 0);
            showUsed.CheckedChanged += ControlChanged;

            language = new ComboBox();
            language.DropDownStyle = ComboBoxStyle.DropDownList;
            language.Width = 142;
            language.Margin = new Padding(4, 1, 0, 0);
            language.Items.Add(new LanguageOption("auto", I18n.T("LanguageAuto")));
            language.Items.Add(new LanguageOption("zh-CN", "简体中文"));
            language.Items.Add(new LanguageOption("zh-TW", "繁體中文"));
            language.Items.Add(new LanguageOption("en", "English"));
            language.SelectedIndexChanged += ControlChanged;

            // ---- Display mode: occupies its own full-width row on row 2. ----
            var modeGroup = CreateGroup(I18n.T("DisplayMode"));
            var modePanel = new SettingsFlowPanel();
            modePanel.Dock = DockStyle.Top;
            modePanel.AutoSize = true;
            modePanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            modePanel.WrapContents = true;
            modePanel.Padding = new Padding(4, 6, 4, 4);
            desktopModeRadio = new RadioButton();
            desktopModeRadio.Text = I18n.T("DesktopFloatingMode");
            desktopModeRadio.AutoSize = true;
            desktopModeRadio.Margin = new Padding(4, 2, 22, 0);
            desktopModeRadio.CheckedChanged += ControlChanged;
            attachModeRadio = new RadioButton();
            attachModeRadio.Text = I18n.T("WindowAttachMode");
            attachModeRadio.AutoSize = true;
            attachModeRadio.Margin = new Padding(0, 2, 0, 0);
            attachModeRadio.CheckedChanged += ControlChanged;
            showResetCredits = new CheckBox();
            showResetCredits.Text = I18n.T("ShowResetCredits");
            showResetCredits.AutoSize = true;
            showResetCredits.Margin = new Padding(22, 2, 0, 0);
            showResetCredits.CheckedChanged += ControlChanged;
            modePanel.Controls.Add(desktopModeRadio);
            modePanel.Controls.Add(attachModeRadio);
            modePanel.Controls.Add(showResetCredits);
            modeGroup.Controls.Add(modePanel);
            root.Controls.Add(modeGroup, 0, 2);

            // ---- Display position + Display lines: side-by-side on row 3 using two
            // distinct GroupBox containers (no divider line). When desktop floating
            // mode is selected, the position column collapses to 0% and the lines
            // group expands to fill the full row and determines its content height.
            positionLinesRow = new TableLayoutPanel();
            positionLinesRow.Dock = DockStyle.Top;
            positionLinesRow.AutoSize = true;
            positionLinesRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            // Outer margin is zero by design: the inner GroupBoxes already carry
            // their own 10px bottom margin, and stacking another 10px here would
            // squeeze the radio buttons again.
            positionLinesRow.Margin = Padding.Empty;
            positionLinesRow.ColumnCount = 2;
            positionLinesRow.RowCount = 1;
            positionLinesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            positionLinesRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            positionLinesRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            positionGroup = CreateGroup(I18n.T("DisplayPosition"));
            var positionPanel = new SettingsFlowPanel();
            positionPanel.Dock = DockStyle.Top;
            positionPanel.AutoSize = true;
            positionPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            positionPanel.WrapContents = true;
            positionPanel.Padding = new Padding(4, 6, 4, 4);
            positionPanel.Controls.Add(topPosition);
            positionPanel.Controls.Add(bottomPosition);
            positionGroup.Controls.Add(positionPanel);
            positionLinesRow.Controls.Add(positionGroup, 0, 0);

            var linesGroup = CreateGroup(I18n.T("DisplayLines"));
            var linesPanel = new SettingsFlowPanel();
            linesPanel.Dock = DockStyle.Top;
            linesPanel.AutoSize = true;
            linesPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            linesPanel.WrapContents = true;
            linesPanel.Padding = new Padding(4, 6, 4, 4);
            linesPanel.Controls.Add(oneLineRadio);
            linesPanel.Controls.Add(twoLinesRadio);
            linesGroup.Controls.Add(linesPanel);
            positionLinesRow.Controls.Add(linesGroup, 1, 0);

            root.Controls.Add(positionLinesRow, 0, 3);

            // ---- Progress display + Language: side-by-side in a single row ----
            var progressLangRow = new TableLayoutPanel();
            progressLangRow.Dock = DockStyle.Top;
            progressLangRow.AutoSize = true;
            progressLangRow.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            // Outer margin must be zero here, otherwise it stacks on top of the
            // GroupBox children's own 10px bottom margin and squeezes the radios.
            progressLangRow.Margin = Padding.Empty;
            progressLangRow.ColumnCount = 2;
            progressLangRow.RowCount = 1;
            progressLangRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            progressLangRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            progressLangRow.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var progressGroup = CreateGroup(I18n.T("ProgressDisplay"));
            var progressPanel = new SettingsFlowPanel();
            progressPanel.Dock = DockStyle.Top;
            progressPanel.AutoSize = true;
            progressPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            progressPanel.WrapContents = true;
            progressPanel.Padding = new Padding(4, 6, 4, 4);
            progressPanel.Controls.Add(showRemaining);
            progressPanel.Controls.Add(showUsed);
            progressGroup.Controls.Add(progressPanel);
            progressLangRow.Controls.Add(progressGroup, 0, 0);

            var languageGroup = CreateGroup(I18n.T("Language"));
            var languagePanel = new SettingsFlowPanel();
            languagePanel.Dock = DockStyle.Top;
            languagePanel.AutoSize = true;
            languagePanel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            languagePanel.WrapContents = true;
            languagePanel.Padding = new Padding(4, 6, 4, 4);
            languagePanel.Controls.Add(language);
            languageGroup.Controls.Add(languagePanel);
            progressLangRow.Controls.Add(languageGroup, 1, 0);

            root.Controls.Add(progressLangRow, 0, 4);

            fontFamily = new ComboBox();
            fontFamily.DropDownStyle = ComboBoxStyle.DropDown;
            fontFamily.AutoCompleteMode = AutoCompleteMode.SuggestAppend;
            fontFamily.AutoCompleteSource = AutoCompleteSource.ListItems;
            fontFamily.Width = 210;
            try
            {
                using (var fonts = new InstalledFontCollection())
                {
                    var installed = new HashSet<string>(
                        fonts.Families.Select(delegate(FontFamily family) { return family.Name; }),
                        StringComparer.OrdinalIgnoreCase);
                    string[] preferred =
                    {
                        "Microsoft YaHei UI",
                        "Microsoft JhengHei UI",
                        "Segoe UI",
                        "Arial",
                        "Calibri",
                        "Consolas",
                        "Times New Roman",
                        "SimSun"
                    };
                    var choices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string name in preferred.Where(installed.Contains))
                        choices.Add(name);
                    if (!string.IsNullOrWhiteSpace(working.Style.FontFamily) &&
                        installed.Contains(working.Style.FontFamily))
                        choices.Add(working.Style.FontFamily);
                    foreach (string name in choices.OrderBy(
                        delegate(string value) { return value; },
                        StringComparer.OrdinalIgnoreCase))
                        fontFamily.Items.Add(name);
                    if (fontFamily.Items.Count == 0)
                        fontFamily.Items.Add(SystemFonts.MessageBoxFont.FontFamily.Name);
                }
            }
            catch
            {
            }
            fontFamily.TextChanged += ControlChanged;

            fontSize = CreateNumber(10, 22, 1, 0);
            resetFontSize = CreateNumber(9, 18, 1, 0);
            scale = CreateNumber((decimal)(StyleSettings.MinimumScale * 100),
                (decimal)(StyleSettings.MaximumScale * 100), 1, 2);
            opacity = CreateNumber(50, 100, 5, 0);
            cornerRadius = CreateNumber(0, 20, 1, 0);

            var typographyGroup = CreateGroup(I18n.T("Typography"));
            typographyGroup.AutoSize = true;
            typographyGroup.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            typographyGroup.Controls.Add(BuildTypographyTable());
            root.Controls.Add(typographyGroup, 0, 5);

            var colorsGroup = CreateGroup(I18n.T("Colors"));
            colorsGroup.Controls.Add(BuildColorsPanel());
            root.Controls.Add(colorsGroup, 0, 6);

            var hint = new Label();
            hint.Dock = DockStyle.Top;
            hint.AutoSize = true;
            hint.Margin = new Padding(0, 0, 0, 8);
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.ForeColor = Color.FromArgb(103, 112, 123);
            hint.Text = I18n.T("SettingsHint");
            root.Controls.Add(hint, 0, 7);

            // Keep actions visible even when the scaled content needs scrolling.
            var footer = new Panel();
            footer.Dock = DockStyle.Bottom;
            footer.Height = 66;
            footer.Padding = new Padding(20, 0, 20, 14);
            footer.Controls.Add(BuildButtons());
            Controls.Add(footer);

            LoadControls();
            FormClosed += delegate
            {
                if (!committed && onCancel != null)
                    onCancel();
            };
            InitializeDpiLayout(new Size(820, 760));
            ResumeLayout(true);
            content.ClientSizeChanged += delegate { LayoutContent(content, root); };
            root.Layout += delegate { LayoutContent(content, root); };
            LayoutContent(content, root);
        }

        private static void LayoutContent(Panel content, TableLayoutPanel root)
        {
            root.Width = Math.Max(root.MinimumSize.Width, content.ClientSize.Width);
            int preferred = root.GetPreferredSize(new Size(root.Width, 0)).Height;
            int contentBottom = root.Controls.Cast<Control>().Max(delegate(Control control)
            {
                return control.Bottom + control.Margin.Bottom;
            });
            root.Height = Math.Max(preferred, contentBottom + root.Padding.Bottom);
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            FitToWorkingArea(Screen.FromControl(this).WorkingArea);
        }

        public void SetPreviewDpi(int dpi)
        {
            preview.TargetDpi = dpi;
        }

        protected override void OnDpiLayoutChanged()
        {
            FitToWorkingArea(Screen.FromControl(this).WorkingArea);
        }

        private void FitToWorkingArea(Rectangle workingArea)
        {
            int margin = (int)Math.Ceiling(12d * LayoutDpi / 96d);
            Rectangle available = Rectangle.Inflate(workingArea, -margin, -margin);
            var content = Controls.OfType<Panel>().Single(delegate(Panel panel) { return panel.AutoScroll; });
            var footer = Controls.OfType<Panel>().Single(delegate(Panel panel) { return panel.Dock == DockStyle.Bottom; });
            var root = (TableLayoutPanel)content.Controls[0];
            MaximumSize = Size.Empty;
            int preferredWidth = (int)Math.Round(820d * LayoutDpi / 96d);
            Width = Math.Min(preferredWidth + Width - ClientSize.Width, available.Width);
            LayoutContent(content, root);
            int preferredHeight = root.Height + footer.Height + Height - ClientSize.Height;
            MaximumSize = available.Size;
            Size = new Size(Width, Math.Min(preferredHeight, available.Height));
            Location = new Point(
                available.Left + (available.Width - Width) / 2,
                available.Top + (available.Height - Height) / 2);
        }

        private Control BuildHeader()
        {
            var panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Top;
            panel.AutoSize = true;
            panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            panel.Margin = new Padding(0, 0, 0, 8);
            panel.ColumnCount = 1;
            panel.RowCount = 2;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var title = new Label();
            title.Dock = DockStyle.Fill;
            title.AutoSize = true;
            title.Margin = new Padding(0);
            title.Font = CreateUiFont(15f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(28, 31, 36);
            title.Text = I18n.T("AppearanceTitle");
            title.TextAlign = ContentAlignment.BottomLeft;
            panel.Controls.Add(title, 0, 0);

            var subtitle = new Label();
            subtitle.Dock = DockStyle.Fill;
            subtitle.AutoSize = true;
            subtitle.Margin = new Padding(1, 3, 0, 0);
            subtitle.ForeColor = Color.FromArgb(103, 112, 123);
            subtitle.Text = I18n.T("AppearanceSubtitle");
            subtitle.TextAlign = ContentAlignment.TopLeft;
            panel.Controls.Add(subtitle, 0, 1);
            return panel;
        }

        private GroupBox CreateGroup(string title)
        {
            var group = new SettingsGroupBox();
            group.Text = title;
            group.Dock = DockStyle.Top;
            group.AutoSize = true;
            group.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            group.Margin = new Padding(0, 0, 0, 10);
            group.Padding = new Padding(10, 4, 10, 6);
            group.ForeColor = Color.FromArgb(42, 46, 52);
            return group;
        }

        private NumericUpDown CreateNumber(decimal min, decimal max, decimal increment, int decimals)
        {
            var control = new NumericUpDown();
            // The dialog applies this composite control's logical dimensions;
            // inherited autoscaling must not multiply them a second time.
            control.AutoScaleMode = AutoScaleMode.None;
            control.Minimum = min;
            control.Maximum = max;
            control.Increment = increment;
            control.DecimalPlaces = decimals;
            control.Width = 82;
            control.Margin = Padding.Empty;
            control.TextAlign = HorizontalAlignment.Right;
            control.ValueChanged += ControlChanged;
            return control;
        }

        private Control BuildTypographyTable()
        {
            var table = new TableLayoutPanel();
            table.Dock = DockStyle.Top;
            table.AutoSize = true;
            table.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            table.Padding = new Padding(4, 4, 4, 2);
            table.ColumnCount = 4;
            table.RowCount = 4;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 4; i++)
                table.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            AddField(table, 0, 0, I18n.T("Font"), fontFamily);
            table.SetColumnSpan(table.GetControlFromPosition(1, 0), 3);
            AddField(table, 0, 1, I18n.T("MainFontSize"), fontSize);
            AddField(table, 2, 1, I18n.T("TimeFontSize"), resetFontSize);
            AddField(table, 0, 2, I18n.T("Scale"), scale, "%");
            AddField(table, 2, 2, I18n.T("Opacity"), opacity, "%");
            AddField(table, 0, 3, I18n.T("CornerRadius"), cornerRadius);
            return table;
        }

        private void AddField(
            TableLayoutPanel table,
            int column,
            int row,
            string labelText,
            Control control,
            string suffix)
        {
            var label = new Label();
            label.Text = labelText;
            label.AutoSize = true;
            label.TextAlign = ContentAlignment.MiddleRight;
            label.Dock = DockStyle.Fill;
            table.Controls.Add(label, column, row);

            var panel = new SettingsFlowPanel();
            panel.Dock = DockStyle.Fill;
            panel.AutoSize = true;
            panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            panel.WrapContents = false;
            panel.Margin = new Padding(4, 3, 4, 2);
            panel.Controls.Add(control);
            if (!string.IsNullOrEmpty(suffix))
            {
                var suffixLabel = new Label();
                suffixLabel.Text = suffix;
                suffixLabel.AutoSize = true;
                suffixLabel.Margin = new Padding(2, 5, 0, 0);
                suffixLabel.ForeColor = Color.FromArgb(103, 112, 123);
                panel.Controls.Add(suffixLabel);
            }
            table.Controls.Add(panel, column + 1, row);
        }

        private void AddField(
            TableLayoutPanel table,
            int column,
            int row,
            string labelText,
            Control control)
        {
            AddField(table, column, row, labelText, control, null);
        }

        private Control BuildColorsPanel()
        {
            var container = new TableLayoutPanel();
            container.Dock = DockStyle.Top;
            container.AutoSize = true;
            container.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            container.ColumnCount = 1;
            container.RowCount = 2;
            container.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            container.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            container.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Top;
            grid.AutoSize = true;
            grid.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            grid.Padding = new Padding(4, 4, 4, 0);
            grid.ColumnCount = 5;
            grid.RowCount = 2;
            for (int i = 0; i < 5; i++)
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            AddColorPicker(grid, 0, 0, I18n.T("OuterBackground"), "Background");
            AddColorPicker(grid, 1, 0, I18n.T("RowBackground"), "CardBackground");
            AddColorPicker(grid, 2, 0, I18n.T("Border"), "Border");
            AddColorPicker(grid, 3, 0, I18n.T("ProgressTrack"), "Track");
            AddColorPicker(grid, 4, 0, I18n.T("MainText"), "Text");
            AddColorPicker(grid, 0, 1, I18n.T("TimeText"), "MutedText");
            AddColorPicker(grid, 1, 1, I18n.T("FiveHourColor"), "Primary");
            AddColorPicker(grid, 2, 1, I18n.T("SevenDayColor"), "Secondary");
            AddColorPicker(grid, 3, 1, I18n.T("Warning"), "Warning");
            AddColorPicker(grid, 4, 1, I18n.T("Danger"), "Danger");
            bool arranging = false;
            grid.Layout += delegate
            {
                if (arranging || grid.ClientSize.Width <= 0) return;
                Control[] pickers = grid.Controls.Cast<Control>().ToArray();
                int cellWidth = pickers.Max(delegate(Control picker)
                {
                    return picker.GetPreferredSize(Size.Empty).Width + picker.Margin.Horizontal;
                });
                int columns = Math.Max(1, Math.Min(5,
                    (grid.ClientSize.Width - grid.Padding.Horizontal) / Math.Max(1, cellWidth)));
                if (grid.ColumnCount == columns) return;
                arranging = true;
                try
                {
                    grid.SuspendLayout();
                    grid.ColumnCount = columns;
                    grid.RowCount = (pickers.Length + columns - 1) / columns;
                    grid.ColumnStyles.Clear();
                    grid.RowStyles.Clear();
                    for (int column = 0; column < columns; column++)
                        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
                    for (int row = 0; row < grid.RowCount; row++)
                        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                    for (int item = 0; item < pickers.Length; item++)
                        grid.SetCellPosition(pickers[item], new TableLayoutPanelCellPosition(item % columns, item / columns));
                }
                finally { grid.ResumeLayout(true); arranging = false; }
            };
            container.Controls.Add(grid, 0, 0);

            var presets = new SettingsFlowPanel();
            presets.Dock = DockStyle.Top;
            presets.AutoSize = true;
            presets.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            presets.FlowDirection = FlowDirection.RightToLeft;
            presets.WrapContents = false;
            var dark = CreateSecondaryButton(I18n.T("DarkPreset"));
            dark.Click += delegate { ApplyPreset(true); };
            presets.Controls.Add(dark);
            var light = CreateSecondaryButton(I18n.T("LightPreset"));
            light.Click += delegate { ApplyPreset(false); };
            presets.Controls.Add(light);
            container.Controls.Add(presets, 0, 1);
            return container;
        }

        private void AddColorPicker(
            TableLayoutPanel grid,
            int column,
            int row,
            string label,
            string property)
        {
            var panel = new SettingsFlowPanel();
            panel.Dock = DockStyle.Fill;
            panel.AutoSize = true;
            panel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            panel.WrapContents = false;
            panel.Margin = new Padding(2);

            var button = new Button();
            button.Size = new Size(28, 24);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(190, 195, 202);
            button.Tag = property;
            button.Click += ColorButtonClicked;
            colorButtons[property] = button;
            panel.Controls.Add(button);

            var text = new Label();
            text.Text = label;
            text.AutoSize = true;
            text.Margin = new Padding(5, 5, 0, 0);
            panel.Controls.Add(text);
            grid.Controls.Add(panel, column, row);
        }

        private Control BuildButtons()
        {
            var panel = new SettingsFlowPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.RightToLeft;
            panel.WrapContents = false;

            var save = new Button();
            save.Text = I18n.T("SaveClose");
            save.AutoSize = true;
            save.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            save.MinimumSize = new Size(75, 32);
            save.Height = 32;
            save.Padding = new Padding(12, 0, 12, 0);
            save.BackColor = Color.FromArgb(38, 38, 38);
            save.ForeColor = Color.White;
            save.FlatStyle = FlatStyle.Flat;
            save.FlatAppearance.BorderSize = 0;
            save.Click += delegate
            {
                UpdateWorking();
                committed = true;
                if (onSave != null)
                    onSave(working.Clone());
                Close();
            };
            panel.Controls.Add(save);

            var cancel = CreateSecondaryButton(I18n.T("Cancel"));
            cancel.Click += delegate { Close(); };
            panel.Controls.Add(cancel);

            var reset = CreateSecondaryButton(I18n.T("RestoreDefault"));
            reset.Click += delegate
            {
                working = new MonitorSettings();
                LoadControls();
            };
            panel.Controls.Add(reset);
            return panel;
        }

        private Button CreateSecondaryButton(string text)
        {
            var button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(75, 32);
            button.Height = 32;
            button.Padding = new Padding(8, 0, 8, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = Color.FromArgb(190, 195, 202);
            button.BackColor = Color.White;
            return button;
        }

        private void LoadControls()
        {
            loading = true;
            try
            {
                topPosition.Checked = working.Position == "top";
                bottomPosition.Checked = working.Position == "bottom-right";
                oneLineRadio.Checked = working.DisplayLines != "2";
                twoLinesRadio.Checked = working.DisplayLines == "2";
                showRemaining.Checked = UsageDisplayTools.IsRemaining(working.UsageDisplay);
                showUsed.Checked = !UsageDisplayTools.IsRemaining(working.UsageDisplay);
                desktopModeRadio.Checked = working.OverlayMode == "desktop";
                attachModeRadio.Checked = working.OverlayMode == "attach";
                showResetCredits.Checked = working.ShowResetCredits;
                string languageCode = I18n.NormalizeSetting(working.Language);
                for (int i = 0; i < language.Items.Count; i++)
                {
                    var option = language.Items[i] as LanguageOption;
                    if (option != null &&
                        string.Equals(option.Code, languageCode, StringComparison.OrdinalIgnoreCase))
                    {
                        language.SelectedIndex = i;
                        break;
                    }
                }
                fontFamily.Text = working.Style.FontFamily;
                fontSize.Value = ClampDecimal((decimal)working.Style.FontSize, fontSize);
                resetFontSize.Value = ClampDecimal((decimal)working.Style.ResetFontSize, resetFontSize);
                scale.Value = ClampDecimal(Math.Round((decimal)(working.Style.Scale * 100), 2), scale);
                lastScaleInput = scale.Value;
                opacity.Value = ClampDecimal((decimal)(working.Style.Opacity * 100), opacity);
                cornerRadius.Value = ClampDecimal((decimal)working.Style.CornerRadius, cornerRadius);
                foreach (KeyValuePair<string, Button> item in colorButtons)
                    SetColorButton(item.Value, GetStyleColor(item.Key));
            }
            finally
            {
                loading = false;
            }
            UpdatePositionGroupVisibility();
            Preview();
        }

        private static decimal ClampDecimal(decimal value, NumericUpDown control)
        {
            return Math.Max(control.Minimum, Math.Min(control.Maximum, value));
        }

        private void ControlChanged(object sender, EventArgs e)
        {
            if (loading)
                return;
            UpdateWorking();
            UpdatePositionGroupVisibility();
            Preview();
        }

        // The display-position option only applies to window-attach mode. When the
        // desktop floating mode is selected, hide the position group and collapse
        // its column to 0% so the display-lines group expands to fill the whole
        // row. The row follows the visible group's preferred height.
        private void UpdatePositionGroupVisibility()
        {
            bool attach = attachModeRadio != null && attachModeRadio.Checked;
            if (positionGroup != null)
                positionGroup.Visible = attach;
            if (positionLinesRow != null)
            {
                if (attach)
                {
                    positionLinesRow.ColumnStyles[0].Width = 50;
                    positionLinesRow.ColumnStyles[1].Width = 50;
                }
                else
                {
                    positionLinesRow.ColumnStyles[0].Width = 0;
                    positionLinesRow.ColumnStyles[1].Width = 100;
                }
                positionLinesRow.PerformLayout();
            }
        }

        private void UpdateWorking()
        {
            working.Position = bottomPosition.Checked ? "bottom-right" : "top";
            working.DisplayLines = twoLinesRadio.Checked ? "2" : "1";
            working.UsageDisplay = showUsed.Checked ? "used" : "remaining";
            working.OverlayMode = desktopModeRadio.Checked ? "desktop" : "attach";
            working.ShowResetCredits = showResetCredits.Checked;
            var selectedLanguage = language.SelectedItem as LanguageOption;
            working.Language = selectedLanguage == null ? "auto" : selectedLanguage.Code;
            working.Style.FontFamily = string.IsNullOrWhiteSpace(fontFamily.Text)
                ? "Microsoft YaHei UI"
                : fontFamily.Text.Trim();
            working.Style.FontSize = (double)fontSize.Value;
            working.Style.ResetFontSize = (double)resetFontSize.Value;
            // The displayed percentage may be rounded after legacy migration.
            // Preserve its exact scale unless the user actually edits this input.
            if (scale.Value != lastScaleInput)
            {
                working.Style.Scale = (double)scale.Value / 100.0;
                lastScaleInput = scale.Value;
            }
            working.Style.Opacity = (double)opacity.Value / 100.0;
            working.Style.CornerRadius = (double)cornerRadius.Value;
        }

        private void Preview()
        {
            preview.Settings = working.Clone();
            if (onPreview != null)
                onPreview(working.Clone());
        }

        private void ColorButtonClicked(object sender, EventArgs e)
        {
            var button = sender as Button;
            if (button == null)
                return;
            string property = Convert.ToString(button.Tag, CultureInfo.InvariantCulture);
            using (var dialog = new ColorDialog())
            {
                dialog.FullOpen = true;
                dialog.Color = ColorTools.Parse(GetStyleColor(property));
                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                string value = string.Format(
                    CultureInfo.InvariantCulture,
                    "#{0:X2}{1:X2}{2:X2}",
                    dialog.Color.R,
                    dialog.Color.G,
                    dialog.Color.B);
                SetStyleColor(property, value);
                SetColorButton(button, value);
                Preview();
            }
        }

        private void SetColorButton(Button button, string value)
        {
            Color color = ColorTools.Parse(value);
            button.BackColor = Color.FromArgb(color.R, color.G, color.B);
            button.Text = string.Empty;
        }

        private string GetStyleColor(string property)
        {
            switch (property)
            {
                case "Background": return working.Style.Background;
                case "CardBackground": return working.Style.CardBackground;
                case "Border": return working.Style.Border;
                case "Text": return working.Style.Text;
                case "MutedText": return working.Style.MutedText;
                case "Track": return working.Style.Track;
                case "Primary": return working.Style.Primary;
                case "Secondary": return working.Style.Secondary;
                case "Warning": return working.Style.Warning;
                case "Danger": return working.Style.Danger;
                default: return "#000000";
            }
        }

        private void SetStyleColor(string property, string value)
        {
            switch (property)
            {
                case "Background": working.Style.Background = value; break;
                case "CardBackground": working.Style.CardBackground = value; break;
                case "Border": working.Style.Border = value; break;
                case "Text": working.Style.Text = value; break;
                case "MutedText": working.Style.MutedText = value; break;
                case "Track": working.Style.Track = value; break;
                case "Primary": working.Style.Primary = value; break;
                case "Secondary": working.Style.Secondary = value; break;
                case "Warning": working.Style.Warning = value; break;
                case "Danger": working.Style.Danger = value; break;
            }
        }

        private void ApplyPreset(bool dark)
        {
            if (dark)
            {
                working.Style.Background = "#25262A";
                working.Style.CardBackground = "#303137";
                working.Style.Border = "#484A52";
                working.Style.Text = "#F2F2F2";
                working.Style.MutedText = "#B6B8C0";
                working.Style.Track = "#44464E";
                working.Style.Primary = "#6EA0FF";
                working.Style.Secondary = "#B18AF2";
                working.Style.Warning = "#F0B44D";
                working.Style.Danger = "#F06A6A";
            }
            else
            {
                working.Style.Background = "#F7F7F5";
                working.Style.CardBackground = "#FFFFFF";
                working.Style.Border = "#D8D8D4";
                working.Style.Text = "#252525";
                working.Style.MutedText = "#727272";
                working.Style.Track = "#EAEAE7";
                working.Style.Primary = "#4F8CFF";
                working.Style.Secondary = "#8A63D2";
                working.Style.Warning = "#E6A23C";
                working.Style.Danger = "#E45757";
            }
            LoadControls();
        }

        private static Font CreateUiFont(float size)
        {
            return CreateUiFont(size, FontStyle.Regular);
        }

        private static Font CreateUiFont(float size, FontStyle style)
        {
            try
            {
                return new Font("Microsoft YaHei UI", size * 96f / 72f, style, GraphicsUnit.Pixel);
            }
            catch
            {
                return new Font(SystemFonts.MessageBoxFont.FontFamily, size * 96f / 72f, style, GraphicsUnit.Pixel);
            }
        }
    }

    internal sealed class SettingsGroupBox : GroupBox
    {
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size preferred = base.GetPreferredSize(proposedSize);
            int width = Width > 1 ? Width : proposedSize.Width;
            int innerWidth = Math.Max(1, width - Padding.Horizontal);
            int height = 0;
            foreach (Control control in Controls)
            {
                if (control.Visible)
                    height = Math.Max(height,
                        control.GetPreferredSize(new Size(innerWidth, 0)).Height + control.Margin.Vertical);
            }
            // Measure wrapped children at the group's available width, rather
            // than the unbounded width used by GroupBox's default autosizing.
            preferred.Height = DisplayRectangle.Top + height + Padding.Bottom;
            return preferred;
        }
    }

    internal sealed class SettingsFlowPanel : FlowLayoutPanel
    {
        public override Size GetPreferredSize(Size proposedSize)
        {
            Size preferred = base.GetPreferredSize(proposedSize);
            // Native edit controls can round their actual height above the
            // preferred height during a DPI/font change. Reserve that difference.
            int rowHeight = 0;
            foreach (Control control in Controls)
            {
                if (control.Visible)
                    rowHeight = Math.Max(rowHeight, control.Height + control.Margin.Vertical);
            }
            preferred.Height = Math.Max(preferred.Height, Padding.Vertical + rowHeight);
            return preferred;
        }
    }

    internal sealed class OverlayPreviewControl : Control
    {
        private MonitorSettings settings = new MonitorSettings();
        private int targetDpi;
        private readonly RateSnapshot sample = new RateSnapshot
        {
            Primary = new WindowUsage { UsedPercent = 35,
                ResetsAt = new DateTimeOffset(DateTime.Today.AddDays(1).AddHours(3).AddMinutes(25)).ToUnixTimeSeconds() },
            Secondary = new WindowUsage { UsedPercent = 5,
                ResetsAt = new DateTimeOffset(DateTime.Today.AddDays(7).AddHours(22).AddMinutes(25)).ToUnixTimeSeconds() }
        };
        private readonly ResetCreditsInfo sampleCredits = new ResetCreditsInfo
        {
            AvailableCount = 2,
            EarliestExpiry = DateTime.Today.AddDays(5),
            EarliestGranted = DateTime.Today.AddDays(-20)
        };

        public OverlayPreviewControl()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(236, 239, 243);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                ControlStyles.OptimizedDoubleBuffer, true);
        }

        public MonitorSettings Settings
        {
            get { return settings; }
            set { settings = value ?? new MonitorSettings(); RefreshLayout(); }
        }

        // Use the overlay's monitor DPI even when settings are on another screen.
        public int TargetDpi
        {
            get { return targetDpi > 0 ? targetDpi : DeviceDpi; }
            set
            {
                if (targetDpi == value) return;
                targetDpi = value;
                RefreshLayout();
            }
        }

        private void RefreshLayout()
        {
            if (Parent != null) Parent.PerformLayout();
            Invalidate();
        }

        public override Size GetPreferredSize(Size proposedSize)
        {
            Size overlay = OverlayRenderer.GetPixelSize(settings, settings.ShowResetCredits, TargetDpi);
            int padding = (int)Math.Ceiling(12d * DeviceDpi / 96d);
            int width = proposedSize.Width > 0 ? proposedSize.Width : overlay.Width + padding * 2;
            float fit = Math.Min(1f, Math.Max(1, width - padding * 2) / (float)overlay.Width);
            return new Size(width, Font.Height + padding * 3 + (int)Math.Ceiling(overlay.Height * fit));
        }

        public Rectangle GetOverlayBounds()
        {
            Size overlay = OverlayRenderer.GetPixelSize(settings, settings.ShowResetCredits, TargetDpi);
            int padding = (int)Math.Ceiling(12d * DeviceDpi / 96d);
            int top = Font.Height + padding * 2;
            float fit = Math.Min(1f, Math.Min(
                Math.Max(1, Width - padding * 2) / (float)overlay.Width,
                Math.Max(1, Height - top - padding) / (float)overlay.Height));
            Size rendered = new Size(Math.Max(1, (int)Math.Round(overlay.Width * fit)),
                Math.Max(1, (int)Math.Round(overlay.Height * fit)));
            return new Rectangle((Width - rendered.Width) / 2,
                top + Math.Max(0, (Height - top - padding - rendered.Height) / 2),
                rendered.Width, rendered.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Size pixels = OverlayRenderer.GetPixelSize(settings, settings.ShowResetCredits, TargetDpi);
            Rectangle bounds = GetOverlayBounds();
            using (Bitmap bitmap = OverlayRenderer.CreateBitmap(settings, sample,
                settings.ShowResetCredits ? sampleCredits : null, null, TargetDpi))
            {
                // The real window applies opacity through the compositor. Show
                // the same transparency against this preview's background.
                using (var attributes = new System.Drawing.Imaging.ImageAttributes())
                {
                    var matrix = new System.Drawing.Imaging.ColorMatrix();
                    matrix.Matrix33 = (float)settings.Style.Opacity;
                    attributes.SetColorMatrix(matrix);
                    e.Graphics.DrawImage(bitmap, bounds, 0, 0, pixels.Width, pixels.Height,
                        GraphicsUnit.Pixel, attributes);
                }
            }
            int percent = (int)Math.Round(bounds.Width * 100d / pixels.Width);
            string size = percent >= 100 ? "1:1" : string.Format(CultureInfo.CurrentCulture,
                I18n.Translate("PreviewReduced", settings.Language), percent);
            string caption = I18n.Translate("LivePreview", settings.Language) + " · " +
                I18n.Translate("PreviewSample", settings.Language) + " · " + size;
            using (var brush = new SolidBrush(Color.FromArgb(103, 112, 123)))
                e.Graphics.DrawString(caption, Font, brush, 8f * DeviceDpi / 96f, 6f * DeviceDpi / 96f);
        }
    }
}
