// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace JinraShaderSync
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (HttpClient client = ShaderSync.CreateClient())
            using (var window = new SyncWindow(client))
                Application.Run(window);
        }
    }

    internal sealed class SyncWindow : Form
    {
        private const string OcarinaId = "0400486024DEC000";
        private readonly TextBox folder = new TextBox { ReadOnly = true, Dock = DockStyle.Fill };
        private readonly ComboBox title = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly Button browse = new Button { Text = "Browse…", AutoSize = true };
        private readonly Button check = new Button { Text = "Check online", AutoSize = true };
        private readonly Button sync = new Button { Text = "Sync cache", AutoSize = true };
        private readonly Button cancel = new Button { Text = "Cancel", AutoSize = true, Enabled = false };
        private readonly Label status = new Label { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };
        private readonly HttpClient client;
        private SyncPlan plan;
        private CancellationTokenSource operation;

        internal SyncWindow(HttpClient client)
        {
            this.client = client;
            Text = "Jinra Shader Sync";
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10F);
            ClientSize = new Size(560, 300);
            MinimumSize = new Size(490, 312);
            StartPosition = FormStartPosition.CenterScreen;

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 8 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            for (int i = 0; i < layout.RowCount; i++)
                layout.RowStyles.Add(new RowStyle(i == 5 ? SizeType.Percent : SizeType.AutoSize, 100));
            Controls.Add(layout);

            AddWide(layout, new Label { Text = "Shared Vulkan shader cache", AutoSize = true,
                                       Font = new Font(Font.FontFamily, 15F, FontStyle.Bold),
                                       Margin = new Padding(0, 0, 0, 14) }, 0);
            AddWide(layout, new Label { Text = "Jinra data folder", AutoSize = true }, 1);
            layout.Controls.Add(folder, 0, 2);
            layout.Controls.Add(browse, 1, 2);
            AddWide(layout, new Label { Text = "Game", AutoSize = true, Margin = new Padding(0, 10, 0, 4) }, 3);
            AddWide(layout, title, 4);
            AddWide(layout, status, 5);

            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            buttons.Controls.AddRange(new Control[] { check, sync, cancel });
            AddWide(layout, buttons, 6);
            AddWide(layout, new Label { Text = "Catalog: " + ShaderSync.Repository + " on GitHub",
                                       AutoSize = true, ForeColor = SystemColors.GrayText }, 7);

            browse.Click += (sender, args) => Browse();
            title.SelectedIndexChanged += (sender, args) => ResetPlan();
            check.Click += async (sender, args) => await CheckAsync();
            sync.Click += async (sender, args) => await SyncAsync();
            cancel.Click += (sender, args) => operation.Cancel();
            FormClosing += (sender, args) => {
                if (operation != null)
                {
                    operation.Cancel();
                    args.Cancel = true;
                }
            };

            string installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                            "Programs", "Jinra", "user");
            string portable = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "user");
            string roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eden");
            SetFolder(new[] { portable, installed, roaming }.FirstOrDefault(Directory.Exists) ?? "");
        }

        private static void AddWide(TableLayoutPanel layout, Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
        }

        private void Browse()
        {
            using (var picker = new FolderBrowserDialog { Description = "Select the Jinra user data folder.",
                                                           SelectedPath = folder.Text, ShowNewFolderButton = false })
            {
                if (picker.ShowDialog(this) != DialogResult.OK)
                    return;
                string portable = Path.Combine(picker.SelectedPath, "user");
                string selected = Directory.Exists(portable) ? portable : picker.SelectedPath;
                if (!Directory.Exists(Path.Combine(selected, "config")) &&
                    !Directory.Exists(Path.Combine(selected, "cache", "shader")))
                {
                    status.Text = "Select the user data folder of a Jinra installation you have opened at least once.";
                    return;
                }
                SetFolder(selected);
            }
        }

        private void SetFolder(string path)
        {
            folder.Text = path;
            title.Items.Clear();
            title.Items.Add(new Game(OcarinaId, "The Legend of Zelda: Ocarina of Time"));
            if (path.Length != 0)
            {
                string shaders = Path.Combine(path, "cache", "shader");
                if (Directory.Exists(shaders))
                    foreach (string directory in Directory.GetDirectories(shaders))
                    {
                        string id = Path.GetFileName(directory).ToUpperInvariant();
                        if (id != OcarinaId && id.Length == 16 && id.All(Uri.IsHexDigit) &&
                            File.Exists(Path.Combine(directory, ShaderCache.FileName)))
                            title.Items.Add(new Game(id, id));
                    }
            }
            title.SelectedIndex = 0;
        }

        private void ResetPlan()
        {
            plan = null;
            status.Text = folder.TextLength == 0 ? "Choose your Jinra data folder to get started."
                                                 : "Check which shader entries are available for the selected game.";
            UpdateButtons();
        }

        private async Task CheckAsync()
        {
            ResetPlan();
            var game = (Game)title.SelectedItem;
            string path = Path.Combine(folder.Text, "cache", "shader", game.Id.ToLowerInvariant(), ShaderCache.FileName);
            await RunAsync("Checking the catalog and local cache…", async token => {
                plan = await Task.Run(() => ShaderSync.CheckAsync(client, game.Id, path, token), token);
                if (plan == null)
                    status.Text = "No shared pack is available for this game.";
                else if (plan.Missing == 0)
                    status.Text = "Your cache already has all the entries in the shared pack.";
                else
                    status.Text = string.Format("{0:N0} missing entries. Download: {1:N1} MB.\r\n" +
                        "Close Jinra before syncing. An existing cache will be backed up.",
                        plan.Missing, plan.DownloadBytes / 1000000.0);
            });
        }

        private async Task SyncAsync()
        {
            SyncPlan approved = plan;
            plan = null;
            var progress = new Progress<string>(message => status.Text = message);
            await RunAsync("Preparing the cache…", async token => {
                int added = await Task.Run(() => ShaderSync.ApplyAsync(client, approved, progress, token), token);
                status.Text = string.Format("Added {0:N0} entries. You can open Jinra now.", added);
                if (File.Exists(approved.CachePath + ".jinra-sync.bak"))
                    status.Text += "\r\nThe previous cache is beside it with the .jinra-sync.bak extension.";
            });
        }

        private async Task RunAsync(string message, Func<CancellationToken, Task> action)
        {
            operation = new CancellationTokenSource();
            UpdateButtons();
            status.Text = message;
            try { await action(operation.Token); }
            catch (OperationCanceledException) {
                status.Text = operation.IsCancellationRequested ? "Cancelled. The current cache is unchanged."
                                                                : "The download timed out. Check online to retry.";
            }
            catch (Exception error) { status.Text = error.Message; }
            finally
            {
                operation.Dispose();
                operation = null;
                UpdateButtons();
            }
        }

        private void UpdateButtons()
        {
            bool ready = operation == null;
            browse.Enabled = title.Enabled = ready;
            check.Enabled = ready && folder.TextLength != 0 && title.SelectedItem != null;
            sync.Enabled = ready && plan != null && plan.Missing != 0;
            cancel.Enabled = !ready;
        }

        private sealed class Game
        {
            internal readonly string Id;
            private readonly string name;

            internal Game(string id, string name) { Id = id; this.name = name; }
            public override string ToString() { return name; }
        }
    }
}

