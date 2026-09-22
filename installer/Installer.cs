using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: AssemblyTitle("Sea of Stars Local Map Installer")]
[assembly: AssemblyProduct("Sea of Stars Local Map Installer")]
[assembly: AssemblyVersion("0.9.3.0")]
[assembly: AssemblyFileVersion("0.9.3.0")]

namespace SeaOfStarsLocalMapInstaller
{
    internal sealed class FileRecord
    {
        public string Path;
        public long Bytes;
        public string Sha256;
    }

    internal sealed class PackageInfo
    {
        public string PackagePath;
        public string Name;
        public string Version;
        public long Bytes;
        public List<FileRecord> Files = new List<FileRecord>();

        public static PackageInfo FindAndLoad()
        {
            string directory = AppDomain.CurrentDomain.BaseDirectory;
            string[] packages = Directory.GetFiles(directory, "*.lmpkg", SearchOption.TopDirectoryOnly);
            if (packages.Length != 1)
                throw new InvalidOperationException("Exactly one .lmpkg file must be placed next to the installer.");
            return Load(packages[0]);
        }

        public static PackageInfo Load(string path)
        {
            PackageInfo result = new PackageInfo();
            result.PackagePath = System.IO.Path.GetFullPath(path);
            using (FileStream stream = File.OpenRead(result.PackagePath))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                ZipArchiveEntry infoEntry = archive.GetEntry("installer-info.ini");
                ZipArchiveEntry manifestEntry = archive.GetEntry("installer-manifest.tsv");
                if (infoEntry == null || manifestEntry == null)
                    throw new InvalidDataException("The package does not contain the installer metadata.");
                Dictionary<string, string> info = ReadIni(infoEntry);
                if (!info.ContainsKey("Format") || info["Format"] != "1" ||
                    !info.ContainsKey("ModId") || info["ModId"] != "local.seaofstars.localmap")
                    throw new InvalidDataException("This is not a supported Local Map package.");
                result.Name = info.ContainsKey("Name") ? info["Name"] : "Local Map";
                result.Version = info.ContainsKey("Version") ? info["Version"] : "?";
                result.Bytes = long.Parse(info["Bytes"], CultureInfo.InvariantCulture);
                using (StreamReader reader = new StreamReader(manifestEntry.Open(), Encoding.UTF8, true))
                {
                    string line;
                    HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    while ((line = reader.ReadLine()) != null)
                    {
                        if (line.Length == 0 || line[0] == '#') continue;
                        string[] parts = line.Split('\t');
                        if (parts.Length != 3) throw new InvalidDataException("The package file manifest is malformed.");
                        string relative = NormalizeRelative(parts[0]);
                        if (!seen.Add(relative)) throw new InvalidDataException("Duplicate path in package manifest: " + relative);
                        result.Files.Add(new FileRecord
                        {
                            Path = relative,
                            Bytes = long.Parse(parts[1], CultureInfo.InvariantCulture),
                            Sha256 = parts[2].ToLowerInvariant()
                        });
                    }
                }
                if (result.Files.Count == 0 || result.Files.Sum(x => x.Bytes) != result.Bytes)
                    throw new InvalidDataException("The package size does not match its manifest.");
            }
            return result;
        }

