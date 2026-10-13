using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using WixToolset.Mba.Core;

namespace LaserficheReports.Setup
{
    // Dashboard's six-step visual structure, with Reports connection and planner settings.
    internal sealed class Wizard : Form
    {
        private static readonly Color Navy = Color.FromArgb(14, 42, 78), Blue = Color.FromArgb(20, 94, 168), Canvas = Color.FromArgb(247, 249, 252);
        private readonly ReportsBa ba;
        private readonly LaunchAction requested;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly Panel[] pages = new Panel[6];
        private readonly Label title = new Label { AutoSize = true, Font = new Font("Segoe UI", 22, FontStyle.Bold) };
        private readonly Label subtitle = new Label { AutoSize = true, ForeColor = Color.DimGray };
        private readonly Button next = new Button { Text = "Next", Width = 140, Height = 42, BackColor = Blue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        private readonly Button back = new Button { Text = "Back", Width = 100, Height = 42 };
        private readonly Button cancel = new Button { Text = "Cancel", Width = 100, Height = 42 };
        private readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Top, Height = 25 };
        private readonly Label result = new Label { Dock = DockStyle.Fill, AutoSize = false, Padding = new Padding(0, 18, 0, 0) };
        private readonly Dictionary<string, TextBox> fields = new Dictionary<string, TextBox>();
        private readonly ComboBox version = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly RadioButton repair = new RadioButton { Text = "Repair or update the Reports installation", Checked = true, AutoSize = true };
        private readonly RadioButton remove = new RadioButton { Text = "Uninstall Reports (keep connection settings and all Laserfiche files)", AutoSize = true };
        private readonly Label maintenance = new Label { Text = "A Reports MSI installation was found.", AutoSize = true };
        private readonly Label review = new Label { Dock = DockStyle.Fill, AutoSize = false };
        private int page;
        private bool applying, complete;
        private string profile = "";
        private readonly string[] titles = { "Welcome", "Connect to Laserfiche", "Choose deployment options", "Review and install", "Installing Reports", "Setup complete" };
        internal Wizard(ReportsBa ba, LaunchAction requested)
        {
            this.ba = ba; this.requested = requested;
            Text = "Laserfiche Reports Setup"; Font = new Font("Segoe UI", 10); MinimumSize = new Size(1000, 750); Size = new Size(1080, 800);
            StartPosition = FormStartPosition.CenterScreen; BackColor = Canvas;
            var rail = new Panel { Dock = DockStyle.Left, Width = 245, BackColor = Navy, Padding = new Padding(25) };
            var railText = new Label { Dock = DockStyle.Fill, ForeColor = Color.White, Font = new Font("Segoe UI", 15, FontStyle.Bold),
                Text = "Laserfiche Reports\n\nSmart document reports\n\n\n1   Welcome\n\n2   Laserfiche connection\n\n3   Local AI and ports\n\n4   Review and install" };
            rail.Controls.Add(railText); Controls.Add(rail);
            var main = new Panel { Dock = DockStyle.Fill, Padding = new Padding(30) }; Controls.Add(main); main.BringToFront();
            var heading = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 105, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            heading.Controls.Add(title); heading.Controls.Add(subtitle); main.Controls.Add(heading);
            var footer = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 60, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 10, 0, 0) };
            footer.Controls.Add(next); footer.Controls.Add(cancel); footer.Controls.Add(back); main.Controls.Add(footer);
            var content = new Panel { Dock = DockStyle.Fill, Padding = new Padding(0, 10, 0, 0) }; main.Controls.Add(content); content.BringToFront();
            for (var i = 0; i < pages.Length; i++) { pages[i] = new Panel { Dock = DockStyle.Fill, AutoScroll = true }; content.Controls.Add(pages[i]); }
            var welcome = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
            welcome.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(660, 0), Text = "Install the Reports web app and local planner as a dedicated Windows service.\n\nNo Visual Studio, .NET SDK or separate Python installation is needed on the target. Ollama and the selected model must already be available locally.\n\nThe installer preserves settings and never removes Laserfiche, models, databases or unrelated files.\n\nFor migration from the older scheduled-task installer, run unregister.ps1 first to prevent two copies running." });
            welcome.Controls.Add(maintenance); welcome.Controls.Add(repair); welcome.Controls.Add(remove); pages[0].Controls.Add(welcome);
            var connection = Grid(pages[1]);
            Field(connection, "ServerUrl", "Laserfiche server URL", "");
            Field(connection, "ApiBasePath", "Repository API base path", "/LFRepositoryAPI");
            AddRow(connection, "API version", version); version.Items.AddRange(new object[] { "Auto", "v1", "v2" }); version.SelectedIndex = 0;
            Field(connection, "RepositoryId", "Default repository (optional)", "");
            Field(connection, "WebClientBaseUrl", "Laserfiche Web Client URL (optional)", "");
            AddRow(connection, "", new Label { AutoSize = true, Text = "Sign in with your Laserfiche account after installation. No passwords enter MSI logs.", MaximumSize = new Size(430, 0) });
            var deployment = Grid(pages[2]);
            Field(deployment, "Port", "Reports local web port", "5187"); Field(deployment, "GraphPort", "Local planner port", "8766");
            Field(deployment, "Model", "Ollama model name", "qwen2.5:14b"); Field(deployment, "OllamaUrl", "Local Ollama address", "http://127.0.0.1:11434");
            AddRow(deployment, "Install directory", new Label { AutoSize = true, Text = Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LaserficheReports") });
            AddRow(deployment, "Configuration", new Label { AutoSize = true, Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LaserficheReports") });
            pages[3].Controls.Add(review); pages[4].Controls.Add(result); pages[4].Controls.Add(progress);
            LoadSettings();
            ba.DetectedChanged += () => { maintenance.Visible = repair.Visible = remove.Visible = ba.Installed; next.Enabled = true; if (requested == LaunchAction.Uninstall) remove.Checked = true; };
            ba.ProgressChanged += percent => progress.Value = Math.Max(0, Math.Min(100, percent));
            ba.Completed += async (ok, message) =>
            {
                applying = false; complete = true; result.Text = message; next.Enabled = true; cancel.Enabled = true;
                ShowPage(5); var label = new Label { Dock = DockStyle.Fill, Text = message, AutoSize = false }; pages[5].Controls.Add(label);
                if (ok && !remove.Checked)
                {
                    label.Text += "\n\nChecking the installed web app and local AI service...";
                    label.Text += "\n\n" + await CheckInstalledAsync();
                    var open = new Button { Text = "Open Reports", Dock = DockStyle.Bottom, Height = 42 };
                    open.Click += (_, __) => Process.Start(new ProcessStartInfo($"http://127.0.0.1:{fields["Port"].Text}") { UseShellExecute = true }); pages[5].Controls.Add(open);
                }
            };
            next.Click += (_, __) => Advance(); back.Click += (_, __) => ShowPage(Math.Max(0, page - 1)); cancel.Click += (_, __) => Close();
            FormClosing += (_, e) => { if (applying) e.Cancel = true; };
            maintenance.Visible = repair.Visible = remove.Visible = false; next.Enabled = false; ShowPage(0);
        }
        private void Advance()
        {
            if (complete) { Close(); return; }
            if (page == 0 && remove.Visible && remove.Checked) { profile = ""; review.Text = "Remove only Reports-owned files and its Windows service. Configuration and unknown files stay in place."; ShowPage(3); return; }
            if (page == 2)
            {
                try
                {
                    var values = new Dictionary<string, object>(); foreach (var item in fields) values[item.Key] = item.Value.Text.Trim(); values["ApiVersion"] = version.Text;
                    if (!Uri.TryCreate((string)values["ServerUrl"], UriKind.Absolute, out var server) || (server.Scheme != "http" && server.Scheme != "https") || !string.IsNullOrEmpty(server.UserInfo)) throw new ArgumentException("Enter the target Laserfiche server URL.");
                    if (!int.TryParse(fields["Port"].Text, out var port) || port < 1024 || port > 65535 || !int.TryParse(fields["GraphPort"].Text, out var graphPort) || graphPort < 1024 || graphPort > 65535 || port == graphPort) throw new ArgumentException("Choose two different ports between 1024 and 65535.");
                    values["Port"] = port; values["GraphPort"] = graphPort;
                    profile = Convert.ToBase64String(Encoding.UTF8.GetBytes(json.Serialize(values)));
                    review.Text = $"Laserfiche: {fields["ServerUrl"].Text}\nAPI: {fields["ApiBasePath"].Text} ({version.Text})\nRepository: {fields["RepositoryId"].Text}\nWeb Client: {fields["WebClientBaseUrl"].Text}\n\nReports: http://127.0.0.1:{port}\nPlanner: http://127.0.0.1:{graphPort}\nModel: {fields["Model"].Text}\n\nInstaller configuration is merged. Runtime settings and credentials are preserved.\nA successful MSI transaction does not prove access to your repository; sign in and test a report.";
                }
                catch (Exception error) { MessageBox.Show(this, error.Message, "Review settings"); return; }
            }
            if (page == 3)
            { applying = true; next.Enabled = back.Enabled = cancel.Enabled = false; ShowPage(4); ba.Begin(remove.Visible && remove.Checked ? LaunchAction.Uninstall : ba.Installed ? LaunchAction.Repair : LaunchAction.Install, profile); return; }
            ShowPage(page + 1);
        }
        private void ShowPage(int index)
        {
            page = index; foreach (var panel in pages) panel.Visible = false; pages[index].Visible = true; pages[index].BringToFront();
            title.Text = titles[index]; subtitle.Text = index == 1 ? "Use the server address reachable from this machine." : "Local smart document reports";
            back.Visible = index > 0 && index < 4; next.Text = index == 3 ? (remove.Checked && remove.Visible ? "Uninstall" : "Install") : index == 5 ? "Finish" : "Next";
        }
        private static TableLayoutPanel Grid(Panel page)
        { var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = new Padding(0, 10, 0, 0) }; table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 210)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); page.Controls.Add(table); return table; }
        private static void AddRow(TableLayoutPanel table, string label, Control control)
        { var row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); table.Controls.Add(new Label { Text = label, AutoSize = true, Padding = new Padding(0, 8, 0, 0), Margin = new Padding(0, 8, 12, 8) }, 0, row); control.Margin = new Padding(0, 8, 0, 8); table.Controls.Add(control, 1, row); }
        private void Field(TableLayoutPanel table, string key, string label, string value)
        { var box = new TextBox { Text = value, Dock = DockStyle.Fill }; fields[key] = box; AddRow(table, label, box); }
        private void LoadSettings()
        {
            // Preserve current-machine settings during upgrade; never bake developer settings into the release.
            foreach (var path in new[] { Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "defaults.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LaserficheReports", "web", "appsettings.Local.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LaserficheReports", "laserfiche.config.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LaserficheReports", "laserfiche.runtime.json"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "LaserficheReports", "hosting.config.json") })
            {
                if (!File.Exists(path)) continue;
                try
                {
                    InstallerFileSafety.EnsureNoReparsePoints(path);
                    var values = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
                    if (values.TryGetValue("Laserfiche", out var lf) && lf is Dictionary<string, object> nested) values = nested;
                    foreach (var item in values)
                        if (fields.TryGetValue(item.Key, out var box)) box.Text = Convert.ToString(item.Value);
                        else if (item.Key == "ApiVersion" && version.Items.Contains(Convert.ToString(item.Value))) version.SelectedItem = Convert.ToString(item.Value);
                }
                catch (Exception error) { MessageBox.Show(this, "Could not load existing settings: " + error.Message); }
            }
        }
        private async Task<string> CheckInstalledAsync()
        {
            using (var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(3) })
            {
                var url = $"http://127.0.0.1:{fields["Port"].Text}";
                for (var i = 0; i < 30; i++)
                {
                    try
                    {
                        using (var response = await client.GetAsync(url + "/api/app/status"))
                        {
                            if (response.IsSuccessStatusCode)
                            {
                                var status = json.Deserialize<Dictionary<string, object>>(await response.Content.ReadAsStringAsync());
                                var expectedPath = Path.Combine(Environment.GetEnvironmentVariable("ProgramW6432") ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LaserficheReports", "web", "build-info.json");
                                var expected = json.Deserialize<Dictionary<string, object>>(File.ReadAllText(expectedPath));
                                if (!status.TryGetValue("build", out var buildValue) || !(buildValue is Dictionary<string, object> build) ||
                                    !build.TryGetValue("SourceCommit", out var actualCommit) || Convert.ToString(actualCommit) != Convert.ToString(expected["SourceCommit"]))
                                    return "A different Reports build answered on this port. Stop the old copy and restart the LaserficheReports service.";
                                using (var ai = await client.GetAsync($"http://127.0.0.1:{fields["GraphPort"].Text}/health"))
                                {
                                    if (!ai.IsSuccessStatusCode) return "Reports is installed and the web app is running. Local AI is not ready: open Ollama and install the selected model. Check ProgramData\\LaserficheReports\\logs.";
                                    var health = json.Deserialize<Dictionary<string, object>>(await ai.Content.ReadAsStringAsync());
                                    if (!health.TryGetValue("model", out var actualModel) || Convert.ToString(actualModel) != fields["Model"].Text.Trim())
                                        return "The planner reports a different model. Stop the old planner and restart the Reports service.";
                                    return "Reports web app and planner are ready. Sign in and run a report to verify repository access.";
                                }
                            }
                        }
                    }
                    catch (HttpRequestException) { } catch (TaskCanceledException) { }
                    catch (Exception error) { return "Installation completed, but runtime identity could not be verified: " + error.Message; }
                    await Task.Delay(1000);
                }
                return "Installation completed, but runtime startup could not be verified. Read ProgramData\\LaserficheReports\\logs before starting another copy.";
            }
        }
    }
}
