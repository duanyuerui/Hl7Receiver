using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Hl7Receiver;

public sealed class MainForm : Form
{
    private static readonly (string Label, string Name)[] EncodingOptions =
    {
        ("GB18030（中文，推荐）", "GB18030"),
        ("UTF-8", "UTF-8"),
        ("ISO-8859-1（HL7 标准）", "ISO-8859-1"),
        ("ASCII", "ASCII")
    };

    private const string SampleOru =
        "MSH|^~\\&|||||20120508094822||ORU^R01|TEST001|P|2.3.1||||0||ASCII|||\r" +
        "PID|1|1001||| Mike ||19851001095133|M|||||||||||||||||||||||\r" +
        "OBR|1|12345678|10|^|Y|20120405193926|20120405193914|20120405193914||||||20120405193914|serum|||||||||||||||||||||||||||||||||\r" +
        "OBX|1|NM|2|TBil|100| umol/L |-|N|||F||100|20120405194245|||0|\r" +
        "OBX|2|NM|5|ALT|98.2| umol/L |-|N|||F||98.2|20120405194403|||0|\r" +
        "OBX|3|NM|6|AST|26.4| umol/L |-|N|||F||26.4|||||\r";

    private readonly ComboBox _cboMode = new();
    private readonly TextBox _txtIp = new();
    private readonly NumericUpDown _numPort = new();
    private readonly ComboBox _cboEncoding = new();
    private readonly ComboBox _cboAckCode = new();
    private readonly CheckBox _chkAutoAck = new();
    private readonly CheckBox _chkAutoSave = new();
    private readonly CheckBox _chkLenient = new();
    private readonly CheckBox _chkChunkLog = new();
    private readonly CheckBox _chkAutoSelect = new();
    private readonly CheckBox _chkReconnect = new();
    private readonly NumericUpDown _numIdleMs = new();

    private readonly Button _btnStart = new();
    private readonly Button _btnStop = new();
    private readonly Button _btnSelfTest = new();
    private readonly Label _lblRunState = new();
    private readonly Label _lblLocalIp = new();

    private readonly ListView _lvMessages = new();
    private readonly RichTextBox _rtbRaw = new();
    private readonly RichTextBox _rtbHex = new();
    private readonly RichTextBox _rtbLog = new();
    private readonly TreeView _tvFields = new();
    private readonly DataGridView _gridResults = new();

    private readonly StatusStrip _statusStrip = new();
    private readonly ToolStripStatusLabel _lblStatusConn = new();
    private readonly ToolStripStatusLabel _lblStatusMsg = new();
    private readonly ToolStripStatusLabel _lblStatusBytes = new();
    private readonly ToolStripStatusLabel _lblStatusBuf = new();
    private readonly ToolStripStatusLabel _lblStatusFolder = new() { Spring = true, TextAlign = ContentAlignment.MiddleRight };

    private readonly System.Windows.Forms.Timer _uiTimer = new();
    private readonly string _messageRoot = Path.Combine(AppContext.BaseDirectory, "ReceivedMessages");
    private readonly string _logFolder = Path.Combine(AppContext.BaseDirectory, "Logs");
    private readonly object _fileLogLock = new();
    private readonly string _settingsPath = Path.Combine(AppContext.BaseDirectory, "settings.json");

    private Hl7ReceiverService? _service;
    private Encoding _encoding = Encoding.Latin1; // 构造完成后由 LoadSettings 按编码下拉框设置
    private bool _autoAck = true;
    private bool _autoSave = true;
    private string _ackCode = "AA";
    private bool _running;
    private long _messageCount;

    public MainForm()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        Text = "HL7 网口接收测试工具（MLLP / TCP）";
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(1180, 720);
        Size = new Size(1420, 880);
        Font = new Font("Microsoft YaHei UI", 9F);

        BuildUi();
        LoadSettings();
        UpdateLocalIps();
        AppendLog("HL7 网口接收测试工具已就绪。");
        AppendLog("MLLP 帧格式: 0x0B(起始) + HL7 报文 + 0x1C 0x0D(结束)。");
        AppendLog("仪器主动把结果推给 LIS → 选“服务端监听”；仪器作为服务端 → 选“客户端连接”。");
        AppendLog("TCP 一次读取不保证是一条完整报文，本工具会累积到 0x1C 0x0D 才判定完整。");

