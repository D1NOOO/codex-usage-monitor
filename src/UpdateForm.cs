using System;
using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace CodexRateMonitorNative
{
    internal sealed class UpdateForm : Form
    {
        private readonly UpdateInfo info;
        private readonly Button updateButton;
        private readonly Button cancelButton;
        private readonly Label statusLabel;

        public event Action<UpdateForm, UpdateInfo> UpdateRequested;

        public UpdateForm(UpdateInfo value)
        {
            info = value;
            Text = I18n.T("UpdateTitle");
            StartPosition = FormStartPosition.CenterScreen;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ClientSize = new Size(560, 430);
            Font = SystemFonts.MessageBoxFont;

            var title = new Label();
            title.Text = I18n.F("UpdateAvailableTitle", info.Version);
            title.Font = new Font(Font.FontFamily, 14f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(20, 18);
            Controls.Add(title);

            var version = new Label();
            version.Text = I18n.F("UpdateVersionLine", BuildVersion.Value, info.Version);
            version.AutoSize = true;
            version.ForeColor = Color.DimGray;
            version.Location = new Point(22, 54);
            Controls.Add(version);

            var heading = new Label();
            heading.Text = I18n.T("UpdateWhatsNew");
            heading.AutoSize = true;
            heading.Font = new Font(Font, FontStyle.Bold);
            heading.Location = new Point(22, 84);
            Controls.Add(heading);

            var notes = new RichTextBox();
            notes.ReadOnly = true;
            notes.DetectUrls = true;
            notes.BorderStyle = BorderStyle.FixedSingle;
            notes.BackColor = SystemColors.Window;
            notes.Location = new Point(22, 108);
            notes.Size = new Size(516, 245);
            notes.Text = FormatNotes(info.Notes);
            notes.LinkClicked += delegate(object sender, LinkClickedEventArgs e)
            {
                OpenUrl(e.LinkText);
            };
            Controls.Add(notes);

            statusLabel = new Label();
            statusLabel.AutoSize = false;
            statusLabel.Location = new Point(22, 366);
            statusLabel.Size = new Size(320, 42);
            statusLabel.ForeColor = Color.DimGray;
            Controls.Add(statusLabel);

            updateButton = new Button();
            updateButton.Text = I18n.T("UpdateAction");
            updateButton.Size = new Size(92, 32);
            updateButton.Location = new Point(348, 374);
            updateButton.Click += delegate
            {
                Action<UpdateForm, UpdateInfo> handler = UpdateRequested;
                if (handler != null)
                    handler(this, info);
            };
            Controls.Add(updateButton);

            cancelButton = new Button();
            cancelButton.Text = I18n.T("Cancel");
            cancelButton.Size = new Size(92, 32);
            cancelButton.Location = new Point(446, 374);
            cancelButton.DialogResult = DialogResult.Cancel;
            Controls.Add(cancelButton);
            CancelButton = cancelButton;
        }

        public void SetBusy(string text)
        {
            ControlBox = false;
            updateButton.Enabled = false;
            cancelButton.Enabled = false;
            statusLabel.Text = text;
        }

        public void SetError(string text)
        {
            ControlBox = true;
            updateButton.Enabled = true;
            cancelButton.Enabled = true;
            statusLabel.ForeColor = Color.Firebrick;
            statusLabel.Text = text;
        }

        private static string FormatNotes(string value)
        {
            string text = value ?? string.Empty;
            text = Regex.Replace(text, @"^#{1,6}\s*", string.Empty, RegexOptions.Multiline);
            text = Regex.Replace(text, @"^\s*[-*]\s+", "• ", RegexOptions.Multiline);
            return text.Replace("`", string.Empty).Trim();
        }

        private static void OpenUrl(string url)
        {
            try
            {
                var start = new ProcessStartInfo(url);
                start.UseShellExecute = true;
                Process.Start(start);
            }
            catch
            {
            }
        }
    }
}
