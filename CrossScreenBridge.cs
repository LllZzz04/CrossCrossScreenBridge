using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CrossScreenBridge
{
    public sealed class Peer
    {
        public string Name;
        public string Address;
        public int Port;
        public string Code;
        public DateTime Seen;
        public override string ToString() { return Name + "  ·  " + Address; }
    }

    public sealed class TransferItem
    {
        public string SourcePath;
        public string RelativePath;
        public bool IsDirectory;
    }

    public sealed class MainForm : Form
    {
        const int DiscoveryPort = 45990;
        const int TransferPort = 45991;
        const int HotkeyId = 0xC501;
        const int WmHotkey = 0x0312;
        const uint ModAlt = 0x0001;

        readonly string deviceName = Environment.MachineName;
        readonly string pairingCode;
        string receiveDir;
        readonly string settingsPath;
        readonly ConcurrentDictionary<string, Peer> peers = new ConcurrentDictionary<string, Peer>();
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly ListBox peerList = new ListBox();
        readonly Label status = new Label();
        readonly Label modeLabel = new Label();
        readonly Label emptyLabel = new Label();
        readonly ProgressBar progress = new ProgressBar();
        readonly TextBox manualIp = new TextBox();
        bool bridgeEnabled;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        public MainForm()
        {
            pairingCode = MakeCode(deviceName);
            settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CrossScreenBridge", "receive-folder.txt");
            receiveDir = LoadOrChooseReceiveDirectory();
            Directory.CreateDirectory(receiveDir);

            Text = "跨屏桥";
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Color.FromArgb(245, 247, 250);
            ForeColor = Color.FromArgb(30, 41, 59);
            Width = 620;
            Height = 500;
            MinimumSize = new Size(520, 420);
            StartPosition = FormStartPosition.CenterScreen;
            AllowDrop = true;

            BuildUi();
            DragEnter += OnDragEnter;
            DragDrop += OnDragDrop;
            FormClosing += (s, e) => { stop.Cancel(); UnregisterHotKey(Handle, HotkeyId); };
            Shown += (s, e) => StartNetworking();
        }

        void BuildUi()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 100, Padding = new Padding(22, 16, 22, 8), BackColor = Color.White };
            var title = new Label { Text = "跨屏桥", Font = new Font(Font.FontFamily, 18F, FontStyle.Bold), AutoSize = true, Location = new Point(20, 14) };
            modeLabel.Text = "Alt+C 开启跨屏通道";
            modeLabel.AutoSize = true;
            modeLabel.ForeColor = Color.FromArgb(100, 116, 139);
            modeLabel.Location = new Point(22, 55);
            var identity = new Label { Text = deviceName + "    配对码 " + pairingCode, AutoSize = true, Anchor = AnchorStyles.Top | AnchorStyles.Right, Location = new Point(340, 26), ForeColor = Color.FromArgb(71, 85, 105) };
            var changeFolder = new Button { Text = "更改保存位置", Width = 112, Height = 28, Location = new Point(438, 54), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            changeFolder.Click += (s, e) => ChooseReceiveDirectory(false);
            header.Controls.Add(title); header.Controls.Add(modeLabel); header.Controls.Add(identity); header.Controls.Add(changeFolder);

            var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(22, 18, 22, 12) };
            var listTitle = new Label { Text = "局域网中的设备", Font = new Font(Font, FontStyle.Bold), Dock = DockStyle.Top, Height = 28 };
            var manualPanel = new Panel { Dock = DockStyle.Top, Height = 42 };
            manualIp.Width = 190;
            manualIp.Location = new Point(0, 6);
            manualIp.Text = "192.168.0.100";
            var manualButton = new Button { Text = "手动连接", Width = 100, Height = 28, Location = new Point(200, 4) };
            manualButton.Click += (s, e) => AddManualPeer();
            manualIp.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddManualPeer(); e.SuppressKeyPress = true; } };
            manualPanel.Controls.Add(manualIp);
            manualPanel.Controls.Add(manualButton);
            peerList.Dock = DockStyle.Top;
            peerList.Height = 150;
            peerList.BorderStyle = BorderStyle.FixedSingle;
            peerList.DisplayMember = "Name";
            peerList.SelectedIndexChanged += (s, e) => UpdateDropHint();
            emptyLabel.Text = "正在搜索另一台电脑…\r\n请在两台电脑上同时运行跨屏桥";
            emptyLabel.TextAlign = ContentAlignment.MiddleCenter;
            emptyLabel.ForeColor = Color.FromArgb(100, 116, 139);
            emptyLabel.Dock = DockStyle.Top;
            emptyLabel.Height = 70;

            var drop = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(18), AllowDrop = true };
            var dropText = new Label { Name = "DropText", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Text = "选择设备并按 Alt+C\r\n然后把文件拖到这里", ForeColor = Color.FromArgb(71, 85, 105), AllowDrop = true };
            drop.DragEnter += OnDragEnter; drop.DragDrop += OnDragDrop;
            dropText.DragEnter += OnDragEnter; dropText.DragDrop += OnDragDrop;
            drop.Controls.Add(dropText);

            progress.Dock = DockStyle.Bottom;
            progress.Height = 8;
            progress.Visible = false;
            status.Text = "接收目录：" + receiveDir;
            status.Dock = DockStyle.Bottom;
            status.Height = 42;
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.ForeColor = Color.FromArgb(100, 116, 139);

            body.Controls.Add(drop); body.Controls.Add(emptyLabel); body.Controls.Add(peerList); body.Controls.Add(manualPanel); body.Controls.Add(listTitle); body.Controls.Add(progress); body.Controls.Add(status);
            Controls.Add(body); Controls.Add(header);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!RegisterHotKey(Handle, HotkeyId, ModAlt, (uint)Keys.C))
                SetStatus("Alt+C 已被其他软件占用，请关闭冲突软件后重启。", true);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId) ToggleBridge();
            base.WndProc(ref m);
        }

        void ToggleBridge()
        {
            bridgeEnabled = !bridgeEnabled;
            modeLabel.Text = bridgeEnabled ? "跨屏通道已开启 · 再按 Alt+C 关闭" : "Alt+C 开启跨屏通道";
            modeLabel.ForeColor = bridgeEnabled ? Color.FromArgb(5, 150, 105) : Color.FromArgb(100, 116, 139);
            TopMost = bridgeEnabled;
            if (bridgeEnabled) { Show(); WindowState = FormWindowState.Normal; Activate(); }
            UpdateDropHint();
        }

        void UpdateDropHint()
        {
            var label = Controls.Find("DropText", true).FirstOrDefault() as Label;
            var peer = peerList.SelectedItem as Peer;
            if (label == null) return;
            if (!bridgeEnabled) label.Text = "选择设备并按 Alt+C\r\n然后把文件拖到这里";
            else if (peer == null) label.Text = "请先选择接收设备";
            else label.Text = "松手发送到 " + peer.Name + "\r\n支持文件和文件夹";
        }

        async void AddManualPeer()
        {
            IPAddress address;
            var value = manualIp.Text.Trim();
            if (!IPAddress.TryParse(value, out address) || address.AddressFamily != AddressFamily.InterNetwork)
            {
                SetStatus("请输入有效的 IPv4 地址，例如 192.168.0.100。", true);
                return;
            }
            SetStatus("正在测试 " + value + ":" + TransferPort + "…", false);
            try
            {
                using (var client = new TcpClient())
                {
                    var connect = client.ConnectAsync(address, TransferPort);
                    if (await Task.WhenAny(connect, Task.Delay(2500)) != connect) throw new TimeoutException("连接超时");
                    await connect;
                }
                var peer = new Peer { Name = "设备 " + value, Address = value, Port = TransferPort, Code = "MANUAL", Seen = DateTime.UtcNow.AddYears(1) };
                peers.AddOrUpdate(value, peer, (k, old) => peer);
                RefreshPeerList();
                SetStatus("连接成功：" + value + "。按 Alt+C 后即可拖放文件。", false);
            }
            catch (Exception ex) { SetStatus("无法连接 " + value + "：" + ex.Message, true); }
        }

        void OnDragEnter(object sender, DragEventArgs e)
        {
            if (bridgeEnabled && peerList.SelectedItem != null && e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy;
            else e.Effect = DragDropEffects.None;
        }

        async void OnDragDrop(object sender, DragEventArgs e)
        {
            var peer = peerList.SelectedItem as Peer;
            if (!bridgeEnabled || peer == null) { SetStatus("请先选择设备并按 Alt+C 开启通道。", true); return; }
            var paths = (string[])e.Data.GetData(DataFormats.FileDrop);
            foreach (var item in ExpandPaths(paths))
            {
                try { await SendItem(peer, item); }
                catch (Exception ex) { SetStatus("发送失败：" + ex.Message, true); }
            }
        }

        IEnumerable<TransferItem> ExpandPaths(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                if (File.Exists(path))
                {
                    yield return new TransferItem { SourcePath = path, RelativePath = Path.GetFileName(path), IsDirectory = false };
                }
                else if (Directory.Exists(path))
                {
                    var root = new DirectoryInfo(path);
                    yield return new TransferItem { SourcePath = path, RelativePath = root.Name, IsDirectory = true };
                    foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                    {
                        var relative = root.Name + Path.DirectorySeparatorChar + directory.Substring(path.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar);
                        yield return new TransferItem { SourcePath = directory, RelativePath = relative, IsDirectory = true };
                    }
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        var relative = root.Name + Path.DirectorySeparatorChar + file.Substring(path.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar);
                        yield return new TransferItem { SourcePath = file, RelativePath = relative, IsDirectory = false };
                    }
                }
            }
        }

        async Task SendItem(Peer peer, TransferItem item)
        {
            var info = item.IsDirectory ? null : new FileInfo(item.SourcePath);
            var length = item.IsDirectory ? 0L : info.Length;
            SetProgress(true, 0);
            SetStatus("正在发送 " + item.RelativePath + " 到 " + peer.Name + "…", false);
            using (var client = new TcpClient())
            {
                await client.ConnectAsync(IPAddress.Parse(peer.Address), peer.Port);
                client.ReceiveTimeout = 15000;
                using (var stream = client.GetStream())
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(Encoding.ASCII.GetBytes("CSBRDG04"));
                    writer.Write(item.IsDirectory);
                    writer.Write(item.RelativePath);
                    writer.Write(length);
                    writer.Flush();
                    if (!item.IsDirectory)
                    {
                        using (var input = new FileStream(item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true))
                        {
                            var buffer = new byte[1024 * 128];
                            long sent = 0; int read;
                            while ((read = await input.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await stream.WriteAsync(buffer, 0, read);
                                sent += read;
                                SetProgress(true, length == 0 ? 100 : (int)(sent * 100 / length));
                            }
                        }
                    }
                    await stream.FlushAsync();
                    using (var confirmation = new BinaryReader(stream, Encoding.UTF8, true))
                    {
                        var reply = confirmation.ReadString();
                        if (reply != "OK") throw new IOException("接收端未确认保存成功" + (String.IsNullOrWhiteSpace(reply) ? "" : "：" + reply));
                    }
                }
            }
            SetProgress(false, 100);
            SetStatus("已发送：" + item.RelativePath, false);
        }

        void StartNetworking()
        {
            Task.Run(() => DiscoverySender(stop.Token));
            Task.Run(() => DiscoveryReceiver(stop.Token));
            Task.Run(() => TransferServer(stop.Token));
            var cleanup = new System.Windows.Forms.Timer { Interval = 3000 };
            cleanup.Tick += (s, e) => RefreshPeerList();
            cleanup.Start();
        }

        async Task DiscoverySender(CancellationToken token)
        {
            using (var udp = new UdpClient())
            {
                udp.EnableBroadcast = true;
                while (!token.IsCancellationRequested)
                {
                    var payload = Encoding.UTF8.GetBytes("CSB1|" + deviceName + "|" + TransferPort + "|" + pairingCode);
                    foreach (var broadcast in GetBroadcastAddresses())
                    {
                        try { await udp.SendAsync(payload, payload.Length, new IPEndPoint(broadcast, DiscoveryPort)); } catch { }
                    }
                    await Task.Delay(1500);
                }
            }
        }

        IEnumerable<IPAddress> GetBroadcastAddresses()
        {
            var found = new HashSet<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var item in nic.GetIPProperties().UnicastAddresses)
                {
                    if (item.Address.AddressFamily != AddressFamily.InterNetwork || item.IPv4Mask == null) continue;
                    var ip = item.Address.GetAddressBytes();
                    var mask = item.IPv4Mask.GetAddressBytes();
                    var bytes = new byte[4];
                    for (var i = 0; i < 4; i++) bytes[i] = (byte)(ip[i] | (byte)~mask[i]);
                    var result = new IPAddress(bytes);
                    if (found.Add(result.ToString())) yield return result;
                }
            }
            if (found.Add(IPAddress.Broadcast.ToString())) yield return IPAddress.Broadcast;
        }

        async Task DiscoveryReceiver(CancellationToken token)
        {
            using (var udp = new UdpClient())
            {
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udp.ReceiveAsync();
                        var text = Encoding.UTF8.GetString(result.Buffer);
                        var parts = text.Split('|');
                        if (parts.Length == 4 && parts[0] == "CSB1" && parts[1] != deviceName)
                        {
                            var peer = new Peer { Name = parts[1], Address = result.RemoteEndPoint.Address.ToString(), Port = int.Parse(parts[2]), Code = parts[3], Seen = DateTime.UtcNow };
                            peers.AddOrUpdate(peer.Address, peer, (k, old) => peer);
                            BeginInvoke(new Action(RefreshPeerList));
                        }
                    }
                    catch
                    {
                        if (!token.IsCancellationRequested) Thread.Sleep(500);
                    }
                }
            }
        }

        async Task TransferServer(CancellationToken token)
        {
            var listener = new TcpListener(IPAddress.Any, TransferPort);
            listener.Start();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync();
                    var receiveTask = Task.Run(() => ReceiveFile(client));
                }
            }
            finally { listener.Stop(); }
        }

        async Task ReceiveFile(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            using (var reader = new BinaryReader(stream, Encoding.UTF8, true))
            {
                var magic = Encoding.ASCII.GetString(reader.ReadBytes(8));
                if (magic != "CSBRDG04") return;
                var isDirectory = reader.ReadBoolean();
                var relativePath = reader.ReadString();
                var length = reader.ReadInt64();
                if (length < 0 || length > 100L * 1024 * 1024 * 1024) return;
                var destination = SafeDestination(relativePath);
                if (destination == null) return;
                if (isDirectory)
                {
                    Directory.CreateDirectory(destination);
                    SetStatus("已创建文件夹：" + relativePath, false);
                    using (var response = new BinaryWriter(stream, Encoding.UTF8, true)) { response.Write("OK"); response.Flush(); }
                    return;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                if (File.Exists(destination)) destination = UniquePath(destination);
                SetProgress(true, 0);
                SetStatus("正在接收 " + relativePath + "…", false);
                using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 128, true))
                {
                    var buffer = new byte[1024 * 128];
                    long received = 0;
                    while (received < length)
                    {
                        int read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, length - received));
                        if (read == 0) throw new EndOfStreamException("连接提前中断");
                        await output.WriteAsync(buffer, 0, read);
                        received += read;
                        SetProgress(true, length == 0 ? 100 : (int)(received * 100 / length));
                    }
                }
                SetProgress(false, 100);
                SetStatus("已接收：" + destination, false);
                using (var response = new BinaryWriter(stream, Encoding.UTF8, true)) { response.Write("OK"); response.Flush(); }
            }
        }

        string SafeDestination(string relativePath)
        {
            if (String.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return null;
            var root = Path.GetFullPath(receiveDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return candidate;
        }

        string LoadOrChooseReceiveDirectory()
        {
            try
            {
                if (File.Exists(settingsPath))
                {
                    var saved = File.ReadAllText(settingsPath, Encoding.UTF8).Trim();
                    if (!String.IsNullOrWhiteSpace(saved)) return saved;
                }
            }
            catch { }
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "跨屏桥接收");
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "首次运行：请选择接收文件的保存位置";
                dialog.SelectedPath = fallback;
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog() == DialogResult.OK) fallback = dialog.SelectedPath;
            }
            SaveReceiveDirectory(fallback);
            return fallback;
        }

        void ChooseReceiveDirectory(bool firstRun)
        {
            using (var dialog = new FolderBrowserDialog())
            {
                dialog.Description = "选择接收文件的保存位置";
                dialog.SelectedPath = receiveDir;
                dialog.ShowNewFolderButton = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                receiveDir = dialog.SelectedPath;
                Directory.CreateDirectory(receiveDir);
                SaveReceiveDirectory(receiveDir);
                SetStatus("接收目录已更改为：" + receiveDir, false);
            }
        }

        void SaveReceiveDirectory(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath));
                File.WriteAllText(settingsPath, path, Encoding.UTF8);
            }
            catch { }
        }

        string UniquePath(string path)
        {
            if (!File.Exists(path)) return path;
            var dir = Path.GetDirectoryName(path); var stem = Path.GetFileNameWithoutExtension(path); var ext = Path.GetExtension(path);
            for (int i = 1; ; i++) { var candidate = Path.Combine(dir, stem + " (" + i + ")" + ext); if (!File.Exists(candidate)) return candidate; }
        }

        void RefreshPeerList()
        {
            if (InvokeRequired) { BeginInvoke(new Action(RefreshPeerList)); return; }
            var selectedPeer = peerList.SelectedItem as Peer;
            var selectedAddress = selectedPeer == null ? null : selectedPeer.Address;
            var live = peers.Values.Where(p => p.Code == "MANUAL" || DateTime.UtcNow - p.Seen < TimeSpan.FromSeconds(8)).OrderBy(p => p.Name).ToList();
            peerList.BeginUpdate(); peerList.Items.Clear();
            foreach (var peer in live) peerList.Items.Add(peer);
            peerList.EndUpdate();
            if (live.Count > 0)
            {
                var index = live.FindIndex(p => p.Address == selectedAddress);
                peerList.SelectedIndex = index >= 0 ? index : 0;
            }
            emptyLabel.Visible = live.Count == 0;
            UpdateDropHint();
        }

        void SetStatus(string text, bool error)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetStatus(text, error))); return; }
            status.Text = text; status.ForeColor = error ? Color.FromArgb(220, 38, 38) : Color.FromArgb(100, 116, 139);
        }

        void SetProgress(bool visible, int value)
        {
            if (InvokeRequired) { BeginInvoke(new Action(() => SetProgress(visible, value))); return; }
            progress.Visible = visible; progress.Value = Math.Max(0, Math.Min(100, value));
        }

        static string MakeCode(string input)
        {
            using (var sha = SHA256.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
                return ((bytes[0] << 16 | bytes[1] << 8 | bytes[2]) % 1000000).ToString("000000");
            }
        }
    }

    public static class Program
    {
        [STAThread]
        public static void Run()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
