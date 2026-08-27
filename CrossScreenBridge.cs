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
using System.Runtime.InteropServices;
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
        const int WmInput = 0x00FF;
        const int WhMouseLl = 14;
        const int WhKeyboardLl = 13;
        const uint ModAlt = 0x0001;

        readonly string deviceName = Environment.MachineName;
        readonly string pairingCode;
        string receiveDir;
        readonly string settingsPath;
        readonly ConcurrentDictionary<string, Peer> peers = new ConcurrentDictionary<string, Peer>();
        readonly ConcurrentDictionary<string, string> pendingDropDirectories = new ConcurrentDictionary<string, string>();
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly ListBox peerList = new ListBox();
        readonly Label status = new Label();
        readonly Label modeLabel = new Label();
        readonly Label emptyLabel = new Label();
        readonly ProgressBar progress = new ProgressBar();
        readonly TextBox manualIp = new TextBox();
        readonly System.Windows.Forms.Timer mouseTimer = new System.Windows.Forms.Timer();
        readonly UdpClient controlSender = new UdpClient();
        readonly object controlSendLock = new object();
        readonly List<string> carriedPaths = new List<string>();
        readonly object selectionCacheLock = new object();
        readonly List<string> selectionCache = new List<string>();
        bool bridgeEnabled;
        bool selectionArmed;
        bool selectionButtonWasDown;
        bool selectionCaptureRunning;
        int selectionGeneration;
        int pendingRawX;
        int pendingRawY;
        System.Threading.Timer rawInputFlushTimer;
        bool highResolutionTimerEnabled;
        bool controllingRemote;
        volatile bool remoteEntryReady;
        bool lastLeftDown;
        bool remoteSawLeftDown;
        bool awaitingConfirmation;
        bool transferStarted;
        string confirmationId;
        IntPtr mouseHook = IntPtr.Zero;
        LowLevelMouseProc mouseHookProc;
        IntPtr keyboardHook = IntPtr.Zero;
        LowLevelKeyboardProc keyboardHookProc;
        Peer controlPeer;
        Point localReturnPoint;
        Point localControlAnchor;

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct NativePoint { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)]
        struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct RawInputDevice { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr Target; }
        [StructLayout(LayoutKind.Sequential)]
        struct RawInputHeader { public uint Type; public uint Size; public IntPtr Device; public IntPtr WParam; }
        delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);
        delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

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
        [DllImport("user32.dll")]
        static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(NativePoint point);
        [DllImport("user32.dll")]
        static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int GetClassName(IntPtr window, StringBuilder className, int maxCount);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);
        [DllImport("user32.dll")]
        static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll")]
        static extern bool ClipCursor(ref NativeRect rect);
        [DllImport("user32.dll")]
        static extern bool ClipCursor(IntPtr rect);
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll", EntryPoint = "SetWindowsHookEx", SetLastError = true)]
        static extern IntPtr SetWindowsKeyboardHookEx(int hookId, LowLevelKeyboardProc callback, IntPtr module, uint threadId);
        [DllImport("user32.dll")]
        static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")]
        static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")]
        static extern IntPtr GetModuleHandle(string moduleName);
        [DllImport("user32.dll")]
        static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
        [DllImport("winmm.dll")]
        static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")]
        static extern uint timeEndPeriod(uint period);

        public MainForm()
        {
            pairingCode = MakeCode(deviceName);
            settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CrossScreenBridge", "receive-folder.txt");
            receiveDir = LoadOrChooseReceiveDirectory();
            Directory.CreateDirectory(receiveDir);

            Text = "跨屏桥 V6.3 · 一比一鼠标实验版";
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
            FormClosing += (s, e) => { CancelCrossScreen("软件已退出"); stop.Cancel(); StopHighRateInput(); controlSender.Close(); UnregisterHotKey(Handle, HotkeyId); };
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
            var rawMouse = new RawInputDevice { UsagePage = 0x01, Usage = 0x02, Flags = 0x00000100, Target = Handle };
            RegisterRawInputDevices(new[] { rawMouse }, 1, (uint)Marshal.SizeOf(typeof(RawInputDevice)));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey && m.WParam.ToInt32() == HotkeyId) ToggleBridge();
            else if (m.Msg == WmInput && controllingRemote) HandleRawMouse(m.LParam);
            base.WndProc(ref m);
        }

        void ToggleBridge()
        {
            if (bridgeEnabled) { CancelCrossScreen("已取消跨屏模式"); return; }
            controlPeer = peerList.SelectedItem as Peer;
            if (controlPeer == null) { Show(); Activate(); SetStatus("请先选择另一台设备。", true); return; }
            bridgeEnabled = true;
            selectionArmed = true;
            selectionGeneration++;
            selectionCaptureRunning = false;
            lock (selectionCacheLock) selectionCache.Clear();
            modeLabel.Text = "选择文件后，将鼠标推过左右屏幕边缘";
            modeLabel.ForeColor = Color.FromArgb(5, 150, 105);
            SetStatus("选择模式已开启：现在可在资源管理器中选择文件，然后移到屏幕边缘；Esc 取消。", false);
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
                if (selectionArmed)
                {
                    var selecting = (GetAsyncKeyState((int)Keys.LButton) & 0x8000) != 0;
                    if (selecting) selectionButtonWasDown = true;
                    else if (selectionButtonWasDown)
                    {
                        selectionButtonWasDown = false;
                        StartSelectionCacheCapture();
                    }
                }
                if (cursor.X <= bounds.Left + 1 || cursor.X >= bounds.Right - 2)
                {
                    var entrySide = cursor.X <= bounds.Left + 1 ? "LEFT" : "RIGHT";
                    var entryY = bounds.Height <= 1 ? 0 : (int)Math.Round((cursor.Y - bounds.Top) * 10000.0 / (bounds.Height - 1));
                    if (selectionArmed)
                    {
                        var nativeDragActive = (GetAsyncKeyState((int)Keys.LButton) & 0x8000) != 0;
                        List<string> selected;
                        lock (selectionCacheLock) selected = selectionCache.ToList();
                        if (selected.Count == 0) selected = GetExplorerSelectedPaths();
                        if (nativeDragActive)
                        {
                            keybd_event((byte)Keys.Escape, 0, 0, UIntPtr.Zero);
                            keybd_event((byte)Keys.Escape, 0, 0x0002, UIntPtr.Zero);
                        }
                        if (selected.Count == 0) selected = GetExplorerSelectionViaClipboard();
                        if (selected.Count == 0)
                        {
                            SetStatus("尚未识别到文件，请在资源管理器中完成选择后再移到屏幕边缘。", true);
                            return;
                        }
                        carriedPaths.Clear(); carriedPaths.AddRange(selected);
                        if (!InstallInputHooks())
                        {
                            CancelCrossScreen("无法安装输入钩子，请重新启动软件后重试。");
                            return;
                        }
                        selectionArmed = false;
                    }
                    controllingRemote = true;
                    remoteEntryReady = false;
                    remoteSawLeftDown = (GetAsyncKeyState((int)Keys.LButton) & 0x8000) != 0;
                    lastLeftDown = remoteSawLeftDown;
                    localReturnPoint = new Point(cursor.X <= bounds.Left + 1 ? bounds.Left + 8 : bounds.Right - 9, cursor.Y);
                    localControlAnchor = localReturnPoint;
                    Interlocked.Exchange(ref pendingRawX, 0);
                    Interlocked.Exchange(ref pendingRawY, 0);
                    StartHighRateInput();
                    SetCursorPos(localControlAnchor.X, localControlAnchor.Y);
                    var lockRect = new NativeRect { Left = localControlAnchor.X, Top = localControlAnchor.Y, Right = localControlAnchor.X + 1, Bottom = localControlAnchor.Y + 1 };
                    ClipCursor(ref lockRect);
                    HideLocalCursor();
                    SendControl(controlPeer, "ENTER|" + carriedPaths.Count + "|" + entrySide + "|" + entryY);
                    remoteEntryReady = true;
                    SetStatus("鼠标已进入 " + controlPeer.Name + "；可点击并进入目录，选好后按 Enter。", false);
                }
                return;
            }

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
            RemoveInputHooks();
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
                SendControl(peer, "DONE");
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
            bridgeEnabled = false; selectionArmed = false; carriedPaths.Clear(); controlPeer = null;
            selectionGeneration++; selectionCaptureRunning = false;
            awaitingConfirmation = false; transferStarted = false; confirmationId = null; remoteSawLeftDown = false;
            RemoveInputHooks();
            modeLabel.Text = "Alt+C 开启跨屏通道";
            modeLabel.ForeColor = Color.FromArgb(100, 116, 139);
            SetStatus(message, false); UpdateDropHint();
        }

        void StartSelectionCacheCapture()
        {
            if (selectionCaptureRunning || !selectionArmed) return;
            selectionCaptureRunning = true;
            var generation = selectionGeneration;
            var thread = new Thread(() =>
            {
                List<string> selected;
                try { selected = GetExplorerSelectedPaths(); }
                catch { selected = new List<string>(); }
                lock (selectionCacheLock)
                {
                    if (generation == selectionGeneration)
                    {
                        selectionCache.Clear();
                        selectionCache.AddRange(selected);
                    }
                }
                try
                {
                    BeginInvoke(new Action(() =>
                    {
                        if (generation != selectionGeneration) return;
                        selectionCaptureRunning = false;
                        if (selectionArmed && selected.Count > 0)
                            SetStatus("已准备 " + selected.Count + " 项；将鼠标推到屏幕边缘即可跨屏。", false);
                    }));
                }
                catch { if (generation == selectionGeneration) selectionCaptureRunning = false; }
            });
            thread.IsBackground = true;
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
        }

        void RestoreLocalCursor()
        {
            if (!controllingRemote) return;
            controllingRemote = false; lastLeftDown = false; remoteSawLeftDown = false;
            remoteEntryReady = false;
            StopHighRateInput();
            ClipCursor(IntPtr.Zero);
            Interlocked.Exchange(ref pendingRawX, 0);
            Interlocked.Exchange(ref pendingRawY, 0);
            ShowLocalCursor();
            SetCursorPos(localReturnPoint.X, localReturnPoint.Y);
        }

        void HandleRawMouse(IntPtr rawInput)
        {
            const uint input = 0x10000003;
            uint size = 0;
            var headerSize = (uint)Marshal.SizeOf(typeof(RawInputHeader));
            if (GetRawInputData(rawInput, input, IntPtr.Zero, ref size, headerSize) == 0xFFFFFFFF || size == 0) return;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(rawInput, input, buffer, ref size, headerSize) == 0xFFFFFFFF) return;
                var body = IntPtr.Add(buffer, (int)headerSize);
                var dx = Marshal.ReadInt32(body, 12);
                var dy = Marshal.ReadInt32(body, 16);
                if (dx != 0) Interlocked.Add(ref pendingRawX, dx);
                if (dy != 0) Interlocked.Add(ref pendingRawY, dy);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }

        bool InstallInputHooks()
        {
            if (mouseHook != IntPtr.Zero && keyboardHook != IntPtr.Zero) return true;
            mouseHookProc = MouseHookCallback;
            mouseHook = SetWindowsHookEx(WhMouseLl, mouseHookProc, GetModuleHandle(null), 0);
            keyboardHookProc = KeyboardHookCallback;
            keyboardHook = SetWindowsKeyboardHookEx(WhKeyboardLl, keyboardHookProc, GetModuleHandle(null), 0);
            if (mouseHook != IntPtr.Zero && keyboardHook != IntPtr.Zero) return true;
            RemoveInputHooks();
            return false;
        }

        void RemoveInputHooks()
        {
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            mouseHook = IntPtr.Zero; mouseHookProc = null;
            keyboardHook = IntPtr.Zero; keyboardHookProc = null;
        }

        IntPtr MouseHookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && bridgeEnabled)
            {
                var message = wParam.ToInt32();
                if (message == 0x0201)
                {
                    if (controllingRemote && controlPeer != null) BeginInvoke(new Action(() => SendControl(controlPeer, "BUTTON|DOWN")));
                    return new IntPtr(1);
                }
                if (message == 0x0202)
                {
                    if (controllingRemote && controlPeer != null) BeginInvoke(new Action(() => SendControl(controlPeer, "BUTTON|UP")));
                    return new IntPtr(1);
                }
                if (controllingRemote && message == 0x0200) return new IntPtr(1);
            }
            return CallNextHookEx(mouseHook, code, wParam, lParam);
        }

        IntPtr KeyboardHookCallback(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && bridgeEnabled && controllingRemote)
            {
                var message = wParam.ToInt32();
                var virtualKey = Marshal.ReadInt32(lParam);
                if (virtualKey == (int)Keys.Enter)
                {
                    var keyDown = message == 0x0100 || message == 0x0104;
                    var keyUp = message == 0x0101 || message == 0x0105;
                    if (keyDown && !awaitingConfirmation)
                        BeginInvoke(new Action(RequestRemoteConfirmation));
                    else if (awaitingConfirmation && (keyDown || keyUp))
                        SendControl(controlPeer, "KEY|ENTER|" + (keyDown ? "DOWN" : "UP"));
                    return new IntPtr(1);
                }
            }
            return CallNextHookEx(keyboardHook, code, wParam, lParam);
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
                var data = Encoding.UTF8.GetBytes("CSC1|" + command);
                lock (controlSendLock)
                {
                    controlSender.Send(data, data.Length, peer.Address, ControlPort);
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
            else if (selectionArmed) label.Text = "选择模式已开启\r\n在资源管理器中选好文件后移到屏幕边缘";
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

        void StartHighRateInput()
        {
            if (rawInputFlushTimer != null) return;
            highResolutionTimerEnabled = timeBeginPeriod(1) == 0;
            rawInputFlushTimer = new System.Threading.Timer(FlushRawMouseMovement, null, 4, 4);
        }

        void StopHighRateInput()
        {
            var timer = rawInputFlushTimer;
            rawInputFlushTimer = null;
            if (timer != null) timer.Dispose();
            if (highResolutionTimerEnabled)
            {
                timeEndPeriod(1);
                highResolutionTimerEnabled = false;
            }
        }

        void FlushRawMouseMovement(object state)
        {
            if (!controllingRemote || !remoteEntryReady || controlPeer == null)
            {
                Interlocked.Exchange(ref pendingRawX, 0);
                Interlocked.Exchange(ref pendingRawY, 0);
                return;
            }
            var dx = Interlocked.Exchange(ref pendingRawX, 0);
            var dy = Interlocked.Exchange(ref pendingRawY, 0);
            if (dx != 0 || dy != 0) SendControl(controlPeer, "MOVE|" + dx + "|" + dy);
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
                            var dx = int.Parse(parts[2]);
                            var dy = int.Parse(parts[3]);
                            InjectAbsoluteMouseMove(dx, dy);
                        }
                        else if (parts.Length >= 5 && parts[1] == "ENTER")
                        {
                            var itemCount = parts[2];
                            var entrySide = parts[3];
                            var normalizedY = Math.Max(0, Math.Min(10000, int.Parse(parts[4])));
                            BeginInvoke(new Action(() =>
                            {
                                var remoteBounds = Screen.PrimaryScreen.Bounds;
                                var targetX = entrySide == "RIGHT" ? remoteBounds.Left + 8 : remoteBounds.Right - 9;
                                var targetY = remoteBounds.Top + (int)Math.Round(normalizedY * (remoteBounds.Height - 1) / 10000.0);
                                SetCursorPos(targetX, targetY);
                                InjectAbsoluteMouseMove(0, 0);
                                SetStatus("另一台设备携带 " + itemCount + " 项文件进入本屏幕。", false);
                            }));
                        }
                        else if (parts.Length >= 4 && parts[1] == "PROMPT")
                        {
                            var requestId = parts[2];
                            var itemCount = parts[3];
                            var sourceAddress = packet.RemoteEndPoint.Address.ToString();
                            BeginInvoke(new Action(() =>
                            {
                                var destination = ResolveRemoteDropDirectory();
                                Show(); WindowState = FormWindowState.Normal; TopMost = true; Activate();
                                var answer = MessageBox.Show(this,
                                    "是否要将 " + itemCount + " 项文件传输到：\r\n\r\n" + destination,
                                    "确认跨屏传输", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                                if (answer == DialogResult.Yes) pendingDropDirectories[sourceAddress] = destination;
                                else { string ignored; pendingDropDirectories.TryRemove(sourceAddress, out ignored); }
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
                        else if (parts.Length >= 4 && parts[1] == "KEY" && parts[2] == "ENTER")
                        {
                            const uint keyUpFlag = 0x0002;
                            keybd_event(0x0D, 0, parts[3] == "UP" ? keyUpFlag : 0, UIntPtr.Zero);
                        }
                        else if (parts.Length >= 2 && parts[1] == "DROP")
                        {
                            BeginInvoke(new Action(() => SetStatus("正在接收跨屏文件…", false)));
                        }
                        else if (parts.Length >= 2 && parts[1] == "DONE")
                        {
                            string ignored;
                            pendingDropDirectories.TryRemove(packet.RemoteEndPoint.Address.ToString(), out ignored);
                        }
                        else if (parts.Length >= 2 && parts[1] == "CANCEL")
                        {
                            string ignored;
                            pendingDropDirectories.TryRemove(packet.RemoteEndPoint.Address.ToString(), out ignored);
                            BeginInvoke(new Action(() => { TopMost = false; SetStatus("对方已取消跨屏操作。", false); }));
                        }
                    }
                    catch { if (!token.IsCancellationRequested) Thread.Sleep(100); }
                }
            }
        }

        void InjectAbsoluteMouseMove(int deltaX, int deltaY)
        {
            NativePoint current;
            if (!GetCursorPos(out current)) return;
            const int virtualLeftMetric = 76;
            const int virtualTopMetric = 77;
            const int virtualWidthMetric = 78;
            const int virtualHeightMetric = 79;
            var left = GetSystemMetrics(virtualLeftMetric);
            var top = GetSystemMetrics(virtualTopMetric);
            var width = Math.Max(2, GetSystemMetrics(virtualWidthMetric));
            var height = Math.Max(2, GetSystemMetrics(virtualHeightMetric));
            var targetX = Math.Max(left, Math.Min(left + width - 1, current.X + deltaX));
            var targetY = Math.Max(top, Math.Min(top + height - 1, current.Y + deltaY));
            var absoluteX = (uint)Math.Round((targetX - left) * 65535.0 / (width - 1));
            var absoluteY = (uint)Math.Round((targetY - top) * 65535.0 / (height - 1));
            const uint move = 0x0001;
            const uint noCoalesce = 0x2000;
            const uint virtualDesktop = 0x4000;
            const uint absolute = 0x8000;
            mouse_event(move | noCoalesce | virtualDesktop | absolute, absoluteX, absoluteY, 0, UIntPtr.Zero);
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
                var sourceAddress = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                string selectedDirectory;
                if (!pendingDropDirectories.TryGetValue(sourceAddress, out selectedDirectory)) selectedDirectory = receiveDir;
                var destination = SafeDestination(relativePath, selectedDirectory);
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

        string SafeDestination(string relativePath, string rootDirectory)
        {
            if (String.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath)) return null;
            var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var candidate = Path.GetFullPath(Path.Combine(root, relativePath));
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
            return candidate;
        }

        string ResolveRemoteDropDirectory()
        {
            NativePoint point;
            if (!GetCursorPos(out point)) return receiveDir;
            var hit = WindowFromPoint(point);
            var rootWindow = GetAncestor(hit, 2);
            object shell = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application"));
                var windows = GetCom(shell, "Windows");
                var count = Convert.ToInt32(GetCom(windows, "Count"));
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        var window = GetCom(windows, "Item", i);
                        if (window == null || Convert.ToInt64(GetCom(window, "HWND")) != rootWindow.ToInt64()) continue;
                        var document = GetCom(window, "Document");
                        var folder = GetCom(document, "Folder");
                        var self = GetCom(folder, "Self");
                        var path = Convert.ToString(GetCom(self, "Path"));
                        if (Directory.Exists(path)) return path;
                    }
                    catch { }
                }
            }
            catch { }

            var className = new StringBuilder(128);
            GetClassName(rootWindow, className, className.Capacity);
            var windowClass = className.ToString();
            if (windowClass == "Progman" || windowClass == "WorkerW")
                return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            return receiveDir;
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
