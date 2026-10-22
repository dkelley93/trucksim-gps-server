using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Funbit.Ets.Telemetry.Server.Helpers;
using Funbit.Ets.Telemetry.Server.Ui;

namespace Funbit.Ets.Telemetry.Server
{
    public sealed class UpdateForm : DarkDialog
    {
        readonly string _changelogUrl;
        string _tempHtmlPath;

        public UpdateForm(string currentVersion, string installedVersion, string releaseBody, string changelogUrl)
        {
            _changelogUrl = changelogUrl;
            BuildForm(currentVersion, installedVersion, releaseBody);
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
            if (_tempHtmlPath != null && File.Exists(_tempHtmlPath))
            {
                try { File.Delete(_tempHtmlPath); }
                catch { /* best effort cleanup */ }
            }
        }

        void BuildForm(string currentVersion, string installedVersion, string releaseBody)
        {
            SuspendLayout();

            Text = L.Get("update.title");
            ClientSize = new Size(560, 520);
            MinimumSize = new Size(450, 400);
            FormBorderStyle = FormBorderStyle.Sizable;

            var headerLabel = new Label
            {
                Text = L.Get("update.header"),
                Font = DarkTheme.Brand,
                ForeColor = Color.White,
                AutoSize = true,
                Location = new Point(22, 20)
            };

            var versionLabel = new Label
            {
                Text = $"v{TrimVersion(installedVersion)}  →  v{currentVersion}",
                ForeColor = DarkTheme.TextSecondary,
                AutoSize = true,
                Location = new Point(24, 50)
            };

            var notesCard = new Card
            {
                Location = new Point(16, 84),
                Size = new Size(528, 360),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right
            };

            var changelogLabel = new Label
            {
                Text = L.Get("update.release_notes"),
                Font = DarkTheme.Title,
                ForeColor = Color.White,
                BackColor = DarkTheme.Card,
                AutoSize = true,
                Location = new Point(16, 14)
            };

            var webBrowser = new WebBrowser
            {
                Location = new Point(16, 44),
                Size = new Size(notesCard.Width - 24, notesCard.Height - 56),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                IsWebBrowserContextMenuEnabled = false,
                ScriptErrorsSuppressed = true,
                WebBrowserShortcutsEnabled = false,
                Visible = false
            };
            webBrowser.DocumentCompleted += (s, e) => webBrowser.Visible = true;

            webBrowser.Navigating += (s, e) =>
            {
                // Allow file:// navigation for our temp file, block everything else except links
                if (e.Url.Scheme == "http" || e.Url.Scheme == "https")
                {
                    e.Cancel = true;
                    Process.Start(new ProcessStartInfo(e.Url.ToString()) { UseShellExecute = true });
                }
            };

            // Write HTML to temp file with UTF-8 BOM so IE correctly handles emoji
            string html = BuildHtml(string.IsNullOrWhiteSpace(releaseBody) ? L.Get("update.no_release_notes") : releaseBody);
            _tempHtmlPath = Path.Combine(Path.GetTempPath(), $"trucksimgps_update_{Guid.NewGuid():N}.html");
            File.WriteAllText(_tempHtmlPath, html, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            webBrowser.Navigate(_tempHtmlPath);
            notesCard.Controls.AddRange(new Control[] { changelogLabel, webBrowser });

            var bottomPanel = new Panel
            {
                Size = new Size(560, 64),
                Dock = DockStyle.Bottom,
                BackColor = DarkTheme.Header
            };

            var githubLink = new LinkText
            {
                Text = L.Get("update.view_on_github"),
                Location = new Point(22, 24)
            };
            githubLink.LinkClicked += (s, e) =>
            {
                Process.Start(new ProcessStartInfo(_changelogUrl) { UseShellExecute = true });
            };

            var laterButton = new DarkButton
            {
                Text = L.Get("update.later"),
                Primary = false,
                Font = DarkTheme.Body,
                BackColor = DarkTheme.Header,
                Height = 34,
                Anchor = AnchorStyles.Right,
                DialogResult = DialogResult.Cancel
            };

            var updateButton = new DarkButton
            {
                Text = L.Get("update.now"),
                BackColor = DarkTheme.Header,
                Height = 34,
                Anchor = AnchorStyles.Right,
                DialogResult = DialogResult.OK
            };
            updateButton.Width = Math.Max(120, DarkTheme.TextWidth(updateButton, updateButton.Text, updateButton.Font) + 32);
            updateButton.Location = new Point(bottomPanel.Width - 16 - updateButton.Width, 15);
            laterButton.Width = Math.Max(96, DarkTheme.TextWidth(laterButton, laterButton.Text, laterButton.Font) + 32);
            laterButton.Location = new Point(updateButton.Left - 8 - laterButton.Width, 15);

            AcceptButton = updateButton;
            CancelButton = laterButton;

            bottomPanel.Controls.AddRange(new Control[] { githubLink, laterButton, updateButton });
            Controls.AddRange(new Control[]
            {
                headerLabel, versionLabel, notesCard, bottomPanel
            });
            ActiveControl = updateButton;

            ResumeLayout();
        }

        static string BuildHtml(string markdown)
        {
            string body = MarkdownToHtml(markdown);

            return @"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"" />
<meta http-equiv=""X-UA-Compatible"" content=""IE=edge"" />
<style>
html {
  scrollbar-face-color: #404F60;
  scrollbar-track-color: #232938;
  scrollbar-arrow-color: #A0A6B2;
  scrollbar-shadow-color: #404F60;
  scrollbar-highlight-color: #404F60;
  scrollbar-3dlight-color: #232938;
  scrollbar-darkshadow-color: #232938;
}
body {
  font-family: 'Segoe UI', 'Segoe UI Emoji', 'Segoe UI Symbol', sans-serif;
  font-size: 14px;
  color: #DEDEDE;
  background: #232938;
  margin: 0 12px 8px 0;
  line-height: 1.6;
}
h2 {
  font-size: 16px;
  font-weight: 600;
  color: #FFFFFF;
  margin: 1em 0 0.4em 0;
  padding-bottom: 4px;
  border-bottom: 1px solid #2E3648;
}
h2:first-child { margin-top: 0; }
h3 { font-size: 15px; font-weight: 600; color: #FFFFFF; margin: 0.8em 0 0.3em 0; }
ul, ol { padding-left: 22px; margin: 0.3em 0; }
ul { list-style-type: disc; }
ul ul { list-style-type: disc; margin: 2px 0; padding-left: 20px; }
li { margin: 4px 0; }
code {
  background: #1B202A;
  padding: 1px 5px;
  border-radius: 3px;
  font-family: Consolas, monospace;
  font-size: 13px;
}
a { color: #42AEFA; text-decoration: none; }
a:hover { color: #6CC1FF; text-decoration: underline; }
p { margin: 0.4em 0; }
strong { font-weight: 600; color: #FFFFFF; }
hr { border: none; border-top: 1px solid #2E3648; margin: 12px 0; }
</style>
</head>
<body>" + body + @"</body>
</html>";
        }

        static string MarkdownToHtml(string markdown)
        {
            var sb = new StringBuilder();
            var lines = markdown.Replace("\r\n", "\n").Split('\n');
            bool inUl = false;
            bool inOl = false;
            int ulDepth = 0;

            foreach (var rawLine in lines)
            {
                var line = rawLine.TrimEnd();
                var trimmed = line.TrimStart();

                bool isUlItem = Regex.IsMatch(trimmed, @"^[-*+]\s");
                bool isOlItem = Regex.IsMatch(trimmed, @"^\d+\.\s");
                int indent = line.Length - trimmed.Length;

                // Close lists when transitioning out
                if (inUl && !isUlItem && trimmed.Length > 0)
                {
                    while (ulDepth > 0) { sb.Append("</ul>"); ulDepth--; }
                    inUl = false;
                }
                if (inOl && !isOlItem && trimmed.Length > 0)
                {
                    sb.Append("</ol>");
                    inOl = false;
                }

                // Headers
                var hMatch = Regex.Match(trimmed, @"^(#{1,3})\s+(.+)");
                if (hMatch.Success)
                {
                    if (inUl) { while (ulDepth > 0) { sb.Append("</ul>"); ulDepth--; } inUl = false; }
                    if (inOl) { sb.Append("</ol>"); inOl = false; }
                    int level = hMatch.Groups[1].Length;
                    sb.Append($"<h{level}>{Inline(hMatch.Groups[2].Value.Trim())}</h{level}>");
                    continue;
                }

                // Horizontal rule
                if (Regex.IsMatch(trimmed, @"^[-*_]{3,}\s*$")) { sb.Append("<hr/>"); continue; }

                // Unordered list
                if (isUlItem)
                {
                    var m = Regex.Match(trimmed, @"^[-*+]\s+(.+)");
                    if (m.Success)
                    {
                        // Each 2 spaces of indent = one nesting level
                        int targetDepth = (indent / 2) + 1;
                        if (!inUl) { sb.Append("<ul>"); ulDepth = 1; inUl = true; }
                        while (ulDepth < targetDepth) { sb.Append("<ul>"); ulDepth++; }
                        while (ulDepth > targetDepth) { sb.Append("</ul>"); ulDepth--; }
                        sb.Append($"<li>{Inline(m.Groups[1].Value)}</li>");
                    }
                    continue;
                }

                // Ordered list
                if (isOlItem)
                {
                    var m = Regex.Match(trimmed, @"^\d+\.\s+(.+)");
                    if (m.Success)
                    {
                        if (!inOl) { sb.Append("<ol>"); inOl = true; }
                        sb.Append($"<li>{Inline(m.Groups[1].Value)}</li>");
                    }
                    continue;
                }

                // Empty line
                if (trimmed.Length == 0) continue;

                // Paragraph
                sb.Append($"<p>{Inline(trimmed)}</p>");
            }

            if (inUl) { while (ulDepth > 0) { sb.Append("</ul>"); ulDepth--; } }
            if (inOl) sb.Append("</ol>");

            return sb.ToString();
        }

        static string Inline(string text)
        {
            text = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
            text = Regex.Replace(text, @"\*\*(.+?)\*\*", "<strong>$1</strong>");
            text = Regex.Replace(text, @"__(.+?)__", "<strong>$1</strong>");
            text = Regex.Replace(text, @"(?<!\*)\*([^*]+?)\*(?!\*)", "<em>$1</em>");
            text = Regex.Replace(text, @"(?<!\w)_([^_]+?)_(?!\w)", "<em>$1</em>");
            text = Regex.Replace(text, @"`(.+?)`", "<code>$1</code>");
            text = Regex.Replace(text, @"\[(.+?)\]\((.+?)\)", "<a href=\"$2\">$1</a>");
            return text;
        }

        static string TrimVersion(string version)
        {
            var parts = version.Split('.');
            if (parts.Length >= 3)
                return $"{parts[0]}.{parts[1]}.{parts[2]}";
            return version;
        }
    }
}