        private static Dictionary<string, string> ReadIni(ZipArchiveEntry entry)
        {
            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (StreamReader reader = new StreamReader(entry.Open(), Encoding.UTF8, true))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int separator = line.IndexOf('=');
                    if (separator > 0) values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
                }
            }
            return values;
        }

        public static string NormalizeRelative(string value)
        {
            string relative = value.Replace('/', '\\').TrimStart('\\');
            if (relative.Length == 0 || System.IO.Path.IsPathRooted(relative) || relative.IndexOf(':') >= 0 ||
                relative.Split('\\').Any(x => x == ".." || x.Length == 0))
                throw new InvalidDataException("Unsafe path in package: " + value);
            return relative;
        }
    }

    internal sealed class ProgressInfo
    {
        public int Percent;
        public string Message;
        public ProgressInfo(int percent, string message) { Percent = percent; Message = message; }
    }

    internal static class InstallerEngine
    {
        public const string PluginRelative = @"BepInEx\plugins\LocalMap";

        public static List<string> DetectGameFolders()
        {
            HashSet<string> steamRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddRegistryPath(steamRoots, Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");
            AddRegistryPath(steamRoots, Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                steamRoots.Add(System.IO.Path.Combine(drive.RootDirectory.FullName, "Steam"));
                steamRoots.Add(System.IO.Path.Combine(drive.RootDirectory.FullName, "SteamLibrary"));
                steamRoots.Add(System.IO.Path.Combine(drive.RootDirectory.FullName, @"Program Files (x86)\Steam"));
            }
            List<string> initialRoots = steamRoots.ToList();
            foreach (string root in initialRoots)
            {
                string vdf = System.IO.Path.Combine(root, @"steamapps\libraryfolders.vdf");
                if (!File.Exists(vdf)) continue;
                try
                {
                    string text = File.ReadAllText(vdf);
                    foreach (Match match in Regex.Matches(text, "\\\"path\\\"\\s*\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                        steamRoots.Add(match.Groups[1].Value.Replace("\\\\", "\\"));
                }
                catch { }
            }
            HashSet<string> games = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string root in steamRoots)
            {
                try
                {
                    string candidate = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, @"steamapps\common\Sea of Stars"));
                    if (File.Exists(System.IO.Path.Combine(candidate, "SeaOfStars.exe"))) games.Add(candidate);
                }
                catch { }
            }
            return games.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static void AddRegistryPath(HashSet<string> paths, RegistryKey hive, string keyName, string valueName)
        {
            try
            {
                using (RegistryKey key = hive.OpenSubKey(keyName))
                {
                    object value = key == null ? null : key.GetValue(valueName);
                    if (value != null) paths.Add(Convert.ToString(value));
                }
            }
            catch { }
        }

        public static void ValidateGame(string gameFolder, bool requireBepInEx)
        {
            if (String.IsNullOrWhiteSpace(gameFolder)) throw new InvalidOperationException("No game folder was selected.");
            string root = System.IO.Path.GetFullPath(gameFolder.Trim().Trim('"'));
            if (!File.Exists(System.IO.Path.Combine(root, "SeaOfStars.exe")))
                throw new InvalidOperationException("SeaOfStars.exe was not found in the selected folder.");
            if (requireBepInEx && !File.Exists(System.IO.Path.Combine(root, @"BepInEx\core\BepInEx.Core.dll")))
                throw new InvalidOperationException("BepInEx was not found. Install BepInEx for Sea of Stars first.");
            if (Process.GetProcessesByName("SeaOfStars").Length != 0)
                throw new InvalidOperationException("Sea of Stars is running. Save and close the game before continuing.");
        }

        public static void VerifyPackage(PackageInfo package, Action<ProgressInfo> progress)
        {
            using (FileStream stream = File.OpenRead(package.PackagePath))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
            {
                HashSet<string> expected = new HashSet<string>(package.Files.Select(x => "payload/" + x.Path.Replace('\\', '/')), StringComparer.OrdinalIgnoreCase);
                HashSet<string> actual = new HashSet<string>(archive.Entries.Where(x => x.Name.Length != 0 && x.FullName.StartsWith("payload/", StringComparison.OrdinalIgnoreCase)).Select(x => x.FullName), StringComparer.OrdinalIgnoreCase);
                if (!expected.SetEquals(actual)) throw new InvalidDataException("The package contents do not match the manifest.");
                for (int i = 0; i < package.Files.Count; i++)
                {
                    FileRecord record = package.Files[i];
                    ZipArchiveEntry entry = archive.GetEntry("payload/" + record.Path.Replace('\\', '/'));
                    if (entry == null || entry.Length != record.Bytes) throw new InvalidDataException("File size mismatch: " + record.Path);
                    using (Stream input = entry.Open())
                    {
                        string hash = HashStream(input);
                        if (!String.Equals(hash, record.Sha256, StringComparison.OrdinalIgnoreCase))
                            throw new InvalidDataException("Checksum mismatch: " + record.Path);
                    }
                    Report(progress, i + 1, package.Files.Count, "Verifying package: " + record.Path);
                }
            }
        }

        public static void Install(PackageInfo package, string gameFolder, Action<ProgressInfo> progress)
        {
            ValidateGame(gameFolder, true);
            string game = System.IO.Path.GetFullPath(gameFolder.Trim().Trim('"'));
            string plugins = System.IO.Path.Combine(game, @"BepInEx\plugins");
            Directory.CreateDirectory(plugins);
            DriveInfo drive = new DriveInfo(System.IO.Path.GetPathRoot(game));
            if (drive.AvailableFreeSpace < package.Bytes + 100L * 1024 * 1024)
                throw new IOException("Not enough free space. At least " + FormatBytes(package.Bytes + 100L * 1024 * 1024) + " is required.");

            string suffix = Guid.NewGuid().ToString("N");
            string target = System.IO.Path.Combine(game, PluginRelative);
            string stage = System.IO.Path.Combine(plugins, ".LocalMap.installing-" + suffix);
            string rollback = System.IO.Path.Combine(plugins, ".LocalMap.rollback-" + suffix);
            Directory.CreateDirectory(stage);
            try
            {
                using (FileStream stream = File.OpenRead(package.PackagePath))
                using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Read, false))
                {
                    for (int i = 0; i < package.Files.Count; i++)
                    {
                        FileRecord record = package.Files[i];
                        ZipArchiveEntry entry = archive.GetEntry("payload/" + record.Path.Replace('\\', '/'));
                        if (entry == null || entry.Length != record.Bytes) throw new InvalidDataException("File is missing from the package: " + record.Path);
                        string output = SafeCombine(stage, record.Path);
                        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output));
                        using (Stream input = entry.Open())
                        using (FileStream destination = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        using (SHA256 digest = SHA256.Create())
                        {
                            byte[] buffer = new byte[1024 * 1024];
                            int read;
                            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                destination.Write(buffer, 0, read);
                                digest.TransformBlock(buffer, 0, read, null, 0);
                            }
                            digest.TransformFinalBlock(new byte[0], 0, 0);
                            string hash = ToHex(digest.Hash);
                            if (!String.Equals(hash, record.Sha256, StringComparison.OrdinalIgnoreCase))
                                throw new InvalidDataException("Checksum mismatch: " + record.Path);
                        }
                        Report(progress, i + 1, package.Files.Count, "Installing: " + record.Path);
                    }
                }

                BackupSmallFiles(target, System.IO.Path.Combine(plugins, "LocalMap Installer Backups"));
                if (Directory.Exists(target)) Directory.Move(target, rollback);
                try
                {
                    Directory.Move(stage, target);
                    File.WriteAllText(System.IO.Path.Combine(target, ".localmap-installer.txt"),
                        "Name=" + package.Name + Environment.NewLine + "Version=" + package.Version + Environment.NewLine +
                        "InstalledUtc=" + DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture) + Environment.NewLine,
                        new UTF8Encoding(false));
                }
                catch
                {
                    if (Directory.Exists(target)) Directory.Delete(target, true);
                    if (Directory.Exists(rollback)) Directory.Move(rollback, target);
                    throw;
                }
                if (Directory.Exists(rollback)) Directory.Delete(rollback, true);
                Report(progress, 1, 1, "Local Map " + package.Version + " was installed.");
            }
            catch
            {
                if (Directory.Exists(stage)) Directory.Delete(stage, true);
                throw;
            }
        }

        public static void VerifyInstalled(PackageInfo package, string gameFolder, Action<ProgressInfo> progress)
        {
            ValidateGame(gameFolder, false);
            string target = System.IO.Path.Combine(System.IO.Path.GetFullPath(gameFolder.Trim().Trim('"')), PluginRelative);
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException("Local Map is not installed.");
            for (int i = 0; i < package.Files.Count; i++)
            {
                FileRecord record = package.Files[i];
                string path = SafeCombine(target, record.Path);
                if (!File.Exists(path) || new FileInfo(path).Length != record.Bytes ||
                    !String.Equals(HashFile(path), record.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Installed file does not match: " + record.Path);
                Report(progress, i + 1, package.Files.Count, "Verifying installation: " + record.Path);
            }
        }

        public static void Uninstall(string gameFolder, Action<ProgressInfo> progress)
        {
            ValidateGame(gameFolder, false);
            string game = System.IO.Path.GetFullPath(gameFolder.Trim().Trim('"'));
            string target = System.IO.Path.Combine(game, PluginRelative);
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException("Local Map is not installed.");
            string removed = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(target), ".LocalMap.removing-" + Guid.NewGuid().ToString("N"));
            Directory.Move(target, removed);
            try { Directory.Delete(removed, true); }
            catch
            {
                if (!Directory.Exists(target) && Directory.Exists(removed)) Directory.Move(removed, target);
                throw;
            }
            Report(progress, 1, 1, "Local Map was removed. Exploration fog and settings were preserved.");
        }

        private static void BackupSmallFiles(string target, string backupRoot)
        {
            if (!Directory.Exists(target)) return;
            string backup = System.IO.Path.Combine(backupRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            foreach (string file in Directory.GetFiles(target, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(target.Length).TrimStart('\\');
                if (relative.StartsWith("Maps\\", StringComparison.OrdinalIgnoreCase)) continue;
                string output = SafeCombine(backup, relative);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output));
                File.Copy(file, output, true);
            }
        }

        private static string SafeCombine(string root, string relative)
        {
            string safeRoot = System.IO.Path.GetFullPath(root).TrimEnd('\\') + "\\";
            string result = System.IO.Path.GetFullPath(System.IO.Path.Combine(safeRoot, PackageInfo.NormalizeRelative(relative)));
            if (!result.StartsWith(safeRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Path escapes the destination folder.");
            return result;
        }

        private static void Report(Action<ProgressInfo> progress, int current, int total, string message)
        {
            if (progress == null) return;
            int percent = total == 0 ? 100 : (int)((long)current * 100 / total);
            int previousPercent = current <= 1 || total == 0 ? -1 : (int)((long)(current - 1) * 100 / total);
            if (current == total || percent != previousPercent)
                progress(new ProgressInfo(percent, message));
        }

        public static string HashFile(string path)
        {
            using (FileStream stream = File.OpenRead(path)) return HashStream(stream);
        }

        private static string HashStream(Stream stream)
        {
            using (SHA256 digest = SHA256.Create()) return ToHex(digest.ComputeHash(stream));
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder value = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) value.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return value.ToString();
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = { "B", "KB", "MB", "GB" };
            double value = bytes; int unit = 0;
            while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
            return value.ToString(unit == 0 ? "0" : "0.00", CultureInfo.CurrentCulture) + " " + units[unit];
        }
    }

    internal sealed class InstallerForm : Form
    {
        private readonly PackageInfo package;
        private readonly TextBox pathBox = new TextBox();
        private readonly Label pathState = new Label();
        private readonly ProgressBar progress = new ProgressBar();
        private readonly RichTextBox log = new RichTextBox();
        private readonly Button install = new Button();
        private readonly Button verify = new Button();
        private readonly Button remove = new Button();
        private readonly Button browse = new Button();
        private readonly Button detect = new Button();
        private bool busy;

        public InstallerForm(PackageInfo packageInfo)
        {
            package = packageInfo;
            Text = "Sea of Stars — Local Map Installer";
            ClientSize = new Size(760, 520);
            MinimumSize = new Size(720, 500);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(21, 29, 42);
            ForeColor = Color.White;
            Font = new Font("Segoe UI", 10F);

            Label title = NewLabel("LOCAL MAP", 26F, FontStyle.Bold, Color.White);
            title.Location = new Point(28, 20); title.AutoSize = true;
            Label version = NewLabel("Installer version " + package.Version, 11F, FontStyle.Regular, Color.FromArgb(122, 220, 239));
            version.Location = new Point(31, 62); version.AutoSize = true;
            Label details = NewLabel(package.Files.Count.ToString(CultureInfo.InvariantCulture) + " files · " + InstallerEngine.FormatBytes(package.Bytes) + " · Local Map only", 10F, FontStyle.Regular, Color.FromArgb(180, 194, 216));
            details.Location = new Point(31, 88); details.AutoSize = true;

            Panel line = new Panel(); line.BackColor = Color.FromArgb(99, 125, 163); line.Location = new Point(28, 120); line.Size = new Size(704, 2); line.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Label folderLabel = NewLabel("Sea of Stars folder", 11F, FontStyle.Bold, Color.White);
            folderLabel.Location = new Point(28, 142); folderLabel.AutoSize = true;
            pathBox.Location = new Point(31, 172); pathBox.Size = new Size(535, 29); pathBox.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; pathBox.TextChanged += delegate { UpdatePathState(); };
            browse.Text = "Browse..."; browse.Location = new Point(578, 170); browse.Size = new Size(92, 32); browse.Anchor = AnchorStyles.Top | AnchorStyles.Right; StyleButton(browse, false); browse.Click += BrowseClicked;
            detect.Text = "Find"; detect.Location = new Point(676, 170); detect.Size = new Size(56, 32); detect.Anchor = AnchorStyles.Top | AnchorStyles.Right; StyleButton(detect, false); detect.Click += delegate { DetectGame(); };
            pathState.Location = new Point(31, 208); pathState.Size = new Size(700, 23); pathState.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;

            install.Text = "INSTALL"; install.Location = new Point(31, 248); install.Size = new Size(178, 44); StyleButton(install, true); install.Click += delegate { RunOperation("Installation", delegate(Action<ProgressInfo> p) { InstallerEngine.Install(package, pathBox.Text, p); }); };
            verify.Text = "VERIFY"; verify.Location = new Point(218, 248); verify.Size = new Size(160, 44); StyleButton(verify, false); verify.Click += delegate { RunOperation("Verification", delegate(Action<ProgressInfo> p) { InstallerEngine.VerifyInstalled(package, pathBox.Text, p); }); };
            remove.Text = "REMOVE MOD"; remove.Location = new Point(387, 248); remove.Size = new Size(178, 44); StyleButton(remove, false); remove.Click += RemoveClicked;

            Label preserve = NewLabel("Removal preserves exploration fog and settings.", 9.5F, FontStyle.Regular, Color.FromArgb(180, 194, 216));
            preserve.Location = new Point(31, 305); preserve.AutoSize = true;
            progress.Location = new Point(31, 335); progress.Size = new Size(701, 18); progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            progress.Style = ProgressBarStyle.Continuous;
            log.Location = new Point(31, 368); log.Size = new Size(701, 124); log.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            log.BackColor = Color.FromArgb(31, 40, 56); log.ForeColor = Color.FromArgb(221, 231, 244); log.BorderStyle = BorderStyle.FixedSingle; log.ReadOnly = true; log.Font = new Font("Consolas", 9F);

            Controls.AddRange(new Control[] { title, version, details, line, folderLabel, pathBox, browse, detect, pathState, install, verify, remove, preserve, progress, log });
            DetectGame();
            Append("Package found: " + System.IO.Path.GetFileName(package.PackagePath));
        }

        private static Label NewLabel(string text, float size, FontStyle style, Color color)
        {
            Label label = new Label(); label.Text = text; label.Font = new Font("Segoe UI", size, style); label.ForeColor = color; label.BackColor = Color.Transparent; return label;
        }

        private static void StyleButton(Button button, bool primary)
        {
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Color.FromArgb(104, 137, 177);
            button.BackColor = primary ? Color.FromArgb(50, 173, 199) : Color.FromArgb(52, 67, 91);
            button.ForeColor = Color.White;
            button.Font = new Font("Segoe UI", 9.5F, FontStyle.Bold);
        }

        private void DetectGame()
        {
            List<string> folders = InstallerEngine.DetectGameFolders();
            if (folders.Count > 0) { pathBox.Text = folders[0]; Append("Game found: " + folders[0]); }
            else { pathState.Text = "The game was not detected automatically. Select its folder manually."; pathState.ForeColor = Color.FromArgb(242, 188, 82); }
        }

        private void UpdatePathState()
        {
            try
            {
                string root = pathBox.Text.Trim().Trim('"');
                bool game = File.Exists(System.IO.Path.Combine(root, "SeaOfStars.exe"));
                bool bep = File.Exists(System.IO.Path.Combine(root, @"BepInEx\core\BepInEx.Core.dll"));
                pathState.Text = game ? (bep ? "Sea of Stars and BepInEx were found." : "The game was found, but BepInEx is not installed.") : "SeaOfStars.exe was not found.";
                pathState.ForeColor = game && bep ? Color.FromArgb(118, 222, 151) : Color.FromArgb(242, 188, 82);
            }
            catch { pathState.Text = "Invalid path."; pathState.ForeColor = Color.FromArgb(242, 110, 110); }
        }

        private void BrowseClicked(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Select the folder that contains SeaOfStars.exe";
                dialog.SelectedPath = Directory.Exists(pathBox.Text) ? pathBox.Text : "";
                if (dialog.ShowDialog(this) == DialogResult.OK) pathBox.Text = dialog.SelectedPath;
            }
        }

        private void RemoveClicked(object sender, EventArgs e)
        {
            if (MessageBox.Show(this, "Remove Local Map? Exploration fog and settings will be preserved.", "Remove Local Map", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            RunOperation("Removal", delegate(Action<ProgressInfo> p) { InstallerEngine.Uninstall(pathBox.Text, p); });
        }

        private void RunOperation(string name, Action<Action<ProgressInfo>> operation)
        {
            if (busy) return;
            busy = true; SetButtons(false); progress.Value = 0; Append(name + " started.");
            Task.Run(delegate
            {
                operation(delegate(ProgressInfo update)
                {
                    BeginInvoke((Action)delegate
                    {
                        progress.Value = Math.Max(0, Math.Min(100, update.Percent));
                        if (update.Percent == 100 || update.Percent % 5 == 0) Append(update.Message);
                    });
                });
            }).ContinueWith(task => BeginInvoke((Action)delegate
            {
                busy = false; SetButtons(true);
                if (task.IsFaulted)
                {
                    Exception error = task.Exception.Flatten().InnerExceptions[0];
                    Append("ERROR: " + error.Message);
                    MessageBox.Show(this, error.Message, name + " failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    progress.Value = 100; Append(name + " completed successfully.");
                    MessageBox.Show(this, name + " completed successfully.", "Local Map", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                UpdatePathState();
            }));
        }

        private void SetButtons(bool enabled)
        {
            install.Enabled = verify.Enabled = remove.Enabled = browse.Enabled = detect.Enabled = pathBox.Enabled = enabled;
        }

        private void Append(string value)
        {
            log.AppendText("[" + DateTime.Now.ToString("HH:mm:ss") + "] " + value + Environment.NewLine);
            log.SelectionStart = log.TextLength; log.ScrollToCaret();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (busy) { e.Cancel = true; MessageBox.Show(this, "Wait for the current operation to finish.", "Local Map", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            base.OnFormClosing(e);
        }
    }

    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                PackageInfo package = PackageInfo.FindAndLoad();
                if (args.Length > 0)
                {
                    string command = args[0].ToLowerInvariant();
                    Action<ProgressInfo> consoleProgress = delegate(ProgressInfo p) { if (p.Percent == 100 || p.Percent % 10 == 0) Console.WriteLine(p.Percent + "% " + p.Message); };
                    if (command == "--verify-package") InstallerEngine.VerifyPackage(package, consoleProgress);
                    else if (command == "--list-games") foreach (string game in InstallerEngine.DetectGameFolders()) Console.WriteLine(game);
                    else if (command == "--verify-installed" && args.Length == 2) InstallerEngine.VerifyInstalled(package, args[1], consoleProgress);
                    else if (command == "--install" && args.Length == 2) InstallerEngine.Install(package, args[1], consoleProgress);
                    else if (command == "--uninstall" && args.Length == 2) InstallerEngine.Uninstall(args[1], consoleProgress);
                    else throw new ArgumentException("Unknown command-line arguments.");
                    Console.WriteLine("OK"); return 0;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new InstallerForm(package));
                return 0;
            }
            catch (Exception error)
            {
                if (args.Length == 0 && Environment.UserInteractive)
                    MessageBox.Show(error.Message, "Local Map Installer", MessageBoxButtons.OK, MessageBoxIcon.Error);
                try { Console.Error.WriteLine(error.ToString()); } catch { }
                return 1;
            }
        }
    }
}
