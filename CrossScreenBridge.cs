using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
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
        const int ControlPort = 45992;
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
        readonly System.Windows.Forms.Timer mouseTimer = new System.Windows.Forms.Timer();
        readonly List<string> carriedPaths = new List<string>();
        bool bridgeEnabled;
        bool controllingRemote;
        bool lastLeftDown;
        bool remoteSawLeftDown;
        bool awaitingConfirmation;
        bool transferStarted;
        string confirmationId;
        Peer controlPeer;
        Point localReturnPoint;
        Point localControlAnchor;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct NativePoint { public int X; public int Y; }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint key);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetCursorPos(out NativePoint point);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetCursorPos(int x, int y);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int ShowCursor(bool show);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int key);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetForegroundWindow();
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);

        public MainForm()
        {
            pairingCode = MakeCode(deviceName);
            settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CrossScreenBridge", "receive-folder.txt");
            receiveDir = LoadOrChooseReceiveDirectory();
            Directory.CreateDirectory(receiveDir);

            Text = "跨屏桥 V5.3 · 跨屏实验版";
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
            mouseTimer.Interval = 16;
            mouseTimer.Tick += (s, e) => PollCrossScreenMouse();
            FormClosing += (s, e) => { CancelCrossScreen("软件已退出"); stop.Cancel(); UnregisterHotKey(Handle, HotkeyId); };
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
            if (bridgeEnabled) { CancelCrossScreen("已取消跨屏模式"); return; }
            controlPeer = peerList.SelectedItem as Peer;
            if (controlPeer == null) { Show(); Activate(); SetStatus("请先选择另一台设备。", true); return; }
            var selected = GetExplorerSelectedPaths();
            if (selected.Count == 0) selected = GetExplorerSelectionViaClipboard();
            if (selected.Count == 0)
            {
                Show(); WindowState = FormWindowState.Normal; Activate();
                SetStatus("请先在资源管理器中选中文件或文件夹，再按 Alt+C。", true);
                return;
            }
            carriedPaths.Clear(); carriedPaths.AddRange(selected);
            bridgeEnabled = true;
            modeLabel.Text = "已携带 " + carriedPaths.Count + " 项 · 将鼠标推过左右屏幕边缘";
            modeLabel.ForeColor = Color.FromArgb(5, 150, 105);
            SetStatus("跨屏已准备：越过屏幕边缘后，在远端单击即可放下并传输；Esc 取消。", false);
            mouseTimer.Start();
            UpdateDropHint();
        }

        void PollCrossScreenMouse()
        {
            if (!bridgeEnabled) return;
            if ((GetAsyncKeyState((int)Keys.Escape) & 0x8000) != 0) { CancelCrossScreen("已取消跨屏模式"); return; }
            NativePoint cursor;
            if (!GetCursorPos(out cursor)) return;
            var bounds = Screen.PrimaryScreen.Bounds;
            if (!controllingRemote)
            {
                if (cursor.X <= bounds.Left + 1 || cursor.X >= bounds.Right - 2)
                {
                    controllingRemote = true;
                    remoteSawLeftDown = (GetAsyncKeyState((int)Keys.LButton) & 0x8000) != 0;
                    lastLeftDown = remoteSawLeftDown;
                    localReturnPoint = new Point(cursor.X <= bounds.Left + 1 ? bounds.Left + 8 : bounds.Right - 9, cursor.Y);
                    localControlAnchor = localReturnPoint;
                    SetCursorPos(localControlAnchor.X, localControlAnchor.Y);
                    HideLocalCursor();
                    SendControl(controlPeer, "ENTER|" + carriedPaths.Count);
                    SetStatus("鼠标已进入 " + controlPeer.Name + "；移动到目标位置后单击放下。", false);
                }
                return;
            }

            var dx = cursor.X - localControlAnchor.X;
            var dy = cursor.Y - localControlAnchor.Y;
            if (dx != 0 || dy != 0)
            {
                SendControl(controlPeer, "MOVE|" + dx + "|" + dy);
                SetCursorPos(localControlAnchor.X, localControlAnchor.Y);
            }
            var leftDown = (GetAsyncKeyState((int)Keys.LButton) & 0x8000) != 0;
            if (awaitingConfirmation)
            {
                if (leftDown != lastLeftDown) SendControl(controlPeer, "BUTTON|" + (leftDown ? "DOWN" : "UP"));
            }
            else
            {
                if (leftDown) remoteSawLeftDown = true;
                if (!leftDown && lastLeftDown && remoteSawLeftDown) RequestRemoteConfirmation();
            }
            lastLeftDown = leftDown;
        }

        void RequestRemoteConfirmation()
        {
            if (!controllingRemote || controlPeer == null || awaitingConfirmation || transferStarted) return;
            awaitingConfirmation = true;
            confirmationId = Guid.NewGuid().ToString("N");
            SendControl(controlPeer, "PROMPT|" + confirmationId + "|" + carriedPaths.Count);
            SetStatus("请在接收方确认是否传输；确认后鼠标会自动返回。", false);
        }

        void CompleteRemoteConfirmation(bool accepted)
        {
            if (!awaitingConfirmation || transferStarted) return;
            awaitingConfirmation = false;
            mouseTimer.Stop();
            RestoreLocalCursor();
            if (!accepted)
            {
                bridgeEnabled = false; carriedPaths.Clear(); controlPeer = null;
                modeLabel.Text = "Alt+C 开启跨屏通道";
                modeLabel.ForeColor = Color.FromArgb(100, 116, 139);
                SetStatus("接收方已取消传输。", false); UpdateDropHint();
                return;
            }
            transferStarted = true;
            SendControl(controlPeer, "DROP|" + carriedPaths.Count);
            SendCarriedFiles();
        }

        async void SendCarriedFiles()
        {
            var peer = controlPeer;
            var paths = carriedPaths.ToArray();
            bridgeEnabled = false;
            modeLabel.Text = "正在向 " + peer.Name + " 传输…";
            try
            {
                foreach (var item in ExpandPaths(paths)) await SendItem(peer, item);
                SetStatus("跨屏传输完成。", false);
            }
            catch (Exception ex) { SetStatus("跨屏传输失败：" + ex.Message, true); }
            finally
            {
                carriedPaths.Clear(); controlPeer = null; transferStarted = false; confirmationId = null;
                modeLabel.Text = "Alt+C 开启跨屏通道";
                modeLabel.ForeColor = Color.FromArgb(100, 116, 139);
                UpdateDropHint();
            }
        }

        void CancelCrossScreen(string message)
        {
            mouseTimer.Stop();
            if (controlPeer != null) SendControl(controlPeer, "CANCEL");
            RestoreLocalCursor();
            bridgeEnabled = false; carriedPaths.Clear(); controlPeer = null;
            awaitingConfirmation = false; transferStarted = false; confirmationId = null; remoteSawLeftDown = false;
            modeLabel.Text = "Alt+C 开启跨屏通道";
            modeLabel.ForeColor = Color.FromArgb(100, 116, 139);
            SetStatus(message, false); UpdateDropHint();
        }

        void RestoreLocalCursor()
        {
            if (!controllingRemote) return;
            controllingRemote = false; lastLeftDown = false; remoteSawLeftDown = false;
            ShowLocalCursor();
            SetCursorPos(localReturnPoint.X, localReturnPoint.Y);
        }

        void HideLocalCursor()
        {
            // ShowCursor uses a process-wide display counter. One call is not
            // guaranteed to hide it when another component incremented it.
            for (var i = 0; i < 16 && ShowCursor(false) >= 0; i++) { }
        }

        void ShowLocalCursor()
        {
            for (var i = 0; i < 16 && ShowCursor(true) < 0; i++) { }
        }

        void SendControl(Peer peer, string command)
        {
            try
            {
                using (var udp = new UdpClient())
                {
                    var data = Encoding.UTF8.GetBytes("CSC1|" + command);
                    udp.Send(data, data.Length, peer.Address, ControlPort);
                }
            }
            catch { }
        }

        List<string> GetExplorerSelectedPaths()
        {
            var result = new List<string>();
            object shell = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                var windows = GetCom(shell, "Windows");
                var count = Convert.ToInt32(GetCom(windows, "Count"));
                var foreground = GetForegroundWindow().ToInt64();
                for (var i = 0; i < count; i++)
                {
                    var window = GetCom(windows, "Item", i);
                    if (window == null || Convert.ToInt64(GetCom(window, "HWND")) != foreground) continue;
                    var document = GetCom(window, "Document");
                    var selected = GetCom(document, "SelectedItems");
                    var selectedCount = Convert.ToInt32(GetCom(selected, "Count"));
                    for (var j = 0; j < selectedCount; j++)
                    {
                        var item = GetCom(selected, "Item", j);
                        var path = Convert.ToString(GetCom(item, "Path"));
                        if (File.Exists(path) || Directory.Exists(path)) result.Add(path);
                    }
                    break;
                }
            }
            catch { }
            return result;
        }

        object GetCom(object target, string member, params object[] args)
        {
            return target.GetType().InvokeMember(member, BindingFlags.GetProperty | BindingFlags.InvokeMethod, null, target, args);
        }

        List<string> GetExplorerSelectionViaClipboard()
        {
            var result = new List<string>();
            IDataObject previous = null;
            try
            {
                previous = Clipboard.GetDataObject();
                SendKeys.SendWait("^c");
                Application.DoEvents();
                Thread.Sleep(120);
                if (Clipboard.ContainsFileDropList())
                {
                    foreach (string path in Clipboard.GetFileDropList())
                        if (File.Exists(path) || Directory.Exists(path)) result.Add(path);
                }
            }
            catch { }
            finally
            {
                if (previous != null)
                {
                    try { Clipboard.SetDataObject(previous, true); } catch { }
                }
            }
            return result;
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
            Task.Run(() => ControlReceiver(stop.Token));
            var cleanup = new System.Windows.Forms.Timer { Interval = 3000 };
            cleanup.Tick += (s, e) => RefreshPeerList();
            cleanup.Start();
        }

        async Task ControlReceiver(CancellationToken token)
        {
            using (var udp = new UdpClient(ControlPort))
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var packet = await udp.ReceiveAsync();
                        var text = Encoding.UTF8.GetString(packet.Buffer);
                        if (!text.StartsWith("CSC1|")) continue;
                        var parts = text.Split('|');
                        if (parts.Length >= 4 && parts[1] == "MOVE")
                        {
                            NativePoint point;
                            if (GetCursorPos(out point)) SetCursorPos(point.X + int.Parse(parts[2]), point.Y + int.Parse(parts[3]));
                        }
                        else if (parts.Length >= 3 && parts[1] == "ENTER")
                        {
                            BeginInvoke(new Action(() => { Show(); WindowState = FormWindowState.Normal; TopMost = true; SetStatus("另一台设备携带 " + parts[2] + " 项文件进入本屏幕。", false); }));
                        }
                        else if (parts.Length >= 4 && parts[1] == "PROMPT")
                        {
                            var requestId = parts[2];
                            var itemCount = parts[3];
                            var sourceAddress = packet.RemoteEndPoint.Address.ToString();
                            BeginInvoke(new Action(() =>
                            {
                                Show(); WindowState = FormWindowState.Normal; TopMost = true; Activate();
                                var answer = MessageBox.Show(this,
                                    "是否要将 " + itemCount + " 项文件传输到：\r\n\r\n" + receiveDir,
                                    "确认跨屏传输", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                                var sourcePeer = new Peer { Address = sourceAddress, Port = TransferPort, Name = sourceAddress };
                                SendControl(sourcePeer, "CONFIRM|" + requestId + "|" + (answer == DialogResult.Yes ? "YES" : "NO"));
                                TopMost = false;
                            }));
                        }
                        else if (parts.Length >= 4 && parts[1] == "CONFIRM")
                        {
                            var requestId = parts[2];
                            var accepted = parts[3] == "YES";
                            BeginInvoke(new Action(() =>
                            {
                                if (requestId == confirmationId) CompleteRemoteConfirmation(accepted);
                            }));
                        }
                        else if (parts.Length >= 3 && parts[1] == "BUTTON")
                        {
                            const uint leftDownFlag = 0x0002;
                            const uint leftUpFlag = 0x0004;
                            mouse_event(parts[2] == "DOWN" ? leftDownFlag : leftUpFlag, 0, 0, 0, UIntPtr.Zero);
                        }
                        else if (parts.Length >= 2 && parts[1] == "DROP")
                        {
                            BeginInvoke(new Action(() => SetStatus("正在接收跨屏文件…", false)));
                        }
                        else if (parts.Length >= 2 && parts[1] == "CANCEL")
                        {
                            BeginInvoke(new Action(() => { TopMost = false; SetStatus("对方已取消跨屏操作。", false); }));
                        }
                    }
                    catch { if (!token.IsCancellationRequested) Thread.Sleep(100); }
                }
            }
        }

        async Task DiscoverySender(CancellationToken token)
        {
            using (var udp = new UdpClient())
            {
                udp.EnableBroadcast = true;
                while (!token.IsCancellationRequested)
                {
                    var payload = Encoding.UTF8.GetBytes("CSB5|" + deviceName + "|" + TransferPort + "|" + pairingCode);
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
                        if (parts.Length == 4 && parts[0] == "CSB5" && parts[1] != deviceName)
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
