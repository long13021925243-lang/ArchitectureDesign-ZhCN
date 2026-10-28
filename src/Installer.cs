// Build as x64 WinForms with manifest.json and payload.zip as named embedded resources.
// References: System.Web.Extensions, System.IO.Compression,
// System.IO.Compression.FileSystem, System.Windows.Forms, System.Drawing.
// Entries with windowsFontName are sourced from Windows Fonts and must not be in payload.zip.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace ArchitectureDesignZhCN
{
    public sealed class Patch { public long offset; public string oldHex; public string newHex; }
    public sealed class PayloadFile { public string relative; public string sha256; public string windowsFontName; }
    public sealed class PreviousInstallation { public string patchedSha256; public List<PayloadFile> payloadFiles; }
    public sealed class Manifest
    {
        public string originalSha256; public string patchedSha256; public string buildId;
        public string exeRelative; public List<Patch> patches; public List<PayloadFile> payloadFiles;
        public string appendHex; public string revision; public List<PreviousInstallation> previousInstallations;
    }
    public sealed class JournalEntry
    {
        public string relative; public bool existedBefore; public string beforeSha256;
        public string installedSha256; public string backupRelative;
    }
    public sealed class Journal
    {
        public int schemaVersion; public string buildId; public string originalSha256;
        public string patchedSha256; public string status; public string createdUtc;
        public string updatedUtc; public string note; public List<JournalEntry> files;
    }
    internal sealed class Snapshot
    {
        public string Relative; public bool Exists; public string Hash; public string Copy;
    }
    internal sealed class Mutation { public Snapshot Before; public string AfterHash; }
    internal sealed class InstallerError : Exception
    {
        public readonly int ExitCode;
        public InstallerError(string message, int code) : base(message) { ExitCode = code; }
    }

    internal sealed class Engine
    {
        internal readonly Manifest Manifest;
        internal readonly string Root;
        private readonly string BackupRoot;
        private readonly string JournalPath;
        private readonly Action<string> Logger;
        private readonly JavaScriptSerializer Json = new JavaScriptSerializer { MaxJsonLength = Int32.MaxValue, RecursionLimit = 100 };
        private readonly Dictionary<string, string> Expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> Ordered = new List<string>();
        private readonly Dictionary<string, Dictionary<string, string>> KnownVersions = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        // Font bytes are supplied by this Windows installation, never by the release archive.
        private readonly Dictionary<string, string> SystemFonts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> JournalFontHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly string MissingFontHash = new string('0', 64);

        internal Engine(string root, Action<string> logger)
        {
            Logger = logger;
            Root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Root.Length < 3) throw new InstallerError("请将安装器放在游戏根目录中运行。", 2);
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("manifest.json"))
            {
                if (stream == null) throw new InstallerError("安装器缺少内置清单，请重新获取完整安装器。", 2);
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                    Manifest = Json.Deserialize<Manifest>(reader.ReadToEnd());
            }
            if (Manifest == null || !ValidHash(Manifest.originalSha256) || !ValidHash(Manifest.patchedSha256)
                || String.IsNullOrEmpty(Manifest.buildId) || !Regex.IsMatch(Manifest.buildId, "^[A-Za-z0-9_-]+$")
                || Manifest.patches == null || Manifest.payloadFiles == null || Manifest.payloadFiles.Count == 0)
                throw new InstallerError("安装器内置清单无效。", 2);
            Manifest.exeRelative = Relative(Manifest.exeRelative);
            AddTarget(Manifest.exeRelative, Manifest.patchedSha256);
            foreach (PayloadFile item in Manifest.payloadFiles)
            {
                if (item == null) throw new InstallerError("资源清单包含空文件项。", 2);
                item.relative = Relative(item.relative);
                if (!ValidHash(item.sha256)) throw new InstallerError("资源清单中的校验值无效。", 2);
                if (item.windowsFontName != null)
                {
                    string requiredName = Same(item.relative, @"ArchitectureDesign\Content\Data\Fonts\CN.ttf") ? "msyh.ttc"
                        : Same(item.relative, @"ArchitectureDesign\Content\Data\Fonts\CNB.ttf") ? "msyhbd.ttc" : null;
                    if (requiredName == null || !Same(requiredName, item.windowsFontName) || SystemFonts.ContainsKey(item.relative))
                        throw new InstallerError("本机字体清单无效。", 2);
                    SystemFonts.Add(item.relative, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), requiredName));
                }
                AddTarget(item.relative, item.sha256);
            }
            if (SystemFonts.Count != 0 && SystemFonts.Count != 2) throw new InstallerError("本机字体清单不完整。", 2);
            RefreshSystemFonts(false);
            KnownVersions.Add(Manifest.patchedSha256, Expected);
            if (Manifest.previousInstallations != null)
            foreach (PreviousInstallation prior in Manifest.previousInstallations)
            {
                if (prior == null || !ValidHash(prior.patchedSha256) || prior.payloadFiles == null || KnownVersions.ContainsKey(prior.patchedSha256))
                    throw new InstallerError("旧版汉化升级清单无效。", 2);
                Dictionary<string, string> profile = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                profile.Add(Manifest.exeRelative, prior.patchedSha256);
                foreach (PayloadFile item in prior.payloadFiles)
                {
                    if (item == null || item.windowsFontName != null) throw new InstallerError("旧版资源必须使用固定校验清单。", 2);
                    string relative = Relative(item.relative);
                    if (!Expected.ContainsKey(relative) || profile.ContainsKey(relative) || !ValidHash(item.sha256))
                        throw new InstallerError("旧版汉化升级文件清单无效。", 2);
                    profile.Add(relative, item.sha256);
                }
                if (profile.Count != Expected.Count) throw new InstallerError("旧版汉化升级文件清单不完整。", 2);
                KnownVersions.Add(prior.patchedSha256, profile);
            }
            BackupRoot = Path.Combine(Root, "ArchitectureDesign_ZhCN_Backup", Manifest.buildId);
            JournalPath = Path.Combine(BackupRoot, "journal.json");
        }

        private void AddTarget(string relative, string hash)
        {
            if (relative.StartsWith("ArchitectureDesign_ZhCN_Backup\\", StringComparison.OrdinalIgnoreCase)
                || Expected.ContainsKey(relative)) throw new InstallerError("资源清单含冲突路径。", 2);
            Expected.Add(relative, hash); Ordered.Add(relative);
        }
        private bool KnownInstalledHash(string relative, string hash)
        {
            if (hash == null) return false;
            string recorded;
            if (JournalFontHashes.TryGetValue(relative, out recorded) && Same(recorded, hash)) return true;
            foreach (Dictionary<string, string> version in KnownVersions.Values)
                if (Same(version[relative], hash)) return true;
            return false;
        }
        private static void ValidateFontFile(string path)
        {
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 12 || input.Length > 128L * 1024 * 1024)
                    throw new InstallerError("本机中文字体文件大小异常：" + Path.GetFileName(path), 3);
                byte[] header = new byte[4];
                if (input.Read(header, 0, 4) != 4 || !((header[0] == 0 && header[1] == 1 && header[2] == 0 && header[3] == 0)
                    || Encoding.ASCII.GetString(header) == "ttcf" || Encoding.ASCII.GetString(header) == "OTTO"))
                    throw new InstallerError("本机中文字体格式无效：" + Path.GetFileName(path), 3);
            }
        }
        private string InspectSystemFont(string path, bool required)
        {
            try
            {
                string fontsRoot = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
                if (String.IsNullOrEmpty(fontsRoot) || !Path.IsPathRooted(fontsRoot) || !File.Exists(path))
                    throw new InstallerError("未找到 Windows 微软雅黑字体。请在 Windows 设置中安装简体中文补充字体后重试。", 3);
                NoReparse(path, fontsRoot);
                ValidateFontFile(path);
                return HashFile(path);
            }
            catch
            {
                if (required) throw;
                // Missing/unreadable system fonts must not block restoring an existing backup.
                return MissingFontHash;
            }
        }
        private void RefreshSystemFonts(bool required)
        {
            foreach (PayloadFile item in Manifest.payloadFiles)
            {
                string source;
                if (!SystemFonts.TryGetValue(item.relative, out source)) continue;
                item.sha256 = InspectSystemFont(source, required);
                Expected[item.relative] = item.sha256;
            }
        }
        private void SetJournalTarget(Journal journal)
        {
            journal.patchedSha256 = Manifest.patchedSha256;
            foreach (JournalEntry item in journal.files) item.installedSha256 = Expected[item.relative];
        }
        internal void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + message;
            try { if (Logger != null) Logger(line); } catch { }
            try
            {
                string path = Path.Combine(Root, "ArchitectureDesign_ZhCN_Installer.log");
                NoReparse(path, Root);
                File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
            }
            catch { /* Logging cannot make a successful transaction fail. */ }
        }
        private static bool ValidHash(string hash) { return hash != null && Regex.IsMatch(hash, "^[a-fA-F0-9]{64}$"); }
        private static bool Same(string a, string b) { return String.Equals(a, b, StringComparison.OrdinalIgnoreCase); }
        private static string Relative(string value)
        {
            if (String.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.IndexOf(':') >= 0)
                throw new InstallerError("清单路径必须是游戏目录内的相对路径。", 2);
            string normalized = value.Replace('/', '\\');
            foreach (string part in normalized.Split('\\'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.EndsWith(".") || part.EndsWith(" ")
                    || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                    || Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(\.|$)", RegexOptions.IgnoreCase))
                    throw new InstallerError("清单包含不安全的文件路径。", 2);
            }
            return normalized;
        }
        private static void NoReparse(string path, string boundary)
        {
            string full = Path.GetFullPath(path), stop = Path.GetFullPath(boundary).TrimEnd('\\', '/');
            if (!Same(full, stop) && !full.StartsWith(stop + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InstallerError("文件路径超出预定目录，已停止操作。", 2);
            string cur = full;
            while (cur != null)
            {
                if ((File.Exists(cur) || Directory.Exists(cur)) && (File.GetAttributes(cur) & FileAttributes.ReparsePoint) != 0)
                    throw new InstallerError("目标路径含符号链接或目录联接，无法安全安装：" + cur, 2);
                if (Same(cur, stop)) break;
                cur = Path.GetDirectoryName(cur);
            }
        }
        private string GamePath(string relative)
        {
            string path = Path.GetFullPath(Path.Combine(Root, Relative(relative)));
            NoReparse(path, Root); return path;
        }
        private string BackupPath(JournalEntry item)
        {
            string relative = Relative("files\\" + item.relative);
            if (!Same(relative, item.backupRelative)) throw new InstallerError("备份记录路径无效。", 2);
            string path = Path.GetFullPath(Path.Combine(BackupRoot, relative)); NoReparse(path, Root); return path;
        }
        private static string HashFile(string path)
        {
            using (FileStream input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(input));
        }
        private static string HashBytes(byte[] bytes) { using (SHA256 sha = SHA256.Create()) return Hex(sha.ComputeHash(bytes)); }
        private static string Hex(byte[] bytes) { return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant(); }
        private static byte[] Unhex(string value)
        {
            if (value == null || (value.Length & 1) != 0 || !Regex.IsMatch(value, "^[a-fA-F0-9]*$"))
                throw new InstallerError("补丁字节数据无效。", 2);
            byte[] result = new byte[value.Length / 2];
            for (int i = 0; i < result.Length; i++) result[i] = Convert.ToByte(value.Substring(i * 2, 2), 16);
            return result;
        }
        private static string CurrentHash(string path)
        {
            if (Directory.Exists(path)) throw new InstallerError("目标文件位置被同名文件夹占用：" + path, 2);
            return File.Exists(path) ? HashFile(path) : null;
        }
        private void EnsureNotRunning()
        {
            string shipping = Path.GetFileNameWithoutExtension(Manifest.exeRelative);
            foreach (string name in new string[] { shipping, "ArchitectureDesign" })
            foreach (Process process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    if (process.Id != Process.GetCurrentProcess().Id)
                        throw new InstallerError("游戏仍在运行。请退出游戏后重试。", 4);
                }
            }
        }
        internal void Execute(string action)
        {
            if (!Directory.Exists(Root)) throw new InstallerError("游戏目录不存在：" + Root, 2);
            NoReparse(Root, Root);
            string mutexName = "Local\\ArchitectureDesignZhCN_" + HashBytes(Encoding.UTF8.GetBytes(Root.ToUpperInvariant())).Substring(0, 24);
            using (Mutex mutex = new Mutex(false, mutexName))
            {
                bool locked;
                try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
                if (!locked) throw new InstallerError("另一个汉化安装器正在操作此游戏，请等待其完成。", 4);
                try
                {
                    Log("游戏目录：" + Root);
                    Log("补丁适用 Steam Build " + Manifest.buildId + (String.IsNullOrEmpty(Manifest.revision) ? "" : " · " + Manifest.revision));
                    if (action == "verify") Verify();
                    else if (action == "restore") { EnsureNotRunning(); Restore(); }
                    else if (action == "install") { EnsureNotRunning(); Install(); }
                    else throw new InstallerError("未知操作。", 2);
                }
                finally { mutex.ReleaseMutex(); }
            }
        }
        private bool IsInstalled()
        {
            foreach (string relative in Ordered)
                if (!Same(CurrentHash(GamePath(relative)), Expected[relative])) return false;
            return true;
        }
        private void Verify()
        {
            Journal journal = ReadJournal();
            foreach (string relative in Ordered)
            {
                string expectedHash = Expected[relative], recorded;
                if (journal != null && Same(journal.patchedSha256, Manifest.patchedSha256)
                    && JournalFontHashes.TryGetValue(relative, out recorded)) expectedHash = recorded;
                if (!Same(CurrentHash(GamePath(relative)), expectedHash))
                    throw new InstallerError("汉化校验未通过，文件缺失或内容不符：" + relative, 3);
                Log("校验通过：" + relative);
            }
            Log("汉化完整，所有文件 SHA-256 校验通过。");
        }
        private Journal ReadJournal()
        {
            JournalFontHashes.Clear();
            NoReparse(JournalPath, Root);
            if (!File.Exists(JournalPath)) return null;
            if (new FileInfo(JournalPath).Length > 4 * 1024 * 1024) throw new InstallerError("备份记录异常。", 2);
            Journal journal = Json.Deserialize<Journal>(File.ReadAllText(JournalPath, Encoding.UTF8));
            if (journal == null || journal.schemaVersion != 1 || journal.buildId != Manifest.buildId
                || !Same(journal.originalSha256, Manifest.originalSha256) || !KnownVersions.ContainsKey(journal.patchedSha256 ?? "")
                || journal.files == null || journal.files.Count != Ordered.Count)
                throw new InstallerError("备份记录与当前补丁不匹配，已停止操作并保留备份。", 3);
            HashSet<string> found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> checkedFonts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (JournalEntry item in journal.files)
            {
                if (item == null) throw new InstallerError("备份记录包含空文件项。", 2);
                item.relative = Relative(item.relative);
                bool variableFont = Same(journal.patchedSha256, Manifest.patchedSha256) && SystemFonts.ContainsKey(item.relative);
                if (!Expected.ContainsKey(item.relative) || !found.Add(item.relative)
                    || (variableFont ? (!ValidHash(item.installedSha256) || Same(item.installedSha256, MissingFontHash))
                        : !Same(item.installedSha256, KnownVersions[journal.patchedSha256][item.relative]))
                    || (item.existedBefore && !ValidHash(item.beforeSha256))
                    || (!item.existedBefore && item.beforeSha256 != null))
                    throw new InstallerError("备份记录包含无效文件项。", 2);
                BackupPath(item);
                if (Same(item.relative, Manifest.exeRelative)
                    && (!item.existedBefore || !Same(item.beforeSha256, Manifest.originalSha256)))
                    throw new InstallerError("原版 EXE 备份信息不正确。", 3);
                if (variableFont) checkedFonts.Add(item.relative, item.installedSha256);
            }
            foreach (KeyValuePair<string, string> font in checkedFonts) JournalFontHashes.Add(font.Key, font.Value);
            return journal;
        }
        private void WriteJournal(Journal journal)
        {
            NoReparse(JournalPath, Root); Directory.CreateDirectory(BackupRoot);
            journal.updatedUtc = DateTime.UtcNow.ToString("o");
            string temp = Path.Combine(BackupRoot, "journal." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(Json.Serialize(journal));
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                if (File.Exists(JournalPath)) File.Replace(temp, JournalPath, null);
                else File.Move(temp, JournalPath);
            }
            finally { TryDelete(temp); }
        }
        private void CheckJournalFiles(Journal journal, bool allowPayloadRepair)
        {
            foreach (JournalEntry item in journal.files)
            {
                string current = CurrentHash(GamePath(item.relative));
                bool before = item.existedBefore ? Same(current, item.beforeSha256) : current == null;
                if (!before && !KnownInstalledHash(item.relative, current))
                {
                    if (allowPayloadRepair && !Same(item.relative, Manifest.exeRelative))
                        Log("检测到汉化资源缺失或损坏，将用已校验资源修复：" + item.relative);
                    else
                        throw new InstallerError("检测到更新或其他修改，无法安全覆盖：" + item.relative
                            + "。请使用适配当前游戏版本的补丁；现有文件和备份已保留。", 3);
                }
                if (item.existedBefore && !Same(CurrentHash(BackupPath(item)), item.beforeSha256))
                    throw new InstallerError("原始备份缺失或校验失败：" + item.relative, 3);
            }
        }
        private List<Snapshot> Capture(string temp)
        {
            List<Snapshot> result = new List<Snapshot>(); int index = 0;
            foreach (string relative in Ordered)
            {
                string source = GamePath(relative), hash = CurrentHash(source);
                Snapshot snapshot = new Snapshot { Relative = relative, Exists = hash != null, Hash = hash,
                    Copy = Path.Combine(temp, "before-" + (index++).ToString("D3") + ".bin") };
                if (snapshot.Exists)
                {
                    File.Copy(source, snapshot.Copy, false);
                    if (!Same(HashFile(snapshot.Copy), hash) || !Same(CurrentHash(source), hash))
                        throw new InstallerError("准备过程中游戏文件发生变化，请关闭更新程序后重试。", 3);
                }
                result.Add(snapshot);
            }
            return result;
        }
        private void CheckSnapshot(Snapshot snapshot)
        {
            string current = CurrentHash(GamePath(snapshot.Relative));
            if (snapshot.Exists ? !Same(current, snapshot.Hash) : current != null)
                throw new InstallerError("操作过程中目标文件发生变化，已停止：" + snapshot.Relative, 3);
        }
        private Dictionary<string, string> Prepare(string temp, string originalPath)
        {
            Log("正在准备全部补丁文件，完成校验后才会替换游戏文件……");
            byte[] bytes = File.ReadAllBytes(originalPath);
            if (!Same(HashBytes(bytes), Manifest.originalSha256))
                throw new InstallerError("游戏版本不受支持。此补丁仅适用于 Steam Build " + Manifest.buildId
                    + "；请获取对应版本的汉化。游戏文件未修改。", 3);
            List<Patch> patches = new List<Patch>(Manifest.patches);
            patches.Sort(delegate(Patch a, Patch c) { return a.offset.CompareTo(c.offset); });
            long last = 0;
            foreach (Patch patch in patches)
            {
                byte[] oldBytes = Unhex(patch.oldHex), newBytes = Unhex(patch.newHex);
                if (oldBytes.Length == 0 || oldBytes.Length != newBytes.Length || patch.offset < last
                    || patch.offset < 0 || patch.offset > bytes.LongLength - oldBytes.Length)
                    throw new InstallerError("补丁范围无效或互相重叠。", 2);
                int offset = checked((int)patch.offset);
                for (int i = 0; i < oldBytes.Length; i++)
                    if (bytes[offset + i] != oldBytes[i]) throw new InstallerError("EXE 原始字节校验失败。", 3);
                Buffer.BlockCopy(newBytes, 0, bytes, offset, newBytes.Length);
                last = patch.offset + oldBytes.Length;
            }
            if (!String.IsNullOrEmpty(Manifest.appendHex))
            {
                byte[] appendix = Unhex(Manifest.appendHex);
                if (appendix.Length > 16 * 1024 * 1024) throw new InstallerError("附加补丁节大小异常。", 2);
                int initialLength = bytes.Length;
                Array.Resize(ref bytes, checked(initialLength + appendix.Length));
                Buffer.BlockCopy(appendix, 0, bytes, initialLength, appendix.Length);
            }
            if (!Same(HashBytes(bytes), Manifest.patchedSha256)) throw new InstallerError("生成的汉化 EXE 校验失败。", 2);
            string exe = Path.Combine(temp, "prepared-exe.bin"); File.WriteAllBytes(exe, bytes); bytes = null;
            if (!Same(HashFile(exe), Manifest.patchedSha256)) throw new InstallerError("暂存 EXE 写入校验失败。", 2);
            Dictionary<string, string> prepared = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            prepared.Add(Manifest.exeRelative, exe);
            Dictionary<string, PayloadFile> expectedPayload = new Dictionary<string, PayloadFile>(StringComparer.OrdinalIgnoreCase);
            foreach (PayloadFile file in Manifest.payloadFiles)
                if (!SystemFonts.ContainsKey(file.relative)) expectedPayload.Add(file.relative, file);
            using (Stream resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            {
                if (resource == null) throw new InstallerError("安装器缺少内置资源包。", 2);
                using (ZipArchive zip = new ZipArchive(resource, ZipArchiveMode.Read, false))
                {
                    long total = 0; int index = 0;
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        if (entry.FullName.EndsWith("/") || entry.FullName.EndsWith("\\"))
                        { Relative(entry.FullName.TrimEnd('/', '\\')); continue; }
                        string relative = Relative(entry.FullName);
                        if (!expectedPayload.ContainsKey(relative) || prepared.ContainsKey(relative))
                            throw new InstallerError("资源包包含未声明或重复文件。", 2);
                        if (entry.Length > 512L * 1024 * 1024 || (total += entry.Length) > 1024L * 1024 * 1024)
                            throw new InstallerError("资源包大小异常。", 2);
                        string output = Path.Combine(temp, "prepared-" + (index++).ToString("D3") + ".bin");
                        using (Stream input = entry.Open())
                        using (FileStream target = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        {
                            byte[] buffer = new byte[1024 * 1024]; long copied = 0; int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                copied += count; if (copied > entry.Length) throw new InstallerError("资源包数据长度异常。", 2);
                                target.Write(buffer, 0, count);
                            }
                            if (copied != entry.Length) throw new InstallerError("资源包数据不完整。", 2);
                        }
                        if (!Same(HashFile(output), expectedPayload[relative].sha256))
                            throw new InstallerError("内置资源校验失败：" + relative, 2);
                        prepared.Add(relative, output);
                    }
                }
            }
            int fontIndex = 0;
            foreach (KeyValuePair<string, string> font in SystemFonts)
            {
                string current = InspectSystemFont(font.Value, true);
                if (!Same(current, Expected[font.Key]))
                    throw new InstallerError("准备过程中 Windows 字体发生变化，请重试。", 3);
                string output = Path.Combine(temp, "prepared-system-font-" + (fontIndex++).ToString("D3") + ".bin");
                File.Copy(font.Value, output, false);
                ValidateFontFile(output);
                if (!Same(HashFile(output), Expected[font.Key]) || !Same(InspectSystemFont(font.Value, true), Expected[font.Key]))
                    throw new InstallerError("本机字体暂存校验失败，游戏文件未修改。", 3);
                prepared.Add(font.Key, output);
                Log("已从本机 Windows 准备中文字体：" + Path.GetFileName(font.Value));
            }
            if (prepared.Count != Ordered.Count) throw new InstallerError("内置资源包缺少文件。", 2);
            Log("全部暂存文件校验通过。"); return prepared;
        }
        private Journal CreateBackups(List<Snapshot> snapshots)
        {
            Journal journal = new Journal { schemaVersion = 1, buildId = Manifest.buildId,
                originalSha256 = Manifest.originalSha256, patchedSha256 = Manifest.patchedSha256,
                status = "prepared", createdUtc = DateTime.UtcNow.ToString("o"), files = new List<JournalEntry>() };
            foreach (Snapshot snapshot in snapshots)
            {
                CheckSnapshot(snapshot);
                JournalEntry entry = new JournalEntry { relative = snapshot.Relative, existedBefore = snapshot.Exists,
                    beforeSha256 = snapshot.Hash, installedSha256 = Expected[snapshot.Relative],
                    backupRelative = "files\\" + snapshot.Relative };
                string backup = BackupPath(entry);
                if (snapshot.Exists)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(backup)); NoReparse(backup, Root);
                    if (File.Exists(backup))
                    {
                        if (!Same(HashFile(backup), snapshot.Hash))
                            throw new InstallerError("已有不同内容的备份，拒绝覆盖：" + backup, 3);
                    }
                    else
                    {
                        string stagedBackup = Path.Combine(Path.GetDirectoryName(backup), ".backup-" + Guid.NewGuid().ToString("N") + ".tmp");
                        try
                        {
                            File.Copy(snapshot.Copy, stagedBackup, false);
                            if (!Same(HashFile(stagedBackup), snapshot.Hash)) throw new InstallerError("原始备份校验失败。", 2);
                            File.Move(stagedBackup, backup); // Never replace a backup, even after an interrupted run.
                        }
                        finally { TryDelete(stagedBackup); }
                    }
                    Log("已保留安装前文件：" + snapshot.Relative);
                }
                else if (File.Exists(backup) || Directory.Exists(backup))
                    throw new InstallerError("发现无对应原文件的旧备份，已停止并保留备份。", 3);
                journal.files.Add(entry);
            }
            WriteJournal(journal); return journal;
        }
        private void CopyAtomic(string source, string relative, string expectedHash)
        {
            string destination = GamePath(relative), parent = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(parent); NoReparse(destination, Root);
            string staged = Path.Combine(parent, ".ArchitectureDesignZhCN-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.Copy(source, staged, false);
                if (!Same(HashFile(staged), expectedHash)) throw new InstallerError("写入暂存文件校验失败。", 2);
                NoReparse(destination, Root);
                if (File.Exists(destination)) File.Replace(staged, destination, null);
                else File.Move(staged, destination);
                if (!Same(CurrentHash(destination), expectedHash)) throw new InstallerError("目标文件写入校验失败。", 2);
            }
            finally { TryDelete(staged); }
        }
        private bool Rollback(List<Mutation> changed)
        {
            Log("操作未完成，正在回滚本次修改……"); bool success = true;
            for (int i = changed.Count - 1; i >= 0; i--)
            {
                Mutation item = changed[i]; Snapshot before = item.Before;
                try
                {
                    string current = CurrentHash(GamePath(before.Relative));
                    if (Same(current, before.Hash)) continue;
                    if (!Same(current, item.AfterHash))
                        throw new InstallerError("回滚时发现额外修改，已保留文件：" + before.Relative, 3);
                    if (before.Exists) CopyAtomic(before.Copy, before.Relative, before.Hash);
                    else File.Delete(GamePath(before.Relative));
                    Log("已回滚：" + before.Relative);
                }
                catch (Exception error) { success = false; Log("回滚提示：" + error.Message); }
            }
            return success;
        }
        private void Install()
        {
            string currentExe = CurrentHash(GamePath(Manifest.exeRelative));
            if (currentExe == null) throw new InstallerError("未找到游戏主程序。请把此安装器放入含 ArchitectureDesign.exe 的游戏根目录。", 3);
            if (!Same(currentExe, Manifest.originalSha256) && !KnownInstalledHash(Manifest.exeRelative, currentExe))
                throw new InstallerError("游戏版本不受支持。此补丁仅适用于 Steam Build " + Manifest.buildId
                    + "。可能游戏已更新或使用了其他补丁，请获取对应版本；游戏文件未修改。", 3);
            RefreshSystemFonts(true);
            Journal journal = ReadJournal();
            Dictionary<string, string> priorFontHashes = new Dictionary<string, string>(JournalFontHashes, StringComparer.OrdinalIgnoreCase);
            if (IsInstalled())
            {
                Log("已安装相同版本汉化，完整校验通过，无需重复安装。");
                if (journal == null) Log("未找到本安装器的备份记录；恢复备份功能不可用。");
                return;
            }
            if (journal != null) CheckJournalFiles(journal, KnownInstalledHash(Manifest.exeRelative, currentExe));
            else if (!Same(currentExe, Manifest.originalSha256))
                throw new InstallerError("已检测到汉化 EXE，但资源不完整且没有备份记录，无法安全修复。", 3);
            string temp = NewTemp(); List<Mutation> changed = new List<Mutation>(); bool began = false;
            List<Snapshot> snapshots = null;
            try
            {
                string originalPath = GamePath(Manifest.exeRelative);
                if (!Same(currentExe, Manifest.originalSha256))
                    originalPath = BackupPath(journal.files.Find(delegate(JournalEntry x) { return Same(x.relative, Manifest.exeRelative); }));
                Dictionary<string, string> prepared = Prepare(temp, originalPath);
                snapshots = Capture(temp);
                Snapshot exeSnapshot = snapshots.Find(delegate(Snapshot x) { return Same(x.Relative, Manifest.exeRelative); });
                if (exeSnapshot == null || !Same(exeSnapshot.Hash, currentExe))
                    throw new InstallerError("准备过程中游戏主程序发生变化，可能正在更新。请等待更新结束后重试。", 3);
                foreach (Snapshot snapshot in snapshots) CheckSnapshot(snapshot);
                EnsureNotRunning();
                if (journal == null) journal = CreateBackups(snapshots);
                else { SetJournalTarget(journal); journal.status = "prepared"; journal.note = null; WriteJournal(journal); }
                began = true;
                foreach (Snapshot snapshot in snapshots)
                {
                    CheckSnapshot(snapshot);
                    if (Same(snapshot.Hash, Expected[snapshot.Relative])) continue;
                    changed.Add(new Mutation { Before = snapshot, AfterHash = Expected[snapshot.Relative] });
                    CopyAtomic(prepared[snapshot.Relative], snapshot.Relative, Expected[snapshot.Relative]);
                    Log("已安装：" + snapshot.Relative);
                }
                Verify(); journal.status = "installed"; journal.note = null; WriteJournal(journal);
                Log("安装完成。正常启动游戏即可使用中文；游戏更新后需使用适配的新补丁。");
            }
            catch (Exception error)
            {
                if (began)
                {
                    bool ok = Rollback(changed); journal.status = ok ? "rolled_back" : "recovery_required";
                    // A Windows font update can change only these per-machine hashes. After a
                    // successful rollback retain the bytes actually restored in the journal,
                    // so /restore still works without requiring the newer system font file.
                    if (ok && snapshots != null)
                    foreach (Snapshot snapshot in snapshots)
                    {
                        if (!SystemFonts.ContainsKey(snapshot.Relative) || !snapshot.Exists) continue;
                        string priorHash;
                        bool recognized = KnownInstalledHash(snapshot.Relative, snapshot.Hash)
                            || (priorFontHashes.TryGetValue(snapshot.Relative, out priorHash) && Same(priorHash, snapshot.Hash));
                        // A previously corrupt font may have been repaired in this failed
                        // transaction; do not bless its rolled-back bytes as a valid install.
                        if (!recognized) continue;
                        JournalEntry font = journal.files.Find(delegate(JournalEntry x) { return Same(x.relative, snapshot.Relative); });
                        if (font != null) font.installedSha256 = snapshot.Hash;
                    }
                    journal.note = error.Message;
                    try { WriteJournal(journal); } catch (Exception e) { Log("备份记录更新失败：" + e.Message); }
                    if (!ok) throw new InstallerError("操作失败，部分文件无法自动回滚。备份已保留，请查看日志。原始错误：" + error.Message, 1);
                }
                throw;
            }
            finally { CleanupTemp(temp); }
        }
        private void Restore()
        {
            Journal journal = ReadJournal();
            if (journal == null) throw new InstallerError("未找到本版本的备份记录，无法恢复。游戏文件未修改。", 3);
            CheckJournalFiles(journal, false);
            string temp = NewTemp(); List<Mutation> changed = new List<Mutation>(); bool began = false;
            try
            {
                List<Snapshot> snapshots = Capture(temp);
                CheckJournalFiles(journal, false); EnsureNotRunning();
                journal.status = "restoring"; WriteJournal(journal); began = true;
                foreach (Snapshot snapshot in snapshots)
                {
                    JournalEntry entry = journal.files.Find(delegate(JournalEntry x) { return Same(x.relative, snapshot.Relative); });
                    CheckSnapshot(snapshot);
                    string wanted = entry.existedBefore ? entry.beforeSha256 : null;
                    if (Same(snapshot.Hash, wanted)) continue;
                    changed.Add(new Mutation { Before = snapshot, AfterHash = wanted });
                    if (entry.existedBefore) CopyAtomic(BackupPath(entry), entry.relative, entry.beforeSha256);
                    else File.Delete(GamePath(entry.relative));
                    if (!Same(CurrentHash(GamePath(entry.relative)), wanted)) throw new InstallerError("恢复后校验失败：" + entry.relative, 2);
                    Log("已恢复安装前状态：" + entry.relative);
                }
                journal.status = "restored"; journal.note = null; WriteJournal(journal);
                Log("恢复完成。所有目标文件已回到安装前状态，备份继续保留。");
            }
            catch (Exception error)
            {
                if (began)
                {
                    bool ok = Rollback(changed); journal.status = ok ? "rolled_back" : "recovery_required";
                    journal.note = error.Message;
                    try { WriteJournal(journal); } catch (Exception e) { Log("备份记录更新失败：" + e.Message); }
                    if (!ok) throw new InstallerError("恢复未完成，部分文件无法自动回滚。备份已保留，请查看日志。原始错误：" + error.Message, 1);
                }
                throw;
            }
            finally { CleanupTemp(temp); }
        }
        private static string NewTemp()
        {
            string path = Path.Combine(Path.GetTempPath(), "ArchitectureDesignZhCN-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path); NoReparse(path, path); return path;
        }
        private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
        private static void CleanupTemp(string temp)
        {
            // All staging files are directly inside this newly created, uniquely named directory.
            // Never recursively delete directories or touch game/backup files here.
            try
            {
                string full = Path.GetFullPath(temp), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\', '/');
                if (!Same(Path.GetDirectoryName(full), parent) || !Regex.IsMatch(Path.GetFileName(full), "^ArchitectureDesignZhCN-[a-f0-9]{32}$")) return;
                NoReparse(full, full);
                foreach (string file in Directory.GetFiles(full, "*", SearchOption.TopDirectoryOnly))
                { NoReparse(file, full); TryDelete(file); }
                Directory.Delete(full, false);
            }
            catch { }
        }
    }

    internal sealed class InstallerForm : Form
    {
        private readonly string Root;
        private readonly TextBox Output = new TextBox();
        private readonly Button InstallButton = new Button();
        private readonly Button RestoreButton = new Button();
        private readonly Button VerifyButton = new Button();
        private readonly ProgressBar Progress = new ProgressBar();
        private bool Busy;
        internal InstallerForm(string root)
        {
            Root = root; Text = "Architecture & Design 简体中文汉化安装器";
            Font = new Font("Microsoft YaHei UI", 9F); StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(790, 550); MinimumSize = new Size(700, 510);
            Label title = new Label { Text = "Architecture & Design · 简体中文补丁", Font = new Font(Font.FontFamily, 16F, FontStyle.Bold),
                Location = new Point(22, 18), Size = new Size(740, 36), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            string version;
            try { Manifest m = new Engine(root, null).Manifest; version = m.buildId + (String.IsNullOrEmpty(m.revision) ? "" : " · " + m.revision); } catch { version = "清单读取失败"; }
            Label help = new Label { Text = "适用 Steam Build " + version + "。放在游戏根目录，退出游戏后点击安装。\r\n支持升级已有汉化，保留首次备份；恢复时回到该备份状态。游戏更新后需重新适配。",
                Location = new Point(24, 64), Size = new Size(740, 52), Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            TextBox path = new TextBox { Text = root, ReadOnly = true, Location = new Point(24, 122), Size = new Size(742, 28),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right };
            InstallButton.Text = "安装 / 更新汉化"; RestoreButton.Text = "恢复备份"; VerifyButton.Text = "校验汉化";
            InstallButton.SetBounds(24, 165, 150, 40); RestoreButton.SetBounds(188, 165, 150, 40); VerifyButton.SetBounds(352, 165, 150, 40);
            Progress.SetBounds(24, 220, 742, 10); Progress.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            Output.Multiline = true; Output.ReadOnly = true; Output.ScrollBars = ScrollBars.Vertical; Output.WordWrap = true;
            Output.SetBounds(24, 244, 742, 280); Output.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            Controls.AddRange(new Control[] { title, help, path, InstallButton, RestoreButton, VerifyButton, Progress, Output });
            InstallButton.Click += delegate { Run("install"); }; RestoreButton.Click += delegate { Run("restore"); };
            VerifyButton.Click += delegate { Run("verify"); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            { if (Busy) { e.Cancel = true; Append("正在处理文件，请等待操作完成后关闭窗口。"); } };
            Append("准备就绪。公开版不附带字体；安装时读取本机 Windows 微软雅黑。无需启动游戏。");
        }
        private void Append(string text)
        {
            if (IsDisposed) return;
            if (InvokeRequired) { BeginInvoke((Action<string>)Append, text); return; }
            Output.AppendText(text + Environment.NewLine);
        }
        private void Run(string action)
        {
            if (Busy) return; Busy = true;
            InstallButton.Enabled = RestoreButton.Enabled = VerifyButton.Enabled = false;
            Progress.Style = ProgressBarStyle.Marquee; Progress.MarqueeAnimationSpeed = 25;
            ThreadPool.QueueUserWorkItem(delegate
            {
                Exception failure = null;
                try { new Engine(Root, Append).Execute(action); }
                catch (Exception error) { failure = error; Append(Program.Friendly(error)); }
                BeginInvoke((Action)delegate
                {
                    Busy = false; InstallButton.Enabled = RestoreButton.Enabled = VerifyButton.Enabled = true;
                    Progress.MarqueeAnimationSpeed = 0; Progress.Style = ProgressBarStyle.Blocks;
                    if (failure == null)
                        MessageBox.Show(this, action == "install" ? "汉化已安装并校验通过。正常启动游戏即可。\r\n游戏更新后，请使用适配新版本的补丁。"
                            : action == "restore" ? "已恢复到安装前状态。备份继续保留。" : "汉化文件完整，校验通过。",
                            "操作完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    else MessageBox.Show(this, Program.Friendly(failure), "未完成操作", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                });
            });
        }
    }
    internal static class Program
    {
        internal static string Friendly(Exception error)
        {
            if (error is UnauthorizedAccessException || error is System.Security.SecurityException)
                return "没有足够的文件写入权限。请关闭游戏及占用文件的程序，再右键此安装器选择“以管理员身份运行”。\r\n" + error.Message;
            if (error is IOException)
                return "文件操作失败。请检查磁盘空间、文件占用和写入权限；如需权限，请右键“以管理员身份运行”。\r\n" + error.Message;
            return error.Message;
        }
        [STAThread]
        private static int Main(string[] args)
        {
            bool silent = false; string action = null;
            string root = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            try
            {
                for (int i = 0; i < args.Length; i++)
                {
                    string arg = args[i].ToLowerInvariant();
                    if (arg == "/silent") silent = true;
                    else if (arg == "/root")
                    {
                        if (++i >= args.Length) throw new InstallerError("/root 后需要提供游戏根目录。", 2);
                        root = Path.GetFullPath(args[i]);
                    }
                    else if (arg == "/install" || arg == "/restore" || arg == "/verify")
                    {
                        if (action != null) throw new InstallerError("每次只能指定一种操作。", 2);
                        action = arg.Substring(1);
                    }
                    else throw new InstallerError("未知参数：" + args[i], 2);
                }
                if (action != null)
                {
                    Engine engine = new Engine(root, delegate(string line) { Console.WriteLine(line); });
                    try { engine.Execute(action); return 0; }
                    catch (Exception error) { engine.Log(Friendly(error)); throw; }
                }
                if (silent) throw new InstallerError("/silent 需配合 /install、/restore 或 /verify 使用。", 2);
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new InstallerForm(root)); return 0;
            }
            catch (Exception error)
            {
                try { Console.Error.WriteLine(Friendly(error)); } catch { }
                // Any explicit CLI invocation is non-interactive. /silent never displays a dialog,
                // including malformed arguments and failures before resources are loaded.
                if (args.Length == 0 && !silent)
                    MessageBox.Show(Friendly(error), "汉化安装器", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                InstallerError known = error as InstallerError;
                if (known != null) return known.ExitCode;
                if (error is UnauthorizedAccessException || error is System.Security.SecurityException) return 5;
                return 1;
            }
        }
    }
}