        _uiTimer.Interval = 400;
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();

        FormClosing += (_, _) =>
        {
            SaveSettings();
            StopReceiver();
        };
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            Padding = new Padding(0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 2,
            Padding = new Padding(8, 8, 8, 4)
        };

        var row1 = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };

        _cboMode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboMode.Width = 230;
        _cboMode.Items.AddRange(new object[]
        {
            "服务端监听（仪器来连我）",
            "客户端连接（我连仪器）"
        });

        _txtIp.Width = 130;
        _txtIp.Text = "192.168.26.111";

        _numPort.Minimum = 1;
        _numPort.Maximum = 65535;
        _numPort.Value = 5150;
        _numPort.Width = 80;

        _cboEncoding.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboEncoding.Width = 170;
        foreach (var option in EncodingOptions) _cboEncoding.Items.Add(option.Label);

        AddRowControl(row1, "模式", _cboMode);
        AddRowControl(row1, "IP", _txtIp);
        AddRowControl(row1, "端口", _numPort);
        AddRowControl(row1, "编码", _cboEncoding);

        StyleButton(_btnStart, "开始接收", 100);
        _btnStart.BackColor = Color.FromArgb(0, 120, 212);
        _btnStart.ForeColor = Color.White;
        _btnStart.FlatStyle = FlatStyle.Flat;
        _btnStart.FlatAppearance.BorderSize = 0;
        _btnStart.Click += (_, _) => StartReceiver();
        row1.Controls.Add(_btnStart);

        StyleButton(_btnStop, "停止", 80);
        _btnStop.Enabled = false;
        _btnStop.Click += (_, _) => StopReceiver();
        row1.Controls.Add(_btnStop);

        _lblRunState.Text = "未启动";
        _lblRunState.ForeColor = Color.Gray;
        _lblRunState.AutoSize = true;
        _lblRunState.Margin = new Padding(12, 10, 4, 0);
        row1.Controls.Add(_lblRunState);

        var row2 = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = true,
            Padding = new Padding(0),
            Margin = new Padding(0)
        };

        _chkAutoAck.Text = "自动应答 ACK";
        _chkAutoAck.Checked = true;
        _chkAutoAck.AutoSize = true;
        _chkAutoAck.Margin = new Padding(0, 9, 6, 0);
        row2.Controls.Add(_chkAutoAck);

        _cboAckCode.DropDownStyle = ComboBoxStyle.DropDownList;
        _cboAckCode.Width = 60;
        _cboAckCode.Items.AddRange(new object[] { "AA", "AE", "AR" });
        _cboAckCode.SelectedIndex = 0;
        _cboAckCode.Margin = new Padding(0, 7, 14, 0);
        row2.Controls.Add(_cboAckCode);

        _chkAutoSave.Text = "自动保存报文";
        _chkAutoSave.Checked = true;
        _chkAutoSave.AutoSize = true;
        _chkAutoSave.Margin = new Padding(0, 9, 14, 0);
        row2.Controls.Add(_chkAutoSave);

        _chkLenient.Text = "宽松拆包(空闲超时切分)";
        _chkLenient.AutoSize = true;
        _chkLenient.Margin = new Padding(0, 9, 4, 0);
        row2.Controls.Add(_chkLenient);

        _numIdleMs.Minimum = 100;
        _numIdleMs.Maximum = 10000;
        _numIdleMs.Increment = 100;
        _numIdleMs.Value = 800;
        _numIdleMs.Width = 70;
        _numIdleMs.Margin = new Padding(0, 7, 2, 0);
        row2.Controls.Add(_numIdleMs);

        var lblMs = new Label { Text = "ms", AutoSize = true, Margin = new Padding(0, 10, 14, 0) };
        row2.Controls.Add(lblMs);

        _chkChunkLog.Text = "记录分片日志";
        _chkChunkLog.Checked = true;
        _chkChunkLog.AutoSize = true;
        _chkChunkLog.Margin = new Padding(0, 9, 14, 0);
        row2.Controls.Add(_chkChunkLog);

        _chkAutoSelect.Text = "自动选中最新";
        _chkAutoSelect.Checked = true;
        _chkAutoSelect.AutoSize = true;
        _chkAutoSelect.Margin = new Padding(0, 9, 14, 0);
        row2.Controls.Add(_chkAutoSelect);

        _chkReconnect.Text = "断线自动重连";
        _chkReconnect.Checked = true;
        _chkReconnect.AutoSize = true;
        _chkReconnect.Margin = new Padding(0, 9, 14, 0);
        row2.Controls.Add(_chkReconnect);

        StyleButton(_btnSelfTest, "本地自测(分片发送)", 150);
        _btnSelfTest.Margin = new Padding(0, 5, 10, 0);
        _btnSelfTest.Click += async (_, _) => await SelfTestAsync();
        row2.Controls.Add(_btnSelfTest);

        var btnClear = new Button { Text = "清空列表", Width = 90, Height = 28, Margin = new Padding(0, 5, 6, 0) };
        btnClear.Click += (_, _) => ClearMessages();
        row2.Controls.Add(btnClear);

        var btnCopyRaw = new Button { Text = "复制原始", Width = 90, Height = 28, Margin = new Padding(0, 5, 6, 0) };
        btnCopyRaw.Click += (_, _) => CopyCurrentRaw();
        row2.Controls.Add(btnCopyRaw);

        var btnCopyHex = new Button { Text = "复制HEX", Width = 90, Height = 28, Margin = new Padding(0, 5, 6, 0) };
        btnCopyHex.Click += (_, _) => CopyCurrentHex();
        row2.Controls.Add(btnCopyHex);

        var btnOpen = new Button { Text = "打开报文目录", Width = 110, Height = 28, Margin = new Padding(0, 5, 6, 0) };
        btnOpen.Click += (_, _) => OpenFolder();
        row2.Controls.Add(btnOpen);

        _lblLocalIp.AutoSize = true;
        _lblLocalIp.ForeColor = Color.DimGray;
        _lblLocalIp.Margin = new Padding(6, 10, 0, 0);
        row2.Controls.Add(_lblLocalIp);

        top.Controls.Add(row1, 0, 0);
        top.Controls.Add(row2, 0, 1);

        root.Controls.Add(top, 0, 0);

        var split = new SplitContainer
        {
            Dock = DockStyle.Fill,
            FixedPanel = FixedPanel.Panel1
        };

        Shown += (_, _) =>
        {
            try
            {
                split.Panel1MinSize = 320;
                split.Panel2MinSize = 420;
                split.SplitterDistance = Math.Min(640, Math.Max(320, split.Width - 450));
            }
            catch
            {
                // 窗口尺寸极小时忽略
            }
        };

        var listHeader = new Label
        {
            Text = "接收到的报文（最新在上）",
            Dock = DockStyle.Top,
            Height = 26,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(6, 0, 0, 0),
            BackColor = Color.FromArgb(245, 245, 245)
        };

        _lvMessages.Dock = DockStyle.Fill;
        _lvMessages.View = View.Details;
        _lvMessages.FullRowSelect = true;
        _lvMessages.GridLines = true;
        _lvMessages.HideSelection = false;
        _lvMessages.MultiSelect = false;
        _lvMessages.Columns.Add("序号", 38);
        _lvMessages.Columns.Add("时间", 82);
        _lvMessages.Columns.Add("类型", 88);
        _lvMessages.Columns.Add("控制ID", 70);
        _lvMessages.Columns.Add("样本号", 78);
        _lvMessages.Columns.Add("结果数", 44);
        _lvMessages.Columns.Add("字节", 44);
        _lvMessages.Columns.Add("状态", 50);
        _lvMessages.Columns.Add("来源", 140);
        _lvMessages.SelectedIndexChanged += (_, _) =>
        {
            if (_lvMessages.SelectedItems.Count > 0 && _lvMessages.SelectedItems[0].Tag is Hl7Message message)
                ShowMessage(message);
        };

        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("复制原始报文", null, (_, _) => CopyCurrentRaw());
        contextMenu.Items.Add("复制十六进制", null, (_, _) => CopyCurrentHex());
        contextMenu.Items.Add("另存为...", null, (_, _) => SaveCurrentAs());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("从列表移除", null, (_, _) =>
        {
            if (_lvMessages.SelectedItems.Count > 0) _lvMessages.Items.Remove(_lvMessages.SelectedItems[0]);
        });
        _lvMessages.ContextMenuStrip = contextMenu;

        split.Panel1.Controls.Add(_lvMessages);
        split.Panel1.Controls.Add(listHeader);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(CreateTextTab("原始报文", _rtbRaw));
        tabs.TabPages.Add(CreateTextTab("十六进制", _rtbHex));
        tabs.TabPages.Add(CreateTreeTab("字段解析", _tvFields));
        tabs.TabPages.Add(CreateGridTab("检验结果", _gridResults));
        tabs.TabPages.Add(CreateTextTab("网络日志", _rtbLog));
        split.Panel2.Controls.Add(tabs);

        root.Controls.Add(split, 0, 1);

        _lblStatusFolder.Text = "报文目录: " + _messageRoot;
        _statusStrip.Items.Add(_lblStatusConn);
        _statusStrip.Items.Add(new ToolStripStatusLabel("  |  "));
        _statusStrip.Items.Add(_lblStatusMsg);
        _statusStrip.Items.Add(new ToolStripStatusLabel("  |  "));
        _statusStrip.Items.Add(_lblStatusBytes);
        _statusStrip.Items.Add(new ToolStripStatusLabel("  |  "));
        _statusStrip.Items.Add(_lblStatusBuf);
        _statusStrip.Items.Add(_lblStatusFolder);
        root.Controls.Add(_statusStrip, 0, 2);

        Controls.Add(root);

        _rtbLog.BackColor = Color.FromArgb(30, 30, 30);
        _rtbLog.ForeColor = Color.Gainsboro;
        _rtbRaw.BackColor = Color.White;

        _cboEncoding.SelectedIndexChanged += (_, _) => _encoding = SelectedEncoding();
        _chkAutoAck.CheckedChanged += (_, _) => _autoAck = _chkAutoAck.Checked;
        _chkAutoSave.CheckedChanged += (_, _) => _autoSave = _chkAutoSave.Checked;
        _chkLenient.CheckedChanged += (_, _) =>
        {
            if (_service != null) _service.Lenient = _chkLenient.Checked;
        };
        _cboAckCode.SelectedIndexChanged += (_, _) => _ackCode = _cboAckCode.Text;
    }

    private static TabPage CreateTextTab(string title, RichTextBox box)
    {
        box.Dock = DockStyle.Fill;
        box.ReadOnly = true;
        box.WordWrap = false;
        box.ScrollBars = RichTextBoxScrollBars.Both;
        box.Font = new Font("Consolas", 10F);
        box.DetectUrls = false;
        box.HideSelection = false;

        var page = new TabPage(title) { Padding = new Padding(3) };
        page.Controls.Add(box);
        return page;
    }

    private static TabPage CreateTreeTab(string title, TreeView tree)
    {
        tree.Dock = DockStyle.Fill;
        tree.HideSelection = false;
        tree.Font = new Font("Microsoft YaHei UI", 9F);

        var page = new TabPage(title) { Padding = new Padding(3) };
        page.Controls.Add(tree);
        return page;
    }

    private static TabPage CreateGridTab(string title, DataGridView grid)
    {
        grid.Dock = DockStyle.Fill;
        grid.ReadOnly = true;
        grid.AllowUserToAddRows = false;
        grid.AllowUserToDeleteRows = false;
        grid.AllowUserToResizeRows = false;
        grid.RowHeadersVisible = false;
        grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        grid.BackgroundColor = Color.White;
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "序号", FillWeight = 45 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "项目代码", FillWeight = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "项目名称", FillWeight = 120 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "结果", FillWeight = 90 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "单位", FillWeight = 75 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "参考范围", FillWeight = 110 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "异常标志", FillWeight = 70 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "状态", FillWeight = 65 });
        grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "检测时间", FillWeight = 140 });

        var page = new TabPage(title) { Padding = new Padding(3) };
        page.Controls.Add(grid);
        return page;
    }

    private static void AddRowControl(FlowLayoutPanel panel, string label, Control control)
    {
        var lbl = new Label
        {
            Text = label,
            AutoSize = true,
            Margin = new Padding(0, 10, 4, 0)
        };
        if (panel.Controls.Count > 0) lbl.Margin = new Padding(10, 10, 4, 0);

        panel.Controls.Add(lbl);
        control.Margin = new Padding(0, 7, 0, 0);
        panel.Controls.Add(control);
    }

    private static void StyleButton(Button button, string text, int width)
    {
        button.Text = text;
        button.Width = width;
        button.Height = 30;
        button.Margin = new Padding(10, 4, 4, 0);
    }

    private Encoding SelectedEncoding()
    {
        int index = _cboEncoding.SelectedIndex;
        if (index < 0 || index >= EncodingOptions.Length) index = 0;
        try { return Encoding.GetEncoding(EncodingOptions[index].Name); }
        catch { return Encoding.Latin1; }
    }

    private void StartReceiver()
    {
        if (_running) return;

        int port = (int)_numPort.Value;
        bool listenMode = _cboMode.SelectedIndex == 0;

        string host = _txtIp.Text.Trim();
        if (!listenMode && host.Length == 0)
        {
            MessageBox.Show(this, "请输入仪器 IP 地址。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var service = new Hl7ReceiverService { Lenient = _chkLenient.Checked };
        service.ConnectionOpened += connection =>
        {
            LogLine($"✔ 已建立连接 [{connection.Role}]: {connection.Remote}");
        };
        service.ConnectionClosed += (connection, reason) =>
        {
            LogLine($"✘ 连接断开 [{connection.Role}]: {connection.Remote} ({reason})");
        };
        service.ChunkReceived += (connection, chunk) =>
        {
            if (_chkChunkLog.Checked) LogChunk(connection, chunk);
        };
        service.FrameReceived += OnFrameReceived;
        service.Log += LogLine;

        _service = service;

        try
        {
            if (listenMode)
            {
                service.StartServer(port);
                LogLine($"监听模式：本机所有网卡的 {port} 端口已打开，请把仪器数据上报地址配成本机 IP: {port}");
                LogLine("本机可用 IP: " + LocalIpText());
            }
            else
            {
                service.StartClient(host, port, _chkReconnect.Checked);
                LogLine($"连接模式：正在连接仪器 {host}:{port} ...");
            }
        }
        catch (Exception ex)
        {
            _service = null;
            service.Dispose();
            MessageBox.Show(this, "启动失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            LogLine("启动失败: " + ex.Message);
            return;
        }

        _running = true;

        _btnStart.Enabled = false;
        _btnStop.Enabled = true;
        _btnSelfTest.Enabled = listenMode;
        _cboMode.Enabled = false;
        _txtIp.Enabled = false;
        _numPort.Enabled = false;
        _cboEncoding.Enabled = false;
        _lblRunState.Text = "运行中";
        _lblRunState.ForeColor = Color.ForestGreen;
    }

    private void StopReceiver()
    {
        var service = _service;
        _service = null;

        if (service != null)
        {
            service.Stop();
            service.Dispose();
        }

        if (!_running) return;
        _running = false;

        _btnStart.Enabled = true;
        _btnStop.Enabled = false;
        _btnSelfTest.Enabled = false;
        _cboMode.Enabled = true;
        _txtIp.Enabled = true;
        _numPort.Enabled = true;
        _cboEncoding.Enabled = true;
        _lblRunState.Text = "已停止";
        _lblRunState.ForeColor = Color.Gray;
        AppendLog("已停止接收。");
    }

    private void OnFrameReceived(Hl7Connection connection, byte[] payload, bool incomplete)
    {
        if (_service == null) return;

        Hl7Message message;
        try
        {
            message = Hl7Message.Parse(payload, Mllp.Wrap(payload), _encoding, $"{connection.Remote} ({connection.Role})", incomplete);
        }
        catch (Exception ex)
        {
            LogLine("报文解析失败: " + ex.Message);
            return;
        }

        if (_autoSave)
        {
            try
            {
                string directory = Path.Combine(_messageRoot, message.ReceivedAt.ToString("yyyyMMdd"));
                Directory.CreateDirectory(directory);
                string fileName = (message.Incomplete ? "INCOMPLETE_" : "") + message.FileSafeName();
                message.SavedPath = Path.Combine(directory, fileName);
                File.WriteAllBytes(message.SavedPath, message.Payload);
            }
            catch (Exception ex)
            {
                LogLine("保存报文失败: " + ex.Message);
            }
        }

        if (_autoAck && message.AckMessageType != null)
            _ = SendAckAsync(connection, message);

        try
        {
            BeginInvoke(() => AddMessageToUi(message));
        }
        catch (InvalidOperationException)
        {
            // 窗体已关闭
        }
    }

    private async Task SendAckAsync(Hl7Connection connection, Hl7Message message)
    {
        try
        {
            byte[] ack = Hl7Ack.Build(message, _ackCode, "Message accepted", _encoding);
            using var timeout = new CancellationTokenSource(3000);
            await connection.SendAsync(ack, timeout.Token);
            LogLine($"→ 已回复 {message.AckMessageType} 给 {connection.Remote}  MSA|{_ackCode}|{message.ControlId}");
        }
        catch (Exception ex)
        {
            LogLine("回复 ACK 失败: " + ex.Message);
        }
    }

    private void AddMessageToUi(Hl7Message message)
    {
        _messageCount++;

        var item = new ListViewItem(_messageCount.ToString()) { Tag = message };
        item.SubItems.Add(message.ReceivedAt.ToString("HH:mm:ss.fff"));
        item.SubItems.Add(message.MessageType);
        item.SubItems.Add(message.ControlId);
        item.SubItems.Add(message.SampleId);
        item.SubItems.Add(message.Results.Count.ToString());
        item.SubItems.Add(message.Payload.Length.ToString());
        item.SubItems.Add(message.Incomplete ? "不完整" : "完整");
        item.SubItems.Add(message.Source);

        if (message.Incomplete) item.ForeColor = Color.Firebrick;

        _lvMessages.BeginUpdate();
        _lvMessages.Items.Insert(0, item);
        while (_lvMessages.Items.Count > 3000)
            _lvMessages.Items.RemoveAt(_lvMessages.Items.Count - 1);
        _lvMessages.EndUpdate();

        if (_chkAutoSelect.Checked)
        {
            item.Selected = true;
            item.Focused = true;
            _lvMessages.EnsureVisible(0);
        }

        string summary = $"收到报文 #{_messageCount}: 类型={message.MessageType} 控制ID={message.ControlId} 样本={message.SampleId} 结果={message.Results.Count}条 字节={message.Payload.Length} 来源={message.Source}";
        LogLine((message.Incomplete ? "⚠ 不完整 " : "✔ 完整 ") + summary);
    }

    private void ShowMessage(Hl7Message message)
    {
        _rtbRaw.Text = message.GetVisibleText()
            + (string.IsNullOrWhiteSpace(message.SavedPath) ? "" : Environment.NewLine + Environment.NewLine + "已保存: " + message.SavedPath);
        _rtbRaw.SelectionStart = 0;
        _rtbRaw.ScrollToCaret();

        _rtbHex.Text = Mllp.HexDump(message.Frame);
        _rtbHex.SelectionStart = 0;

        _tvFields.BeginUpdate();
        _tvFields.Nodes.Clear();
        var root = _tvFields.Nodes.Add($"{message.MessageType}  控制ID={message.ControlId}  样本={message.SampleId}  {message.Payload.Length} 字节");
        if (message.Incomplete) root.ForeColor = Color.Firebrick;

        foreach (var segment in message.Segments)
        {
            string preview = segment.Raw.Length > 100 ? segment.Raw[..100] + "..." : segment.Raw;
            var segmentNode = root.Nodes.Add($"{segment.Name}  ({segment.Fields.Count} 字段)   {preview}");

            for (int i = 1; i < segment.Fields.Count; i++)
            {
                string value = segment[i];
                segmentNode.Nodes.Add($"{segment.FieldLabel(i)}: {(value.Length == 0 ? "(空)" : value)}");
            }

            bool expand = segment.Name.Equals("MSH", StringComparison.OrdinalIgnoreCase)
                       || segment.Name.Equals("PID", StringComparison.OrdinalIgnoreCase)
                       || segment.Name.Equals("OBR", StringComparison.OrdinalIgnoreCase)
                       || segment.Name.Equals("MSA", StringComparison.OrdinalIgnoreCase);
            if (expand) segmentNode.Expand();
        }
        root.Expand();
        _tvFields.EndUpdate();

        _gridResults.Rows.Clear();
        foreach (var result in message.Results)
        {
            _gridResults.Rows.Add(
                result.No,
                result.Code,
                result.Name,
                result.Value,
                result.Unit,
                result.Reference,
                result.Flag,
                result.Status,
                result.TestTime);
        }
    }

    private async Task SelfTestAsync()
    {
        if (_service == null || !_service.IsListening)
        {
            MessageBox.Show(this, "请先以“服务端监听”模式点击“开始接收”，再执行本地自测。",
                "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        int port = (int)_numPort.Value;
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port);

            byte[] frame = Mllp.Wrap(_encoding.GetBytes(SampleOru));
            int split = frame.Length / 2;

            // 故意拆成两片发送，验证程序能把 TCP 分包重组成完整报文
            await client.GetStream().WriteAsync(frame.AsMemory(0, split));
            await Task.Delay(120);
            await client.GetStream().WriteAsync(frame.AsMemory(split));

            LogLine($"自测：已向 127.0.0.1:{port} 分两片发送模拟 ORU^R01（共 {frame.Length} 字节），请查看是否出现完整报文。");
            await Task.Delay(200);
        }
        catch (Exception ex)
        {
            LogLine("自测失败: " + ex.Message);
        }
    }

    private void LogChunk(Hl7Connection connection, byte[] chunk)
    {
        bool hasStart = Array.IndexOf(chunk, Mllp.SB) >= 0;
        bool hasEnd = false;
        for (int i = 0; i < chunk.Length - 1; i++)
        {
            if (chunk[i] == Mllp.EB && chunk[i + 1] == Mllp.CR) { hasEnd = true; break; }
        }

        LogLine($"收 {chunk.Length,6} B | 0x0B:{(hasStart ? "有" : "无")} 0x1C0D:{(hasEnd ? "有" : "无")} | 剩余缓存:{connection.Extractor.PendingCount,6} B | {Mllp.HexPreview(chunk, 40)}");
    }

    private void LogLine(string text)
    {
        if (IsDisposed) return;

        if (InvokeRequired)
        {
            try { BeginInvoke(() => LogLine(text)); } catch (InvalidOperationException) { }
            return;
        }

        AppendLog(text);
        WriteFileLog(text);
    }

    private void AppendLog(string text)
    {
        _rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}");

        if (_rtbLog.TextLength > 600_000)
        {
            _rtbLog.Text = _rtbLog.Text[^300_000..];
        }

        _rtbLog.SelectionStart = _rtbLog.TextLength;
        _rtbLog.ScrollToCaret();
    }

    private void WriteFileLog(string text)
    {
        try
        {
            Directory.CreateDirectory(_logFolder);
            string file = Path.Combine(_logFolder, DateTime.Now.ToString("yyyyMMdd") + ".log");
            lock (_fileLogLock)
            {
                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {text}{Environment.NewLine}");
            }
        }
        catch { /* 日志写失败不影响接收 */ }
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        var service = _service;
        if (service == null)
        {
            _lblStatusConn.Text = "连接: -";
            _lblStatusMsg.Text = $"消息: {_messageCount}";
            _lblStatusBytes.Text = "累计字节: -";
            _lblStatusBuf.Text = "缓存: -";
            return;
        }

        if (_chkLenient.Checked)
            service.FlushIdle(TimeSpan.FromMilliseconds((double)_numIdleMs.Value));

        _lblStatusConn.Text = $"连接: {service.ConnectionCount}";
        _lblStatusMsg.Text = $"消息: {_messageCount}";
        _lblStatusBytes.Text = $"累计字节: {service.TotalBytesReceived:N0}";
        _lblStatusBuf.Text = $"缓存: {service.PendingBufferBytes:N0} B";
        _lblStatusFolder.Text = "报文目录: " + _messageRoot;
    }

    private Hl7Message? CurrentMessage()
    {
        return _lvMessages.SelectedItems.Count > 0 ? _lvMessages.SelectedItems[0].Tag as Hl7Message : null;
    }

    private void CopyCurrentRaw()
    {
        var message = CurrentMessage();
        if (message == null) return;
        Clipboard.SetText(message.GetVisibleText());
        LogLine("已复制原始报文到剪贴板。");
    }

    private void CopyCurrentHex()
    {
        var message = CurrentMessage();
        if (message == null) return;
        Clipboard.SetText(Mllp.HexDump(message.Frame));
        LogLine("已复制十六进制到剪贴板。");
    }

    private void SaveCurrentAs()
    {
        var message = CurrentMessage();
        if (message == null) return;

        using var dialog = new SaveFileDialog
        {
            Filter = "HL7 报文 (*.hl7)|*.hl7|所有文件 (*.*)|*.*",
            FileName = message.FileSafeName()
        };

        if (dialog.ShowDialog(this) == DialogResult.OK)
        {
            File.WriteAllBytes(dialog.FileName, message.Payload);
            LogLine("已另存为: " + dialog.FileName);
        }
    }

    private void ClearMessages()
    {
        _lvMessages.Items.Clear();
        _messageCount = 0;
        _rtbRaw.Clear();
        _rtbHex.Clear();
        _tvFields.Nodes.Clear();
        _gridResults.Rows.Clear();
        LogLine("已清空报文列表（日志保留）。");
    }

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(_messageRoot);
            Process.Start(new ProcessStartInfo
            {
                FileName = _messageRoot,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开目录失败: " + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void UpdateLocalIps()
    {
        _lblLocalIp.Text = "本机 IP: " + LocalIpText();
    }

    private static string LocalIpText()
    {
        try
        {
            var ips = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
                .Select(address => address.ToString())
                .Distinct()
                .ToArray();
            return ips.Length == 0 ? "（未发现）" : string.Join(" / ", ips);
        }
        catch
        {
            return "（读取失败）";
        }
    }

    private void LoadSettings()
    {
        try
        {
            AppSettings settings;
            if (File.Exists(_settingsPath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_settingsPath)) ?? new AppSettings();
            else
                settings = new AppSettings();

            _cboMode.SelectedIndex = settings.Mode is >= 0 and <= 1 ? settings.Mode : 1;
            _txtIp.Text = string.IsNullOrWhiteSpace(settings.Ip) ? "192.168.26.111" : settings.Ip;
            _numPort.Value = Math.Clamp(settings.Port, 1, 65535);
            _cboEncoding.SelectedIndex = settings.EncodingIndex is >= 0 and <= 3 ? settings.EncodingIndex : 0;
            _chkAutoAck.Checked = settings.AutoAck;
            _cboAckCode.SelectedIndex = settings.AckCodeIndex is >= 0 and <= 2 ? settings.AckCodeIndex : 0;
            _chkAutoSave.Checked = settings.AutoSave;
            _chkLenient.Checked = settings.Lenient;
            _numIdleMs.Value = Math.Clamp(settings.IdleMs, 100, 10000);
            _chkChunkLog.Checked = settings.ChunkLog;
            _chkAutoSelect.Checked = settings.AutoSelect;
            _chkReconnect.Checked = settings.AutoReconnect;
        }
        catch
        {
            _cboMode.SelectedIndex = 1;
            _cboEncoding.SelectedIndex = 0;
            _chkAutoAck.Checked = true;
            _chkAutoSave.Checked = true;
            _chkChunkLog.Checked = true;
            _chkAutoSelect.Checked = true;
            _chkReconnect.Checked = true;
        }

        _encoding = SelectedEncoding();
        _autoAck = _chkAutoAck.Checked;
        _autoSave = _chkAutoSave.Checked;
        _ackCode = _cboAckCode.Text.Length > 0 ? _cboAckCode.Text : "AA";
    }

    private void SaveSettings()
    {
        try
        {
            var settings = new AppSettings
            {
                Mode = _cboMode.SelectedIndex,
                Ip = _txtIp.Text.Trim(),
                Port = (int)_numPort.Value,
                EncodingIndex = _cboEncoding.SelectedIndex,
                AutoAck = _chkAutoAck.Checked,
                AckCodeIndex = _cboAckCode.SelectedIndex,
                AutoSave = _chkAutoSave.Checked,
                Lenient = _chkLenient.Checked,
                IdleMs = (int)_numIdleMs.Value,
                ChunkLog = _chkChunkLog.Checked,
                AutoSelect = _chkAutoSelect.Checked,
                AutoReconnect = _chkReconnect.Checked
            };

            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 设置保存失败不影响使用 */ }
    }

    private sealed class AppSettings
    {
        public int Mode { get; set; } = 1;
        public string Ip { get; set; } = "192.168.26.111";
        public int Port { get; set; } = 5150;
        public int EncodingIndex { get; set; }
        public bool AutoAck { get; set; } = true;
        public int AckCodeIndex { get; set; }
        public bool AutoSave { get; set; } = true;
        public bool Lenient { get; set; }
        public int IdleMs { get; set; } = 800;
        public bool ChunkLog { get; set; } = true;
        public bool AutoSelect { get; set; } = true;
        public bool AutoReconnect { get; set; } = true;
    }
}
