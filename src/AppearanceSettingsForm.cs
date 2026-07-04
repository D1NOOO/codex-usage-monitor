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
    internal sealed class AppearanceSettingsForm : Form
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
        private readonly RadioButton wholePercent;
        private readonly RadioButton oneDecimal;
        private readonly ComboBox language;
        private readonly ComboBox fontFamily;
        private readonly NumericUpDown fontSize;
        private readonly NumericUpDown resetFontSize;
        private readonly NumericUpDown scale;
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

            Text = I18n.T("SettingsTitle");
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath) ?? SystemIcons.Application;
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.None;
            ClientSize = new Size(820, 790);
            MinimumSize = new Size(760, 720);
            Font = CreateUiFont(9.5f);
            BackColor = Color.FromArgb(246, 247, 249);

            var root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.AutoScroll = false;
            root.Padding = new Padding(20, 16, 20, 14);
            root.ColumnCount = 1;
            root.RowCount = 7;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 62));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 120));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 184));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 180));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            Controls.Add(root);

            root.Controls.Add(BuildHeader(), 0, 0);

            preview = new OverlayPreviewControl();
            preview.Dock = DockStyle.Fill;
            preview.Margin = new Padding(0, 0, 0, 12);
            root.Controls.Add(preview, 0, 1);

            bottomPosition = new RadioButton();
            bottomPosition.Text = I18n.T("BottomRecommended");
            bottomPosition.AutoSize = true;
            bottomPosition.Margin = new Padding(4, 4, 18, 0);
            bottomPosition.CheckedChanged += ControlChanged;

            topPosition = new RadioButton();
            topPosition.Text = I18n.T("TopPosition");
            topPosition.AutoSize = true;
            topPosition.Margin = new Padding(0, 4, 0, 0);
            topPosition.CheckedChanged += ControlChanged;

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

            wholePercent = new RadioButton();
            wholePercent.Text = I18n.T("WholePercent");
            wholePercent.AutoSize = true;
            wholePercent.Margin = new Padding(6, 3, 12, 0);
            wholePercent.CheckedChanged += ControlChanged;

            oneDecimal = new RadioButton();
            oneDecimal.Text = I18n.T("OneDecimal");
            oneDecimal.AutoSize = true;
            oneDecimal.Margin = new Padding(0, 3, 0, 0);
            oneDecimal.CheckedChanged += ControlChanged;

            language = new ComboBox();
            language.DropDownStyle = ComboBoxStyle.DropDownList;
            language.Width = 142;
            language.Margin = new Padding(4, 1, 0, 0);
            language.Items.Add(new LanguageOption("auto", I18n.T("LanguageAuto")));
            language.Items.Add(new LanguageOption("zh-CN", "简体中文"));
            language.Items.Add(new LanguageOption("zh-TW", "繁體中文"));
            language.Items.Add(new LanguageOption("en", "English"));
            language.SelectedIndexChanged += ControlChanged;

            var languageLabel = new Label();
            languageLabel.Text = I18n.T("Language");
            languageLabel.AutoSize = true;
            languageLabel.Margin = new Padding(4, 7, 0, 0);

            var positionGroup = CreateGroup(I18n.T("DisplaySettings"));
            var displayLayout = new TableLayoutPanel();
            displayLayout.Dock = DockStyle.Fill;
            displayLayout.Padding = new Padding(2, 2, 2, 0);
            displayLayout.ColumnCount = 3;
            displayLayout.RowCount = 2;
            displayLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            displayLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            displayLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            displayLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            displayLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 50));

            var positionLabel = new Label();
            positionLabel.Text = I18n.T("DisplayPosition");
            positionLabel.Dock = DockStyle.Fill;
            positionLabel.TextAlign = ContentAlignment.MiddleRight;
            displayLayout.Controls.Add(positionLabel, 0, 0);

            var positionFlow = new FlowLayoutPanel();
            positionFlow.Dock = DockStyle.Fill;
            positionFlow.WrapContents = false;
            positionFlow.Controls.Add(bottomPosition);
            positionFlow.Controls.Add(topPosition);
            displayLayout.Controls.Add(positionFlow, 1, 0);

            var languageFlow = new FlowLayoutPanel();
            languageFlow.Dock = DockStyle.Fill;
            languageFlow.WrapContents = false;
            languageFlow.Controls.Add(languageLabel);
            languageFlow.Controls.Add(language);
            displayLayout.Controls.Add(languageFlow, 2, 0);

            var progressLabel = new Label();
            progressLabel.Text = I18n.T("ProgressDisplay");
            progressLabel.Dock = DockStyle.Fill;
            progressLabel.TextAlign = ContentAlignment.MiddleRight;
            displayLayout.Controls.Add(progressLabel, 0, 1);

            var progressFlow = new FlowLayoutPanel();
            progressFlow.Dock = DockStyle.Fill;
            progressFlow.WrapContents = false;
            progressFlow.Controls.Add(showRemaining);
            progressFlow.Controls.Add(showUsed);
            displayLayout.Controls.Add(progressFlow, 1, 1);

            var precisionLabel = new Label();
            precisionLabel.Text = I18n.T("PercentagePrecision");
            precisionLabel.AutoSize = true;
            precisionLabel.Margin = new Padding(4, 6, 0, 0);

            var precisionFlow = new FlowLayoutPanel();
            precisionFlow.Dock = DockStyle.Fill;
            precisionFlow.WrapContents = false;
            precisionFlow.Controls.Add(precisionLabel);
            precisionFlow.Controls.Add(wholePercent);
            precisionFlow.Controls.Add(oneDecimal);
            displayLayout.Controls.Add(precisionFlow, 2, 1);

            positionGroup.Controls.Add(displayLayout);
            root.Controls.Add(positionGroup, 0, 2);

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
                    var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (string name in preferred)
                    {
                        if (installed.Contains(name) && added.Add(name))
                            fontFamily.Items.Add(name);
                    }
                    if (!string.IsNullOrWhiteSpace(working.Style.FontFamily) &&
                        installed.Contains(working.Style.FontFamily) &&
                        added.Add(working.Style.FontFamily))
                        fontFamily.Items.Add(working.Style.FontFamily);
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
            scale = CreateNumber(75, 150, 5, 0);
            opacity = CreateNumber(50, 100, 5, 0);
            cornerRadius = CreateNumber(0, 20, 1, 0);

            var typographyGroup = CreateGroup(I18n.T("Typography"));
            typographyGroup.Controls.Add(BuildTypographyTable());
            root.Controls.Add(typographyGroup, 0, 3);

            var colorsGroup = CreateGroup(I18n.T("Colors"));
            colorsGroup.Controls.Add(BuildColorsPanel());
            root.Controls.Add(colorsGroup, 0, 4);

            var hint = new Label();
            hint.Dock = DockStyle.Fill;
            hint.TextAlign = ContentAlignment.MiddleLeft;
            hint.ForeColor = Color.FromArgb(103, 112, 123);
            hint.Text = I18n.T("SettingsHint");
            root.Controls.Add(hint, 0, 5);

            root.Controls.Add(BuildButtons(), 0, 6);

            LoadControls();
            FormClosed += delegate
            {
                if (!committed && onCancel != null)
                    onCancel();
            };
        }

        private Control BuildHeader()
        {
            var panel = new TableLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.ColumnCount = 1;
            panel.RowCount = 2;
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            panel.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));

            var title = new Label();
            title.Dock = DockStyle.Fill;
            title.Margin = new Padding(0);
            title.Font = CreateUiFont(15f, FontStyle.Bold);
            title.ForeColor = Color.FromArgb(28, 31, 36);
            title.Text = I18n.T("AppearanceTitle");
            title.TextAlign = ContentAlignment.BottomLeft;
            panel.Controls.Add(title, 0, 0);

            var subtitle = new Label();
            subtitle.Dock = DockStyle.Fill;
            subtitle.Margin = new Padding(1, 3, 0, 0);
            subtitle.ForeColor = Color.FromArgb(103, 112, 123);
            subtitle.Text = I18n.T("AppearanceSubtitle");
            subtitle.TextAlign = ContentAlignment.TopLeft;
            panel.Controls.Add(subtitle, 0, 1);
            return panel;
        }

        private GroupBox CreateGroup(string title)
        {
            var group = new GroupBox();
            group.Text = title;
            group.Dock = DockStyle.Fill;
            group.Margin = new Padding(0, 0, 0, 10);
            group.Padding = new Padding(10, 8, 10, 8);
            group.ForeColor = Color.FromArgb(42, 46, 52);
            return group;
        }

        private NumericUpDown CreateNumber(decimal min, decimal max, decimal increment, int decimals)
        {
            var control = new NumericUpDown();
            control.Minimum = min;
            control.Maximum = max;
            control.Increment = increment;
            control.DecimalPlaces = decimals;
            control.Width = 82;
            control.TextAlign = HorizontalAlignment.Right;
            control.ValueChanged += ControlChanged;
            return control;
        }

        private Control BuildTypographyTable()
        {
            var table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.Padding = new Padding(4, 4, 4, 2);
            table.ColumnCount = 4;
            table.RowCount = 4;
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            for (int i = 0; i < 4; i++)
                table.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));

            AddField(table, 0, 0, I18n.T("Font"), fontFamily);
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
            label.TextAlign = ContentAlignment.MiddleRight;
            label.Dock = DockStyle.Fill;
            table.Controls.Add(label, column, row);

            var panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
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
            var container = new Panel();
            container.Dock = DockStyle.Fill;

            var grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Top;
            grid.Height = 84;
            grid.Padding = new Padding(4, 4, 4, 0);
            grid.ColumnCount = 5;
            grid.RowCount = 2;
            for (int i = 0; i < 5; i++)
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 20));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

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
            container.Controls.Add(grid);

            var presets = new FlowLayoutPanel();
            presets.Dock = DockStyle.Bottom;
            presets.Height = 32;
            presets.FlowDirection = FlowDirection.RightToLeft;
            presets.WrapContents = false;
            var dark = CreateSecondaryButton(I18n.T("DarkPreset"));
            dark.Click += delegate { ApplyPreset(true); };
            presets.Controls.Add(dark);
            var light = CreateSecondaryButton(I18n.T("LightPreset"));
            light.Click += delegate { ApplyPreset(false); };
            presets.Controls.Add(light);
            container.Controls.Add(presets);
            return container;
        }

        private void AddColorPicker(
            TableLayoutPanel grid,
            int column,
            int row,
            string label,
            string property)
        {
            var panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
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
            var panel = new FlowLayoutPanel();
            panel.Dock = DockStyle.Fill;
            panel.FlowDirection = FlowDirection.RightToLeft;
            panel.WrapContents = false;

            var save = new Button();
            save.Text = I18n.T("SaveClose");
            save.AutoSize = true;
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
                bottomPosition.Checked = working.Position == "bottom-left";
                topPosition.Checked = working.Position == "top";
                showRemaining.Checked = UsageDisplayTools.IsRemaining(working.UsageDisplay);
                showUsed.Checked = !UsageDisplayTools.IsRemaining(working.UsageDisplay);
                wholePercent.Checked = working.PercentDecimalPlaces == 0;
                oneDecimal.Checked = working.PercentDecimalPlaces == 1;
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
                scale.Value = ClampDecimal((decimal)(working.Style.Scale * 100), scale);
                opacity.Value = ClampDecimal((decimal)(working.Style.Opacity * 100), opacity);
                cornerRadius.Value = ClampDecimal((decimal)working.Style.CornerRadius, cornerRadius);
                foreach (KeyValuePair<string, Button> item in colorButtons)
                    SetColorButton(item.Value, GetStyleColor(item.Key));
            }
            finally
            {
                loading = false;
            }
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
            Preview();
        }

        private void UpdateWorking()
        {
            working.Position = bottomPosition.Checked ? "bottom-left" : "top";
            working.UsageDisplay = showUsed.Checked ? "used" : "remaining";
            working.PercentDecimalPlaces = oneDecimal.Checked ? 1 : 0;
            var selectedLanguage = language.SelectedItem as LanguageOption;
            working.Language = selectedLanguage == null ? "auto" : selectedLanguage.Code;
            working.Style.FontFamily = string.IsNullOrWhiteSpace(fontFamily.Text)
                ? "Microsoft YaHei UI"
                : fontFamily.Text.Trim();
            working.Style.FontSize = (double)fontSize.Value;
            working.Style.ResetFontSize = (double)resetFontSize.Value;
            working.Style.Scale = (double)scale.Value / 100.0;
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
                return new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Point);
            }
            catch
            {
                return new Font(SystemFonts.MessageBoxFont.FontFamily, size, style, GraphicsUnit.Point);
            }
        }
    }

    internal sealed class OverlayPreviewControl : Control
    {
        private MonitorSettings settings = new MonitorSettings();

        public OverlayPreviewControl()
        {
            DoubleBuffered = true;
            BackColor = Color.FromArgb(236, 239, 243);
            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint |
                     ControlStyles.OptimizedDoubleBuffer, true);
        }

        public MonitorSettings Settings
        {
            get { return settings; }
            set
            {
                settings = value ?? new MonitorSettings();
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            int baseWidth = settings.Position == "bottom-left"
                ? DrawingHelpers.BottomLeftWidth
                : 470;
            int baseHeight = settings.Position == "bottom-left"
                ? DrawingHelpers.BottomLeftHeight
                : 40;
            float fit = Math.Min(
                (ClientSize.Width - 28f) / baseWidth,
                (ClientSize.Height - 30f) / baseHeight);
            fit = Math.Min(fit, 1.35f);
            float x = (ClientSize.Width - baseWidth * fit) / 2f;
            float y = (ClientSize.Height - baseHeight * fit) / 2f + 6f;

            g.TranslateTransform(x, y);
            g.ScaleTransform(fit, fit);
            DrawOverlay(g, baseWidth, baseHeight);
            g.ResetTransform();

            using (var font = new Font("Microsoft YaHei UI", 8.5f, FontStyle.Regular))
            using (var brush = new SolidBrush(Color.FromArgb(103, 112, 123)))
                g.DrawString(I18n.Translate("LivePreview", settings.Language), font, brush, 8, 6);
        }

        private void DrawOverlay(Graphics g, int width, int height)
        {
            StyleSettings style = settings.Style;
            Color outer = ColorTools.Parse(style.Background);
            Color border = ColorTools.Parse(style.Border);
            Color card = ColorTools.Parse(style.CardBackground);

            using (var brush = new SolidBrush(outer))
            using (var pen = new Pen(border, 1f))
            using (GraphicsPath path = DrawingHelpers.RoundRect(
                new RectangleF(0.5f, 0.5f, width - 1f, height - 1f),
                (float)style.CornerRadius))
            {
                g.FillPath(brush, path);
                g.DrawPath(pen, path);
            }

            DateTime fiveReset = DateTime.Today.AddDays(1).AddHours(3).AddMinutes(25);
            DateTime sevenReset = DateTime.Today.AddDays(7).AddHours(22).AddMinutes(25);
            if (settings.Position == "bottom-left")
            {
                DrawRow(g, DrawingHelpers.GetBottomLeftCardBounds(true),
                    I18n.Translate("FiveHour", settings.Language), 35f,
                    FormatReset(fiveReset), card, true);
                DrawRow(g, DrawingHelpers.GetBottomLeftCardBounds(false),
                    I18n.Translate("SevenDay", settings.Language), 5f,
                    FormatReset(sevenReset), card, false);
            }
            else
            {
                DrawRow(g, new RectangleF(5, 5, 228, 30),
                    I18n.Translate("FiveHour", settings.Language), 35f,
                    FormatReset(fiveReset), card, true);
                DrawRow(g, new RectangleF(237, 5, 228, 30),
                    I18n.Translate("SevenDay", settings.Language), 5f,
                    FormatReset(sevenReset), card, false);
            }
        }

        private void DrawRow(
            Graphics g,
            RectangleF bounds,
            string label,
            float usedPercent,
            string reset,
            Color card,
            bool primary)
        {
            StyleSettings style = settings.Style;
            using (var brush = new SolidBrush(card))
            using (GraphicsPath path = DrawingHelpers.RoundRect(
                bounds, Math.Max(0, (float)style.CornerRadius - 3f)))
                g.FillPath(brush, path);

            FontFamily family;
            try { family = new FontFamily(style.FontFamily); }
            catch { family = SystemFonts.MessageBoxFont.FontFamily; }

            double value = UsageDisplayTools.GetDisplayedPercent(
                usedPercent, settings.UsageDisplay);
            string percent = UsageDisplayTools.FormatPercent(
                value, settings.PercentDecimalPlaces);
            float mainSize = (float)style.FontSize;
            float resetSize = (float)style.ResetFontSize;
            using (family)
            using (var main = new Font(family, mainSize, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var percentFont = new Font(
                family,
                mainSize + DrawingHelpers.PercentOpticalSizeOffset,
                FontStyle.Bold,
                GraphicsUnit.Pixel))
            using (var resetFont = new Font(family, resetSize, FontStyle.Regular, GraphicsUnit.Pixel))
            using (var textBrush = new SolidBrush(ColorTools.Parse(style.Text)))
            using (var mutedBrush = new SolidBrush(ColorTools.Parse(style.MutedText)))
            {
                DrawingHelpers.DrawUsageText(
                    g, bounds, label, percent, reset,
                    main, percentFont, resetFont, textBrush, mutedBrush);
            }

            RectangleF track = new RectangleF(bounds.X + 7, bounds.Bottom - 4, bounds.Width - 14, 2);
            using (var trackBrush = new SolidBrush(ColorTools.Parse(style.Track)))
                g.FillRectangle(trackBrush, track);
            Color normal = ColorTools.Parse(primary ? style.Primary : style.Secondary);
            Color progress = UsageDisplayTools.GetProgressColor(
                value,
                settings.UsageDisplay,
                normal,
                ColorTools.Parse(style.Warning),
                ColorTools.Parse(style.Danger));
            using (var progressBrush = new SolidBrush(progress))
                g.FillRectangle(progressBrush,
                    new RectangleF(
                        track.X,
                        track.Y,
                        track.Width * (float)value / 100f,
                        track.Height));
        }

        private string FormatReset(DateTime time)
        {
            return I18n.FormatDate(time, settings.Language);
        }
    }
}
