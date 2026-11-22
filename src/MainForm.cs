// Main window: WinForms UI, point-cloud view control, AI analysis flow, history.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace L2PointAI
{
    public sealed class MainForm : Form
    {
        readonly AppSettings _cfg = AppSettings.Load();
        readonly LiveCapture _live = new LiveCapture();
        readonly AiClient _ai = new AiClient();
        readonly System.Windows.Forms.Timer _liveTimer = new System.Windows.Forms.Timer();

        float _ui = 1f;
        PointCloud _cloud = PointCloud.Empty();
        PointCloudView _view;
        Camera _cam = new Camera();

        // left
        Label _lblFile, _lblStats, _lblLive;
        Button _btnOpen, _btnLiveStart, _btnLiveStop, _btnGrab, _btnSaveCsv;
        ComboBox _cbColor, _cbBg;
        NumericUpDown _numSize;
        CheckBox _chkGrid, _chkAxes;

        // right
        RadioButton _rbOllama, _rbApi;
        TextBox _txtOllamaUrl, _txtApiBase, _txtApiModel, _txtApiKey;
        ComboBox _cbOllamaModel, _cbPreset;
        Button _btnRefreshModels;
        CheckBox _chkSendImages, _chkRememberKey;
        ComboBox _cbThink;
        NumericUpDown _numTemp, _numTokens;
        Label _lblVision;

        ComboBox _cbTemplate;
        TextBox _txtPrompt;
        Button _btnLoadTemplate, _btnSaveTemplate, _btnResetPrompt;

        Button _btnAnalyze, _btnStop, _btnSaveReport, _btnCopy;
        TabControl _tabs;
        TextBox _txtNl, _txtJson, _txtReport, _txtLog;
        ListBox _lstHistory;
        Button _btnHistOpen, _btnHistFolder, _btnHistDelete;

        StatusStrip _status;
        ToolStripStatusLabel _stLabel;
        ToolStripProgressBar _stProgress;

        bool _busy;
        StringBuilder _stream = new StringBuilder();
        TextBox _txtThink;
        ListView _lvClusters;
        Label _lblSeg;
        Button _btnModeRot, _btnModeSel, _btnModeMeas;
        ComboBox _cbMeasureKind;
        Button _btnVis;
        NumericUpDown _numVoxel, _numMinPts;
        double _usedVoxel = 0.2;
        int _usedMinPts = 30;
        List<ClusterInfo> _clusters = new List<ClusterInfo>();
        List<int> _sel = new List<int>();
        readonly StringBuilder _thinking = new StringBuilder();
        readonly object _thinkLock = new object();
        readonly object _streamLock = new object();
        DateTime _lastThinkUi = DateTime.MinValue;
        List<PromptTemplate> _templates;

        public MainForm()
        {
            Text = "L2 点云 AI 分析器  —  Unitree 4D LiDAR L2";
            // 手写布局必须显式声明设计基准 DPI，否则 WinForms 无法按 DPI 缩放控件尺寸，
            // 而 9pt 字体在高 DPI 下会自动变大 → 文字溢出/控件堆叠。
            // 手写界面：不用 WinForms 自动缩放（与 DPI 感知模式叠加会缩放两次）。
            // 统一按 _ui 缩放：字体用"像素"单位，保证字体与控件同一把尺子。
            AutoScaleMode = AutoScaleMode.None;
            _ui = ComputeUiScale();
            Width = 1480;            // 设计尺寸（96 DPI 基准），随后由 ApplyUiScale() 统一放大
            Height = 940;
            MinimumSize = new Size(980, 620);
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 12f * _ui, GraphicsUnit.Pixel);
            DoubleBuffered = true;

            BuildUi();
            _templates = PromptTemplates.Load();
            FillTemplates();
            LoadSettingsToUi();
            UpdateBackendPanels();
            Load += delegate { ClampToScreen(); };
            ApplyUiScale();
            AppendLog("就绪。可打开导出的 CSV/TXT/PLY，或启动直采后抓帧。");
            UpdateButtons();
        }

        // ------------------------------------------------------------- UI build
        void BuildUi()
        {
            MenuStrip menu = new MenuStrip();
            ToolStripMenuItem mFile = new ToolStripMenuItem("文件");
            mFile.DropDownItems.Add(Item("打开点云…", delegate { OpenCloudDialog(); }));
            mFile.DropDownItems.Add(Item("保存当前点云为 CSV…", delegate { SaveCloudCsv(); }));
            mFile.DropDownItems.Add(Item("分析报告另存为…", delegate { SaveReport(); }));
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add(Item("打开历史目录", delegate { OpenFolder(AppSettings.HistoryDir); }));
            mFile.DropDownItems.Add(Item("打开导出目录", delegate { OpenFolder(AppSettings.ExportsDir); }));
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add(Item("退出", delegate { Close(); }));
            menu.Items.Add(mFile);

            ToolStripMenuItem mCap = new ToolStripMenuItem("采集");
            mCap.DropDownItems.Add(Item("启动接收", delegate { StartLive(); }));
            mCap.DropDownItems.Add(Item("停止接收", delegate { StopLive(); }));
            mCap.DropDownItems.Add(Item("抓帧（送入分析）", delegate { GrabFrame(); }));
            menu.Items.Add(mCap);

            ToolStripMenuItem mAi = new ToolStripMenuItem("分析");
            mAi.DropDownItems.Add(Item("开始分析", delegate { StartAnalysis(); }));
            mAi.DropDownItems.Add(Item("停止", delegate { StopAnalysis(); }));
            menu.Items.Add(mAi);

            ToolStripMenuItem mHelp = new ToolStripMenuItem("帮助");
            mHelp.DropDownItems.Add(Item("关于", delegate { ShowAbout(); }));
            menu.Items.Add(mHelp);
            MainMenuStrip = menu;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 3;
            root.RowCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 47));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            root.Controls.Add(ScrollHost(BuildLeft()), 0, 0);
            root.Controls.Add(BuildCenter(), 1, 0);
            root.Controls.Add(ScrollHost(BuildRight()), 2, 0);

            Controls.Add(root);
            Controls.Add(menu);

            _status = new StatusStrip();
            _stLabel = new ToolStripStatusLabel("就绪");
            _stProgress = new ToolStripProgressBar();
            _stProgress.Size = new Size(140, 16);
            _stProgress.Visible = false;
            _status.Items.Add(_stLabel);
            _status.Items.Add(new ToolStripStatusLabel("  "));
            _status.Items.Add(_stProgress);
            Controls.Add(_status);
        }



        static Control ScrollHost(Control inner)
        {
            Panel host = new Panel();
            host.Dock = DockStyle.Fill;
            host.AutoScroll = true;
            inner.Dock = DockStyle.Fill;
            host.Controls.Add(inner);
            return host;
        }
        Control BuildCenter()
        {
            TableLayoutPanel c = new TableLayoutPanel();
            c.Dock = DockStyle.Fill;
            c.ColumnCount = 1;
            c.RowCount = 2;
            c.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            c.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.WrapContents = false;
            bar.Padding = new Padding(3, 3, 3, 0);
            _btnModeRot = MkTool("旋转");
            _btnModeSel = MkTool("框选");
            _btnModeMeas = MkTool("测距");
            _btnModeRot.Width = 62; _btnModeSel.Width = 62; _btnModeMeas.Width = 62;
            Button clear = MkTool("清除选择");
            Button seg = MkTool("自动分割");
            Button exp = MkTool("导出选中");
            seg.Visible = false; exp.Visible = false;
            _cbMeasureKind = new ComboBox();
            _cbMeasureKind.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbMeasureKind.Width = 104;
            _cbMeasureKind.Items.AddRange(new object[] { "3D 距离", "水平距离", "点到面" });
            _cbMeasureKind.SelectedIndex = 0;
            _cbMeasureKind.SelectedIndexChanged += delegate
            {
                if (_view != null) _view.Kind = (MeasureKind)Math.Max(0, _cbMeasureKind.SelectedIndex);
            };
            _btnVis = MkTool("可见面");
            _btnVis.Width = 76;
            _btnVis.BackColor = Color.FromArgb(210, 232, 255);
            _btnVis.Click += delegate
            {
                if (_view == null) return;
                _view.DepthTestSelection = !_view.DepthTestSelection;
                _btnVis.BackColor = _view.DepthTestSelection ? Color.FromArgb(210, 232, 255) : SystemColors.Control;
                SetStatus(_view.DepthTestSelection ? "框选：只选可见表面（深度测试开）" : "框选：包含被遮挡的点（深度测试关）");
            };

            _btnModeRot.Click += delegate { SetViewMode(ViewMode.Rotate); };
            _btnModeSel.Click += delegate { SetViewMode(ViewMode.Select); };
            _btnModeMeas.Click += delegate { SetViewMode(ViewMode.Measure); };
            clear.Click += delegate { ClearSelection(); };
            seg.Click += delegate { RunSegmentation(); };
            exp.Click += delegate { ExportSelected(); };
            bar.Controls.Add(_btnModeRot);
            bar.Controls.Add(_btnModeSel);
            bar.Controls.Add(_btnModeMeas);
            bar.Controls.Add(_cbMeasureKind);
            bar.Controls.Add(_btnVis);
            bar.Controls.Add(clear);

            _view = new PointCloudView(this);
            _view.UiScale = _ui;
            _view.SelectionChanged += OnSelectionChanged;
            _view.Measured += OnMeasured;

            c.Controls.Add(bar, 0, 0);
            c.Controls.Add(_view, 0, 1);
            SetViewMode(ViewMode.Rotate);
            return c;
        }

        Button MkTool(string text)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = 84;
            b.Height = 30;
            b.Margin = new Padding(2, 0, 2, 0);
            return b;
        }

        void SetViewMode(ViewMode mode)
        {
            if (_view == null) return;
            _view.Mode = mode;
            Color on = Color.FromArgb(210, 232, 255);
            Color off = SystemColors.Control;
            if (_btnModeRot != null) _btnModeRot.BackColor = mode == ViewMode.Rotate ? on : off;
            if (_btnModeSel != null) _btnModeSel.BackColor = mode == ViewMode.Select ? on : off;
            if (_btnModeMeas != null) _btnModeMeas.BackColor = mode == ViewMode.Measure ? on : off;
            SetStatus(mode == ViewMode.Rotate ? "旋转模式：左键旋转，右键平移，滚轮缩放"
                    : mode == ViewMode.Select ? "框选模式：在视图里拖一个矩形"
                    : "测距模式：依次点击两个点（会自动吸附最近的点）");
        }

        void ClearSelection()
        {
            if (_cloud != null) _cloud.Selected = null;
            _sel = new List<int>();
            if (_lblSeg != null) _lblSeg.Text = "（无选区）";
            if (_view != null) _view.Redraw();
        }

        void OnSelectionChanged(List<int> indices)
        {
            _sel = indices ?? new List<int>();
            if (_cloud == null || _cloud.Count == 0) return;
            if (_sel.Count == 0)
            {
                if (_lblSeg != null) _lblSeg.Text = "框选为空（试着框住一处点云）";
                SetStatus("框选为空");
                return;
            }
            ClusterInfo info = CloudSegmenter.Describe(_cloud, _sel, 0);
            string text = string.Format(
                "选区：{0:N0} 点\r\n尺寸：{1:F2} × {2:F2} × {3:F2} m\r\n中心：({4:F2}, {5:F2}, {6:F2})\r\n距离：最近 {7:F2} m / 平均 {8:F2} m / 最远 {9:F2} m",
                info.Points, info.SizeX, info.SizeY, info.SizeZ, info.Cx, info.Cy, info.Cz,
                info.MinRange, info.MeanRange, info.MaxRange);
            if (_lblSeg != null) _lblSeg.Text = text;
            AppendLog(string.Format("框选 {0} 点，最近 {1:F2} m，方向尺寸 {2:F2}×{3:F2}×{4:F2} m",
                info.Points, info.MinRange, info.SizeX, info.SizeY, info.SizeZ));
            SetStatus(string.Format("框选 {0:N0} 点，最近 {1:F2} m", info.Points, info.MinRange));
        }

        void OnMeasured(MeasureResult m)
        {
            if (m == null)
            {
                SetStatus("附近没有可吸附的点，换个位置点");
                return;
            }
            if (m.Mode < 0)
            {
                SetStatus(string.Format("第一点 ({0:F2}, {1:F2}, {2:F2})，请点第二个点", m.Ax, m.Ay, m.Az));
                if (_lblSeg != null)
                    _lblSeg.Text = string.Format("第一点：({0:F2}, {1:F2}, {2:F2})\r\n请点第二个点…", m.Ax, m.Ay, m.Az);
                return;
            }
            string text = "测距：" + m.Label;
            if (m.Mode == (int)MeasureKind.PointToPlane)
                text += string.Format("\r\n平面：点 ({0:F2},{1:F2},{2:F2})，法向 ({3:F2},{4:F2},{5:F2})，邻域 {6} 点",
                    m.Ax, m.Ay, m.Az, m.Nx, m.Ny, m.Nz, (int)m.PlanePoints);
            if (_lblSeg != null) _lblSeg.Text = text;
            AppendLog("测距：" + m.Label);
            SetStatus(string.Format("测距 {0:F3} m", m.Distance));
        }
        static ToolStripMenuItem Item(string text, EventHandler h)
        {
            ToolStripMenuItem it = new ToolStripMenuItem(text);
            it.Click += h;
            return it;
        }

        Control BuildLeft()
        {
            TableLayoutPanel p = new TableLayoutPanel();
            p.Dock = DockStyle.Fill;
            p.ColumnCount = 1;
            p.RowCount = 4;
            p.Padding = new Padding(6);
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 196));
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 220));
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 168));
            p.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            GroupBox g1 = new GroupBox();
            g1.Text = "数据源";
            g1.Dock = DockStyle.Fill;
            TableLayoutPanel t1 = new TableLayoutPanel();
            t1.Dock = DockStyle.Fill;
            t1.ColumnCount = 2;
            t1.RowCount = 5;
            t1.Padding = new Padding(6);
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t1.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            t1.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            t1.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            t1.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            t1.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            _btnOpen = new Button(); _btnOpen.Text = "打开点云文件…"; _btnOpen.Dock = DockStyle.Fill;
            _btnOpen.Click += delegate { OpenCloudDialog(); };
            t1.Controls.Add(_btnOpen, 0, 0);
            t1.SetColumnSpan(_btnOpen, 2);

            _btnLiveStart = new Button(); _btnLiveStart.Text = "启动接收"; _btnLiveStart.Dock = DockStyle.Fill;
            _btnLiveStart.Click += delegate { StartLive(); };
            t1.Controls.Add(_btnLiveStart, 0, 1);
            _btnLiveStop = new Button(); _btnLiveStop.Text = "停止"; _btnLiveStop.Dock = DockStyle.Fill;
            _btnLiveStop.Click += delegate { StopLive(); };
            t1.Controls.Add(_btnLiveStop, 1, 1);

            _btnGrab = new Button(); _btnGrab.Text = "抓帧"; _btnGrab.Dock = DockStyle.Fill;
            _btnGrab.Click += delegate { GrabFrame(); };
            t1.Controls.Add(_btnGrab, 0, 2);
            _btnSaveCsv = new Button(); _btnSaveCsv.Text = "存 CSV"; _btnSaveCsv.Dock = DockStyle.Fill;
            _btnSaveCsv.Click += delegate { SaveCloudCsv(); };
            t1.Controls.Add(_btnSaveCsv, 1, 2);

            _lblLive = new Label();
            _lblLive.Text = "直采未启动（UDP 6201）";
            _lblLive.Dock = DockStyle.Fill;
            _lblLive.ForeColor = Color.DimGray;
            t1.Controls.Add(_lblLive, 0, 3);
            t1.SetColumnSpan(_lblLive, 2);

            _lblFile = new Label();
            _lblFile.Text = "当前：无数据";
            _lblFile.Dock = DockStyle.Fill;
            _lblFile.AutoEllipsis = true;
            t1.Controls.Add(_lblFile, 0, 4);
            t1.SetColumnSpan(_lblFile, 2);

            g1.Controls.Add(t1);
            p.Controls.Add(g1, 0, 0);

            GroupBox g2 = new GroupBox();
            g2.Text = "点云信息";
            g2.Dock = DockStyle.Fill;
            _lblStats = new Label();
            _lblStats.Dock = DockStyle.Fill;
            _lblStats.Padding = new Padding(6);
            _lblStats.Font = new Font("Consolas", 11.3f * _ui, GraphicsUnit.Pixel);
            _lblStats.Text = "（尚未加载点云）";
            g2.Controls.Add(_lblStats);
            p.Controls.Add(g2, 0, 1);

            GroupBox g3 = new GroupBox();
            g3.Text = "显示";
            g3.Dock = DockStyle.Fill;
            TableLayoutPanel t3 = new TableLayoutPanel();
            t3.Dock = DockStyle.Fill;
            t3.ColumnCount = 2;
            t3.RowCount = 5;
            t3.Padding = new Padding(6);
            for (int r = 0; r < 4; r++) t3.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            t3.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t3.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            t3.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            t3.Controls.Add(MkLabel("着色"), 0, 0);
            _cbColor = new ComboBox(); _cbColor.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbColor.Items.AddRange(new object[] { "按高度", "按强度", "按距离", "单色" });
            _cbColor.SelectedIndex = 0; _cbColor.Dock = DockStyle.Fill;
            _cbColor.SelectedIndexChanged += delegate { ApplyDisplayOptions(); };
            t3.Controls.Add(_cbColor, 1, 0);

            t3.Controls.Add(MkLabel("点大小"), 0, 1);
            _numSize = new NumericUpDown(); _numSize.Minimum = 1; _numSize.Maximum = 6; _numSize.Value = 2;
            _numSize.Dock = DockStyle.Fill;
            _numSize.ValueChanged += delegate { ApplyDisplayOptions(); };
            t3.Controls.Add(_numSize, 1, 1);

            t3.Controls.Add(MkLabel("背景"), 0, 2);
            _cbBg = new ComboBox(); _cbBg.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbBg.Items.AddRange(new object[] { "深色", "灰色", "白色" });
            _cbBg.SelectedIndex = 0; _cbBg.Dock = DockStyle.Fill;
            _cbBg.SelectedIndexChanged += delegate { ApplyDisplayOptions(); };
            t3.Controls.Add(_cbBg, 1, 2);

            FlowLayoutPanel chkRow = new FlowLayoutPanel();
            chkRow.Dock = DockStyle.Fill;
            chkRow.WrapContents = false;
            chkRow.Margin = new Padding(0);
            _chkGrid = new CheckBox();
            _chkGrid.Text = "地面网格";
            _chkGrid.Checked = true;
            _chkGrid.AutoSize = true;
            _chkGrid.Margin = new Padding(0, 7, 14, 0);
            _chkGrid.CheckedChanged += delegate { ApplyDisplayOptions(); };
            _chkAxes = new CheckBox();
            _chkAxes.Text = "坐标轴";
            _chkAxes.Checked = true;
            _chkAxes.AutoSize = true;
            _chkAxes.Margin = new Padding(0, 7, 0, 0);
            _chkAxes.CheckedChanged += delegate { ApplyDisplayOptions(); };
            chkRow.Controls.Add(_chkGrid);
            chkRow.Controls.Add(_chkAxes);
            t3.Controls.Add(chkRow, 0, 3);
            t3.SetColumnSpan(chkRow, 2);

            Button reset = new Button(); reset.Text = "重置视角"; reset.Dock = DockStyle.Top;
            reset.Click += delegate { if (_view != null) _view.ResetView(); };
            t3.Controls.Add(reset, 0, 4);
            t3.SetColumnSpan(reset, 2);

            g3.Controls.Add(t3);
            p.Controls.Add(g3, 0, 2);

            Label tip = new Label();
            tip.Dock = DockStyle.Fill;
            tip.ForeColor = Color.DimGray;
            tip.Padding = new Padding(4);
            tip.Text = "左键拖动=旋转，滚轮=缩放，右键=平移，双击=重置。\r\n框选/测距：用视图上方工具栏切换模式。";
            p.Controls.Add(tip, 0, 3);
            return p;
        }

        static Label MkLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Dock = DockStyle.Fill;
            return l;
        }

        Control BuildRight()
        {
            int rowH = Math.Max(28, Font.Height + 10);

            TableLayoutPanel p = new TableLayoutPanel();
            p.Dock = DockStyle.Fill;
            p.ColumnCount = 1;
            p.RowCount = 4;
            p.Padding = new Padding(6);
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH * 10 + 58));
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 252));
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            p.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            GroupBox g1 = new GroupBox();
            g1.Text = "模型";
            g1.Dock = DockStyle.Fill;
            TableLayoutPanel t1 = new TableLayoutPanel();
            t1.Dock = DockStyle.Fill;
            t1.ColumnCount = 3;
            t1.RowCount = 10;
            t1.Padding = new Padding(8, 8, 8, 6);
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t1.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            for (int r = 0; r < 10; r++) t1.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));

            _rbOllama = new RadioButton();
            _rbOllama.Text = "本地 Ollama";
            _rbOllama.Checked = true;
            _rbOllama.Dock = DockStyle.Fill;
            _rbOllama.CheckedChanged += delegate { UpdateBackendPanels(); };
            t1.Controls.Add(_rbOllama, 0, 0);
            t1.SetColumnSpan(_rbOllama, 2);

            _rbApi = new RadioButton();
            _rbApi.Text = "在线 API";
            _rbApi.Dock = DockStyle.Fill;
            _rbApi.CheckedChanged += delegate { UpdateBackendPanels(); };
            t1.Controls.Add(_rbApi, 2, 0);

            t1.Controls.Add(MkLabel("地址"), 0, 1);
            _txtOllamaUrl = new TextBox(); _txtOllamaUrl.Dock = DockStyle.Fill;
            t1.Controls.Add(_txtOllamaUrl, 1, 1);
            _btnRefreshModels = new Button(); _btnRefreshModels.Text = "刷新"; _btnRefreshModels.Dock = DockStyle.Fill;
            _btnRefreshModels.Click += delegate { RefreshOllamaModels(); };
            t1.Controls.Add(_btnRefreshModels, 2, 1);

            t1.Controls.Add(MkLabel("模型"), 0, 2);
            _cbOllamaModel = new ComboBox(); _cbOllamaModel.Dock = DockStyle.Fill;
            _cbOllamaModel.DropDownStyle = ComboBoxStyle.DropDown;
            t1.Controls.Add(_cbOllamaModel, 1, 2);
            t1.SetColumnSpan(_cbOllamaModel, 2);

            _lblVision = new Label();
            _lblVision.Text = "";
            _lblVision.ForeColor = Color.DarkOrange;
            _lblVision.Dock = DockStyle.Fill;
            _lblVision.AutoEllipsis = true;
            _lblVision.TextAlign = ContentAlignment.MiddleLeft;
            t1.Controls.Add(_lblVision, 0, 3);
            t1.SetColumnSpan(_lblVision, 3);

            t1.Controls.Add(MkLabel("BaseURL"), 0, 4);
            _txtApiBase = new TextBox(); _txtApiBase.Dock = DockStyle.Fill;
            t1.Controls.Add(_txtApiBase, 1, 4);
            t1.SetColumnSpan(_txtApiBase, 2);

            t1.Controls.Add(MkLabel("预设"), 0, 5);
            _cbPreset = new ComboBox();
            _cbPreset.Dock = DockStyle.Fill;
            _cbPreset.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbPreset.Items.AddRange(new object[] {
                "OpenAI gpt-4o-mini", "DeepSeek deepseek-chat", "Moonshot 视觉预览",
                "通义 qwen-vl-max", "自定义"
            });
            _cbPreset.SelectedIndex = 0;
            _cbPreset.SelectedIndexChanged += delegate { ApplyPreset(); };
            t1.Controls.Add(_cbPreset, 1, 5);
            t1.SetColumnSpan(_cbPreset, 2);

            t1.Controls.Add(MkLabel("模型名"), 0, 6);
            _txtApiModel = new TextBox(); _txtApiModel.Dock = DockStyle.Fill;
            t1.Controls.Add(_txtApiModel, 1, 6);
            t1.SetColumnSpan(_txtApiModel, 2);

            t1.Controls.Add(MkLabel("API Key"), 0, 7);
            _txtApiKey = new TextBox(); _txtApiKey.Dock = DockStyle.Fill;
            _txtApiKey.UseSystemPasswordChar = true;
            t1.Controls.Add(_txtApiKey, 1, 7);
            _chkRememberKey = new CheckBox(); _chkRememberKey.Text = "记住"; _chkRememberKey.Checked = true;
            _chkRememberKey.Dock = DockStyle.Fill;
            t1.Controls.Add(_chkRememberKey, 2, 7);

            TableLayoutPanel row = new TableLayoutPanel();
            row.ColumnCount = 5;
            row.Dock = DockStyle.Fill;
            row.Margin = new Padding(0);
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 48));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 74));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(MkLabel("温度"), 0, 0);
            _numTemp = new NumericUpDown();
            _numTemp.DecimalPlaces = 2; _numTemp.Increment = 0.05M;
            _numTemp.Minimum = 0; _numTemp.Maximum = 2; _numTemp.Value = 0.30M;
            _numTemp.Dock = DockStyle.Fill;
            row.Controls.Add(_numTemp, 1, 0);
            row.Controls.Add(MkLabel("Tokens"), 2, 0);
            _numTokens = new NumericUpDown();
            _numTokens.Minimum = 128; _numTokens.Maximum = 8192; _numTokens.Increment = 128;
            _numTokens.Value = 2048; _numTokens.Dock = DockStyle.Fill;
            row.Controls.Add(_numTokens, 3, 0);
            t1.Controls.Add(row, 0, 8);
            t1.SetColumnSpan(row, 3);

            FlowLayoutPanel optRow = new FlowLayoutPanel();
            optRow.Dock = DockStyle.Fill;
            optRow.WrapContents = false;
            optRow.Margin = new Padding(0);
            _chkSendImages = new CheckBox();
            _chkSendImages.Text = "发送三视图图片";
            _chkSendImages.Checked = true;
            _chkSendImages.AutoSize = true;
            _chkSendImages.Margin = new Padding(0, 6, 16, 0);
            _cbThink = new ComboBox();
            _cbThink.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbThink.Width = 190;
            _cbThink.Margin = new Padding(0, 2, 0, 0);
            _cbThink.Items.AddRange(new object[] { "思考链：关闭 (快)", "思考链：自动", "思考链：开启（显示推理）" });
            _cbThink.SelectedIndex = 0;
            optRow.Controls.Add(_chkSendImages);
            optRow.Controls.Add(_cbThink);
            t1.Controls.Add(optRow, 0, 9);
            t1.SetColumnSpan(optRow, 3);

            g1.Controls.Add(t1);
            p.Controls.Add(g1, 0, 0);

            GroupBox g2 = new GroupBox();
            g2.Text = "提示词（可自由修改）";
            g2.Dock = DockStyle.Fill;
            TableLayoutPanel t2 = new TableLayoutPanel();
            t2.Dock = DockStyle.Fill;
            t2.ColumnCount = 4;
            t2.RowCount = 2;
            t2.Padding = new Padding(6);
            t2.RowStyles.Add(new RowStyle(SizeType.Absolute, rowH));
            t2.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));
            t2.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 66));

            _cbTemplate = new ComboBox();
            _cbTemplate.Dock = DockStyle.Fill;
            _cbTemplate.DropDownStyle = ComboBoxStyle.DropDownList;
            _cbTemplate.SelectedIndexChanged += delegate { LoadSelectedTemplate(); };
            t2.Controls.Add(_cbTemplate, 0, 0);

            _btnLoadTemplate = new Button(); _btnLoadTemplate.Text = "载入";
            _btnLoadTemplate.Click += delegate { LoadSelectedTemplate(); };
            t2.Controls.Add(_btnLoadTemplate, 1, 0);
            _btnSaveTemplate = new Button(); _btnSaveTemplate.Text = "另存";
            _btnSaveTemplate.Click += delegate { SavePromptAsTemplate(); };
            t2.Controls.Add(_btnSaveTemplate, 2, 0);
            _btnResetPrompt = new Button(); _btnResetPrompt.Text = "默认";
            _btnResetPrompt.Click += delegate
            {
                _txtPrompt.Text = PromptTemplates.DefaultText();
                SetStatus("已恢复默认提示词");
            };
            t2.Controls.Add(_btnResetPrompt, 3, 0);

            _txtPrompt = new TextBox();
            _txtPrompt.Dock = DockStyle.Fill;
            _txtPrompt.Multiline = true;
            _txtPrompt.ScrollBars = ScrollBars.Vertical;
            _txtPrompt.Font = new Font("Consolas", 12f * _ui, GraphicsUnit.Pixel);
            t2.Controls.Add(_txtPrompt, 0, 1);
            t2.SetColumnSpan(_txtPrompt, 4);

            g2.Controls.Add(t2);
            p.Controls.Add(g2, 0, 1);

            FlowLayoutPanel btns = new FlowLayoutPanel();
            btns.Dock = DockStyle.Fill;
            btns.FlowDirection = FlowDirection.LeftToRight;
            btns.WrapContents = false;
            _btnAnalyze = new Button(); _btnAnalyze.Text = "开始分析"; _btnAnalyze.Width = 96; _btnAnalyze.Height = 30;
            _btnAnalyze.Click += delegate { StartAnalysis(); };
            _btnStop = new Button(); _btnStop.Text = "停止"; _btnStop.Width = 64; _btnStop.Height = 30;
            _btnStop.Enabled = false;
            _btnStop.Click += delegate { StopAnalysis(); };
            _btnSaveReport = new Button(); _btnSaveReport.Text = "保存报告"; _btnSaveReport.Width = 88; _btnSaveReport.Height = 30;
            _btnSaveReport.Click += delegate { SaveReport(); };
            _btnCopy = new Button(); _btnCopy.Text = "复制结论"; _btnCopy.Width = 88; _btnCopy.Height = 30;
            _btnCopy.Click += delegate { CopyConclusion(); };
            btns.Controls.Add(_btnAnalyze);
            btns.Controls.Add(_btnStop);
            btns.Controls.Add(_btnSaveReport);
            btns.Controls.Add(_btnCopy);
            p.Controls.Add(btns, 0, 2);

            _tabs = new TabControl();
            _tabs.Dock = DockStyle.Fill;
            _tabs.Multiline = true;

            _txtNl = NewOutputBox();
            _txtJson = NewOutputBox();
            _txtJson.Font = new Font("Consolas", 12f * _ui, GraphicsUnit.Pixel);
            _txtReport = NewOutputBox();
            _txtReport.Font = new Font("Consolas", 11.3f * _ui, GraphicsUnit.Pixel);

            _txtThink = NewOutputBox();
            _txtThink.Font = new Font("Consolas", 11.3f * _ui, GraphicsUnit.Pixel);
            _txtThink.Text = "（思考链关闭时本页不产生内容）";

            _txtLog = new TextBox();
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.Dock = DockStyle.Fill;
            _txtLog.Font = new Font("Consolas", 11.3f * _ui, GraphicsUnit.Pixel);

            TabPage pNl = new TabPage("结论");
            pNl.Controls.Add(_txtNl);
            TabPage pJson = new TabPage("JSON");
            pJson.Controls.Add(_txtJson);
            TabPage pRep = new TabPage("报告");
            pRep.Controls.Add(_txtReport);
            TabPage pThink = new TabPage("思考");
            pThink.Controls.Add(_txtThink);
            TabPage pSeg = new TabPage("分割");
            pSeg.Controls.Add(BuildSegPage());
            TabPage pLog = new TabPage("日志");
            pLog.Controls.Add(_txtLog);
            TabPage pHis = new TabPage("历史");

            TableLayoutPanel th = new TableLayoutPanel();
            th.Dock = DockStyle.Fill;
            th.ColumnCount = 1;
            th.RowCount = 2;
            th.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            th.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            _lstHistory = new ListBox();
            _lstHistory.Dock = DockStyle.Fill;
            _lstHistory.DoubleClick += delegate { OpenHistoryReport(); };
            th.Controls.Add(_lstHistory, 0, 0);
            FlowLayoutPanel hb = new FlowLayoutPanel();
            hb.Dock = DockStyle.Fill;
            hb.WrapContents = false;
            _btnHistOpen = new Button(); _btnHistOpen.Text = "打开报告"; _btnHistOpen.Width = 86;
            _btnHistOpen.Click += delegate { OpenHistoryReport(); };
            _btnHistFolder = new Button(); _btnHistFolder.Text = "打开文件夹"; _btnHistFolder.Width = 92;
            _btnHistFolder.Click += delegate
            {
                HistoryEntry e = _lstHistory.SelectedItem as HistoryEntry;
                if (e != null) OpenFolder(e.Dir);
            };
            _btnHistDelete = new Button(); _btnHistDelete.Text = "删除"; _btnHistDelete.Width = 62;
            _btnHistDelete.Click += delegate { DeleteHistory(); };
            hb.Controls.Add(_btnHistOpen);
            hb.Controls.Add(_btnHistFolder);
            hb.Controls.Add(_btnHistDelete);
            th.Controls.Add(hb, 0, 1);
            pHis.Controls.Add(th);

            _tabs.TabPages.Add(pNl);
            _tabs.TabPages.Add(pJson);
            _tabs.TabPages.Add(pRep);
            _tabs.TabPages.Add(pThink);
            _tabs.TabPages.Add(pSeg);
            _tabs.TabPages.Add(pHis);
            _tabs.TabPages.Add(pLog);
            p.Controls.Add(_tabs, 0, 3);

            return p;
        }

        static TextBox NewOutputBox()
        {
            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Both;
            t.WordWrap = false;
            t.Dock = DockStyle.Fill;
            t.BackColor = Color.White;
            return t;
        }


        // ------------------------------------------------------------ DPI adaptivity

        /// <summary>
        /// One scale factor for the whole hand-built UI.
        /// Takes the monitor DPI but never exceeds what the work area can hold,
        /// so the window always fits (200% scaling on a 1080p screen included).
        /// </summary>
        float ComputeUiScale()
        {
            float dpiScale = 1f;
            try { using (Graphics g = CreateGraphics()) dpiScale = g.DpiX / 96f; } catch { }
            if (dpiScale < 0.75f) dpiScale = 0.75f;

            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            // 留 6% 余量：保证 1480x940 的设计尺寸放大后仍能完整放进工作区
            float fit = Math.Min(wa.Width / (1480f * 1.06f), wa.Height / (940f * 1.06f));
            if (fit < 0.6f) fit = 0.6f;

            float s = Math.Min(dpiScale, fit);
            if (s > 3f) s = 3f;
            return s;
        }

        void ApplyUiScale()
        {
            if (_ui > 1.001f || _ui < 0.999f)
            {
                SuspendLayout();
                Scale(new SizeF(_ui, _ui));
                ResumeLayout(true);
            }
        }
        void ClampToScreen()
        {

            Rectangle wa = Screen.FromControl(this).WorkingArea;
            int w = Math.Min(Width, Math.Max(880, (int)(wa.Width * 0.94)));
            int h = Math.Min(Height, Math.Max(600, (int)(wa.Height * 0.92)));
            if (w != Width || h != Height)
            {
                Width = w; Height = h;
            }
            Left = Math.Max(wa.Left, wa.Left + (wa.Width - Width) / 2);
            Top = Math.Max(wa.Top, wa.Top + (wa.Height - Height) / 2);
            MinimumSize = new Size(Math.Min((int)(980 * _ui), (int)(wa.Width * 0.7)),
                                   Math.Min((int)(620 * _ui), (int)(wa.Height * 0.7)));
        }
        // ------------------------------------------------------------- settings
        void LoadSettingsToUi()
        {
            _txtOllamaUrl.Text = _cfg.OllamaUrl;
            _cbOllamaModel.Text = _cfg.OllamaModel;
            _txtApiBase.Text = _cfg.ApiBase;
            _txtApiModel.Text = _cfg.ApiModel;
            _rbOllama.Checked = _cfg.Backend == AiBackend.Ollama;
            _rbApi.Checked = _cfg.Backend == AiBackend.OpenAi;
            _numTemp.Value = (decimal)Math.Max(0, Math.Min(2, _cfg.Temperature));
            _numTokens.Value = Math.Max(128, Math.Min(8192, _cfg.MaxTokens));
            _txtPrompt.Text = _cfg.Prompt;
            _cbColor.SelectedIndex = Math.Max(0, Math.Min(3, _cfg.ColorMode));
            _numSize.Value = Math.Max(1, Math.Min(6, _cfg.PointSize));
            _cbBg.SelectedIndex = Math.Max(0, Math.Min(2, _cfg.Background));
            _chkGrid.Checked = _cfg.ShowGrid;
            _chkAxes.Checked = _cfg.ShowAxes;
            if (_cbThink != null) _cbThink.SelectedIndex = Math.Max(0, Math.Min(2, _cfg.ThinkMode));
            if (_numVoxel != null) _numVoxel.Value = (decimal)Math.Max(0.05, Math.Min(2.0, _cfg.SegVoxel));
            if (_numMinPts != null) _numMinPts.Value = Math.Max(5, Math.Min(5000, _cfg.SegMinPoints));
            if (_view != null) _view.DepthTestSelection = _cfg.DepthTest;
            if (_btnVis != null) _btnVis.BackColor = _cfg.DepthTest ? Color.FromArgb(210, 232, 255) : SystemColors.Control;
            if (_cbMeasureKind != null) _cbMeasureKind.SelectedIndex = Math.Max(0, Math.Min(2, _cfg.MeasureKind));
            if (_view != null) _view.Kind = (MeasureKind)Math.Max(0, Math.Min(2, _cfg.MeasureKind));
            if (_cfg.HasApiKey) _txtApiKey.Text = _cfg.GetApiKey();
            ApplyDisplayOptions();
            RefreshHistory();
        }

        void CollectSettings()
        {
            _cfg.Backend = _rbOllama.Checked ? AiBackend.Ollama : AiBackend.OpenAi;
            _cfg.OllamaUrl = _txtOllamaUrl.Text.Trim();
            _cfg.OllamaModel = _cbOllamaModel.Text.Trim();
            _cfg.ApiBase = _txtApiBase.Text.Trim();
            _cfg.ApiModel = _txtApiModel.Text.Trim();
            _cfg.Temperature = (double)_numTemp.Value;
            _cfg.MaxTokens = (int)_numTokens.Value;
            _cfg.Prompt = _txtPrompt.Text;
            _cfg.ColorMode = _cbColor.SelectedIndex;
            _cfg.PointSize = (int)_numSize.Value;
            _cfg.Background = _cbBg.SelectedIndex;
            _cfg.ShowGrid = _chkGrid.Checked;
            _cfg.ShowAxes = _chkAxes.Checked;
            if (_cbThink != null) _cfg.ThinkMode = Math.Max(0, _cbThink.SelectedIndex);
            if (_numVoxel != null) _cfg.SegVoxel = (double)_numVoxel.Value;
            if (_numMinPts != null) _cfg.SegMinPoints = (int)_numMinPts.Value;
            if (_view != null) _cfg.DepthTest = _view.DepthTestSelection;
            if (_cbMeasureKind != null) _cfg.MeasureKind = Math.Max(0, _cbMeasureKind.SelectedIndex);
            if (_chkRememberKey.Checked) _cfg.SetApiKey(_txtApiKey.Text.Trim());
            _cfg.Save();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            try { StopLive(); } catch { }
            try { CollectSettings(); } catch { }
            base.OnFormClosing(e);
        }

        // ------------------------------------------------------------- display
        void ApplyDisplayOptions()
        {
            if (_view == null || _cbColor == null || _cbBg == null || _numSize == null) return;
            RenderOptions o = new RenderOptions();
            switch (_cbColor.SelectedIndex)
            {
                case 1: o.Mode = ColorMode.Intensity; break;
                case 2: o.Mode = ColorMode.Range; break;
                case 3: o.Mode = ColorMode.Mono; break;
                default: o.Mode = ColorMode.Height; break;
            }
            o.PointSize = (int)_numSize.Value;
            if (_cbBg.SelectedIndex == 1) o.Background = Color.FromArgb(58, 60, 64);
            else if (_cbBg.SelectedIndex == 2) o.Background = Color.FromArgb(250, 250, 250);
            else o.Background = Color.FromArgb(16, 18, 22);
            o.ShowGrid = _chkGrid.Checked;
            o.ShowAxes = _chkAxes.Checked;
            o.MaxPoints = _cfg.MaxViewPoints;
            _view.Options = o;
            _view.RenderOptionsChanged();
        }

        void SetCloud(PointCloud pc)
        {
            _cloud = pc;
            _clusters = new List<ClusterInfo>();
            _sel = new List<int>();
            if (_view != null) _view.SetCloud(pc, true);
            if (_lblFile != null)
                _lblFile.Text = "当前：" + (pc.Count > 0 ? Path.GetFileName(pc.Source) + "  " + pc.Count.ToString("N0") + " 点" : "无数据");
            if (_lblStats != null) _lblStats.Text = pc.Count > 0 ? pc.StatsText() : "（尚未加载点云）";
            if (_lvClusters != null) _lvClusters.Items.Clear();
            if (_lblSeg != null) _lblSeg.Text = "（无选区）";
            UpdateButtons();
        }

        void OpenCloudDialog()
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "打开点云（CSV / TXT / PLY / ROS bag）";
                dlg.Filter = "点云文件|*.csv;*.txt;*.ply;*.bag|CSV|*.csv|文本|*.txt|PLY|*.ply|ROS bag|*.bag|所有文件|*.*";
                dlg.InitialDirectory = AppSettings.ExportsDir;
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (dlg.FileName.EndsWith(".bag", StringComparison.OrdinalIgnoreCase))
                {
                    OpenBag(dlg.FileName);
                    return;
                }
                try
                {
                    Cursor = Cursors.WaitCursor;
                    PointCloud pc = PointCloud.Load(dlg.FileName);
                    if (pc.Count == 0) { MessageBox.Show(this, "文件里没有解析到点。", "提示"); return; }
                    SetCloud(pc);
                    SetStatus("已加载 " + Path.GetFileName(dlg.FileName) + "，" + pc.Count.ToString("N0") + " 点");
                    AppendLog("加载：" + dlg.FileName + "  点数=" + pc.Count);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "读取失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                finally { Cursor = Cursors.Default; }
            }
        }

        void OpenBag(string path)
        {
            string topic;
            int start, count;
            try
            {
                using (BagImportForm dlg = new BagImportForm(path, _ui))
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    topic = dlg.SelectedTopic;
                    start = dlg.StartIndex;
                    count = dlg.FrameCount;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "无法读取 bag：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (string.IsNullOrEmpty(topic)) return;

            Cursor = Cursors.WaitCursor;
            SetStatus("正在从 bag 提取点云…");
            AppendLog(string.Format("bag 导入：{0}  topic={1}  start={2} count={3}", path, topic, start, count));

            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    PointCloud pc;
                    using (BagReader r = new BagReader(path))
                    {
                        r.Scan();
                        pc = r.Load(topic, start, count, delegate (int got, int total)
                        {
                            if (got == 1 || got % 25 == 0 || got == total)
                            {
                                int g2 = got, t2 = total;
                                BeginInvoke((MethodInvoker)delegate
                                {
                                    SetStatus(string.Format("bag 提取中 {0}/{1} 帧…", g2, t2));
                                });
                            }
                        });
                    }
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Cursor = Cursors.Default;
                        SetCloud(pc);
                        SetStatus(string.Format("已导入 {0:N0} 帧，{1:N0} 点", count, pc.Count));
                    });
                }
                catch (Exception ex)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Cursor = Cursors.Default;
                        SetStatus("bag 导入失败");
                        MessageBox.Show(this, "读取 bag 失败：" + ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                }
            });
        }

        void SaveCloudCsv()
        {
            if (_cloud == null || _cloud.Count == 0)
            {
                MessageBox.Show(this, "没有点云可保存。", "提示");
                return;
            }
            try
            {
                Directory.CreateDirectory(AppSettings.ExportsDir);
                string path = Path.Combine(AppSettings.ExportsDir, "cloud_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                _cloud.SaveCsv(path);
                SetStatus("已保存 " + path);
                AppendLog("保存 CSV：" + path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存失败：" + ex.Message, "错误");
            }
        }

        // ------------------------------------------------------------- live
        void StartLive()
        {
            if (_live.Start(_cfg.CapturePort))
            {
                _liveTimer.Interval = 400;
                _liveTimer.Tick += LiveTick;
                _liveTimer.Start();
                SetStatus("直采中：UDP " + _cfg.CapturePort + " —— 雷达应发往本机 192.168.1.2:6201");
                AppendLog("启动直采 UDP " + _cfg.CapturePort);
                UpdateButtons();
            }
            else
            {
                MessageBox.Show(this, _live.LastError, "直采启动失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                AppendLog("直采失败：" + _live.LastError);
            }
        }

        void StopLive()
        {
            _liveTimer.Stop();
            _liveTimer.Tick -= LiveTick;
            _live.Stop();
            SetStatus("直采已停止");
            AppendLog("停止直采");
            UpdateButtons();
        }

        void LiveTick(object sender, EventArgs e)
        {
            if (_lblLive == null) return;
            _lblLive.Text = string.Format("直采中：包 {0}，点 {1:N0}，坏包 {2}，缓存 {3:N0}",
                _live.PacketCount, _live.PointCount, _live.BadPackets, _live.BufferedPoints);
            if (_live.BufferedPoints > 0 && !_busy)
            {
                PointCloud snap = _live.Snapshot(60000);
                if (snap.Count > 0) SetCloud(snap);
            }
        }

        void GrabFrame()
        {
            if (!_live.Running)
            {
                MessageBox.Show(this, "先启动接收。", "提示");
                return;
            }
            PointCloud snap = _live.Snapshot(_cfg.MaxViewPoints);
            if (snap.Count == 0)
            {
                MessageBox.Show(this, "还没有收到点，检查网线、电脑 IP 192.168.1.2 与防火墙。", "提示");
                return;
            }
            SetCloud(snap);
            SetStatus("已抓帧 " + snap.Count.ToString("N0") + " 点，可直接分析");
            AppendLog("抓帧：" + snap.Count + " 点");
            try
            {
                Directory.CreateDirectory(AppSettings.ExportsDir);
                string path = Path.Combine(AppSettings.ExportsDir, "grab_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".csv");
                snap.SaveCsv(path);
                AppendLog("抓帧已存：" + path);
            }
            catch { }
        }

        // ------------------------------------------------------------- models
        void UpdateBackendPanels()
        {
            if (_rbOllama == null || _txtOllamaUrl == null || _lblVision == null) return;
            bool ollama = _rbOllama.Checked;
            _txtOllamaUrl.Enabled = ollama;
            _cbOllamaModel.Enabled = ollama;
            _btnRefreshModels.Enabled = ollama;
            _txtApiBase.Enabled = !ollama;
            _txtApiModel.Enabled = !ollama;
            _txtApiKey.Enabled = !ollama;
            _cbPreset.Enabled = !ollama;
            if (ollama && _cbOllamaModel.Text.Length == 0)
                _lblVision.Text = "先点“刷新”读取本地模型，再选视觉模型";
        }

        void ApplyPreset()
        {
            if (_txtApiBase == null || _txtApiModel == null || _cbPreset == null) return;
            switch (_cbPreset.SelectedIndex)
            {
                case 0: _txtApiBase.Text = "https://api.openai.com/v1"; _txtApiModel.Text = "gpt-4o-mini"; break;
                case 1: _txtApiBase.Text = "https://api.deepseek.com/v1"; _txtApiModel.Text = "deepseek-chat"; break;
                case 2: _txtApiBase.Text = "https://api.moonshot.cn/v1"; _txtApiModel.Text = "moonshot-v1-8k-vision-preview"; break;
                case 3: _txtApiBase.Text = "https://dashscope.aliyuncs.com/compatible-mode/v1"; _txtApiModel.Text = "qwen-vl-max"; break;
                default: break;
            }
        }

        void RefreshOllamaModels()
        {
            _btnRefreshModels.Enabled = false;
            SetStatus("正在读取 Ollama 模型列表…");
            string url = _txtOllamaUrl.Text.Trim();
            ThreadPool.QueueUserWorkItem(delegate
            {
                string err;
                List<string> names = AiClient.ListOllamaModels(url, out err);
                BeginInvoke((MethodInvoker)delegate
                {
                    _btnRefreshModels.Enabled = true;
                    if (err.Length > 0)
                    {
                        _lblVision.Text = "无法连接 Ollama：" + err;
                        AppendLog("Ollama 列表失败：" + err);
                        SetStatus("Ollama 连接失败");
                        return;
                    }
                    _cbOllamaModel.Items.Clear();
                    foreach (string n in names) _cbOllamaModel.Items.Add(n);
                    if (names.Count == 0)
                    {
                        _lblVision.Text = "没有已安装的模型（先 ollama pull llava 或 qwen2.5vl）";
                        AppendLog("Ollama 无模型");
                    }
                    else
                    {
                        if (_cbOllamaModel.Text.Length == 0) _cbOllamaModel.SelectedIndex = 0;
                        int vision = 0;
                        foreach (string n in names) if (IsVisionModel(n)) vision++;
                        _lblVision.Text = string.Format("共 {0} 个模型，其中疑似视觉模型 {1} 个", names.Count, vision);
                        AppendLog("Ollama 模型：" + string.Join(", ", names.ToArray()));
                    }
                    SetStatus("已刷新 Ollama 模型");
                });
            });
        }

        static bool IsVisionModel(string name)
        {
            string n = name.ToLowerInvariant();
            string[] keys = { "llava", "vision", "vl", "minicpm-v", "moondream", "bakllava", "internvl", "gemma3", "pixtral" };
            foreach (string k in keys) if (n.IndexOf(k, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // ------------------------------------------------------------- templates
        void FillTemplates()
        {
            _cbTemplate.Items.Clear();
            foreach (PromptTemplate t in _templates) _cbTemplate.Items.Add(t.Name);
            if (_cbTemplate.Items.Count > 0) _cbTemplate.SelectedIndex = 0;
        }

        void LoadSelectedTemplate()
        {
            if (_cbTemplate.SelectedIndex < 0 || _cbTemplate.SelectedIndex >= _templates.Count) return;
            _txtPrompt.Text = _templates[_cbTemplate.SelectedIndex].Text;
        }

        void SavePromptAsTemplate()
        {
            string name = PromptInput("为这套提示词起个名字：", "自定义 " + DateTime.Now.ToString("MM-dd HH:mm"));
            if (string.IsNullOrEmpty(name)) return;
            _templates.Add(new PromptTemplate { Name = name, Text = _txtPrompt.Text });
            PromptTemplates.Save(_templates);
            FillTemplates();
            _cbTemplate.SelectedIndex = _cbTemplate.Items.Count - 1;
            SetStatus("已保存提示词模板：" + name);
        }

        static string PromptInput(string title, string def)
        {
            using (Form f = new Form())
            {
                f.Text = title; f.Width = 420; f.Height = 150;
                f.StartPosition = FormStartPosition.CenterParent;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MinimizeBox = false; f.MaximizeBox = false;
                TextBox tb = new TextBox(); tb.Text = def; tb.Dock = DockStyle.Top;
                Button ok = new Button(); ok.Text = "确定"; ok.DialogResult = DialogResult.OK; ok.Dock = DockStyle.Bottom;
                f.Controls.Add(ok); f.Controls.Add(tb);
                return f.ShowDialog() == DialogResult.OK ? tb.Text.Trim() : null;
            }
        }

        // ------------------------------------------------------------- segmentation
        Control BuildSegPage()
        {
            TableLayoutPanel p = new TableLayoutPanel();
            p.Dock = DockStyle.Fill;
            p.ColumnCount = 1;
            p.RowCount = 4;
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            p.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            p.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));

            _lblSeg = new Label();
            _lblSeg.Dock = DockStyle.Fill;
            _lblSeg.Font = new Font("Consolas", 11.3f * _ui, GraphicsUnit.Pixel);
            _lblSeg.Text = "（无选区）\r\n框选：切到“框选”后在视图里拖矩形；测距：切到“测距”后点两个点。";
            p.Controls.Add(_lblSeg, 0, 0);

            TableLayoutPanel paramsRow = new TableLayoutPanel();
            paramsRow.Dock = DockStyle.Fill;
            paramsRow.ColumnCount = 6;
            paramsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            paramsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            paramsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            paramsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 68));
            paramsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            paramsRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
            paramsRow.Controls.Add(MkLabel("体素 m"), 0, 0);
            _numVoxel = new NumericUpDown();
            _numVoxel.DecimalPlaces = 2;
            _numVoxel.Increment = 0.05M;
            _numVoxel.Minimum = 0.05M;
            _numVoxel.Maximum = 2.0M;
            _numVoxel.Value = 0.20M;
            _numVoxel.Dock = DockStyle.Fill;
            paramsRow.Controls.Add(_numVoxel, 1, 0);
            paramsRow.Controls.Add(MkLabel("最小点"), 2, 0);
            _numMinPts = new NumericUpDown();
            _numMinPts.Minimum = 5;
            _numMinPts.Maximum = 5000;
            _numMinPts.Increment = 10;
            _numMinPts.Value = 30;
            _numMinPts.Dock = DockStyle.Fill;
            paramsRow.Controls.Add(_numMinPts, 3, 0);
            paramsRow.Controls.Add(MkLabel("越小越细"), 4, 0);
            p.Controls.Add(paramsRow, 0, 1);

            _lvClusters = new ListView();
            _lvClusters.Dock = DockStyle.Fill;
            _lvClusters.View = View.Details;
            _lvClusters.FullRowSelect = true;
            _lvClusters.HideSelection = false;
            _lvClusters.Columns.Add("#", 30);
            _lvClusters.Columns.Add("点数", 52);
            _lvClusters.Columns.Add("尺寸 W×D×H (m)", 128);
            _lvClusters.Columns.Add("中心 (m)", 128);
            _lvClusters.Columns.Add("最近 (m)", 52);
            _lvClusters.SelectedIndexChanged += delegate { OnClusterPicked(); };
            p.Controls.Add(_lvClusters, 0, 2);

            FlowLayoutPanel bar = new FlowLayoutPanel();
            bar.Dock = DockStyle.Fill;
            bar.WrapContents = false;
            Button run = MkTool("运行分割");
            Button exp = MkTool("导出该簇");
            Button clr = MkTool("清除高亮");
            run.Click += delegate { RunSegmentation(); };
            exp.Click += delegate { ExportCluster(); };
            clr.Click += delegate { ClearSelection(); };
            bar.Controls.Add(run);
            bar.Controls.Add(exp);
            bar.Controls.Add(clr);
            p.Controls.Add(bar, 0, 3);
            return p;
        }

        void RunSegmentation()
        {
            if (_cloud == null || _cloud.Count < 50)
            {
                MessageBox.Show(this, "先加载点云（至少几十个点）。", "提示");
                return;
            }
            double voxel = (double)(_numVoxel == null ? 0.20M : _numVoxel.Value);
            int minPoints = (int)(_numMinPts == null ? 30M : _numMinPts.Value);
            _usedVoxel = voxel;
            _usedMinPts = minPoints;
            PointCloud pc = _cloud;
            SetStatus(string.Format("正在分割（体素 {0:F2} m，最小 {1} 点）…", voxel, minPoints));
            Cursor = Cursors.WaitCursor;
            DateTime t0 = DateTime.Now;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    int[] ids;
                    List<ClusterInfo> list = CloudSegmenter.Segment(pc, voxel, minPoints, out ids);
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Cursor = Cursors.Default;
                        pc.Cluster = ids;
                        _clusters = list;
                        FillClusterList();
                        SetStatus(string.Format("分割完成：{0} 个簇，用时 {1:F2}s", list.Count, (DateTime.Now - t0).TotalSeconds));
                        AppendLog(string.Format("分割：{0} 簇（体素 {1:F2} m，最小 {2} 点，{3:F2}s）",
                            list.Count, voxel, minPoints, (DateTime.Now - t0).TotalSeconds));
                        if (list.Count > 0 && _lvClusters.Items.Count > 0) _lvClusters.Items[0].Selected = true;
                    });
                }
                catch (Exception ex)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        Cursor = Cursors.Default;
                        SetStatus("分割失败");
                        MessageBox.Show(this, "分割失败：" + ex.Message, "错误");
                    });
                }
            });
        }

        void FillClusterList()
        {
            if (_lvClusters == null) return;
            _lvClusters.BeginUpdate();
            _lvClusters.Items.Clear();
            foreach (ClusterInfo c in _clusters)
            {
                ListViewItem it = new ListViewItem(c.Id.ToString());
                it.SubItems.Add(c.Points.ToString("N0"));
                it.SubItems.Add(string.Format("{0:F2}×{1:F2}×{2:F2}", c.SizeX, c.SizeY, c.SizeZ));
                it.SubItems.Add(string.Format("({0:F2},{1:F2},{2:F2})", c.Cx, c.Cy, c.Cz));
                it.SubItems.Add(c.MinRange.ToString("F2"));
                it.Tag = c;
                _lvClusters.Items.Add(it);
            }
            _lvClusters.EndUpdate();
        }

        void OnClusterPicked()
        {
            if (_cloud == null || _lvClusters == null) return;
            if (_lvClusters.SelectedItems.Count == 0) return;
            ClusterInfo c = _lvClusters.SelectedItems[0].Tag as ClusterInfo;
            if (c == null) return;
            _cloud.SelectCluster(c.Id);
            if (_view != null) _view.Redraw();
            string text = string.Format(
                "簇 #{0}：{1:N0} 点\r\n尺寸：{2:F2} × {3:F2} × {4:F2} m\r\n中心：({5:F2}, {6:F2}, {7:F2})\r\n距离：最近 {8:F2} m / 平均 {9:F2} m / 最远 {10:F2} m",
                c.Id, c.Points, c.SizeX, c.SizeY, c.SizeZ, c.Cx, c.Cy, c.Cz, c.MinRange, c.MeanRange, c.MaxRange);
            if (_lblSeg != null) _lblSeg.Text = text;
            SetStatus(string.Format("簇 #{0}：{1:N0} 点，最近 {2:F2} m", c.Id, c.Points, c.MinRange));
        }

        void ExportCluster()
        {
            if (_cloud == null) return;
            int id = -1;
            if (_lvClusters != null && _lvClusters.SelectedItems.Count > 0)
            {
                ClusterInfo c = _lvClusters.SelectedItems[0].Tag as ClusterInfo;
                if (c != null) id = c.Id;
            }
            if (id < 0 && _sel != null && _sel.Count > 0)
            {
                SaveCloudSubset(_cloud.Subset(_sel, "selection"), "selection");
                return;
            }
            if (id < 0)
            {
                MessageBox.Show(this, "先在列表里选一个簇，或先框选。", "提示");
                return;
            }
            SaveCloudSubset(_cloud.ClusterSubset(id, "cluster_" + id), "cluster_" + id);
        }

        void ExportSelected()
        {
            if (_cloud == null) return;
            if (_sel == null || _sel.Count == 0)
            {
                ExportCluster();
                return;
            }
            SaveCloudSubset(_cloud.Subset(_sel, "selection"), "selection");
        }

        void SaveCloudSubset(PointCloud pc, string tag)
        {
            try
            {
                Directory.CreateDirectory(AppSettings.ExportsDir);
                string path = Path.Combine(AppSettings.ExportsDir,
                    string.Format("{0}_{1:yyyyMMdd_HHmmss}.csv", tag, DateTime.Now));
                pc.SaveCsv(path);
                SetStatus("已导出：" + path);
                AppendLog("导出 " + tag + "：" + path);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "导出失败：" + ex.Message, "错误");
            }
        }

        // ------------------------------------------------------------- analysis
        void StartAnalysis()
        {
            if (_busy) return;
            if (_cloud == null || _cloud.Count < 10)
            {
                MessageBox.Show(this, "先加载点云或抓帧。", "提示");
                return;
            }
            if (_rbOllama.Checked && _cbOllamaModel.Text.Trim().Length == 0)
            {
                MessageBox.Show(this, "请选择或填写 Ollama 模型名（点“刷新”读取本地模型列表）。", "提示");
                return;
            }
            if (_rbApi.Checked && (_txtApiModel.Text.Trim().Length == 0 || _txtApiBase.Text.Trim().Length == 0))
            {
                MessageBox.Show(this, "请填写 BaseURL 与模型名。", "提示");
                return;
            }

            CollectSettings();
            _busy = true;
            lock (_streamLock) _stream.Clear();
            _txtNl.Text = "";
            _txtJson.Text = "";
            _txtReport.Text = "";
            _tabs.SelectedIndex = 0;
            UpdateButtons();
            SetStatus("正在生成视图并请求模型…");
            _stProgress.Visible = true;
            _stProgress.Style = ProgressBarStyle.Marquee;

            AiRequest req = new AiRequest();
            req.Backend = _rbOllama.Checked ? AiBackend.Ollama : AiBackend.OpenAi;
            req.BaseUrl = req.Backend == AiBackend.Ollama ? _txtOllamaUrl.Text.Trim() : _txtApiBase.Text.Trim();
            req.ApiKey = _txtApiKey.Text.Trim();
            req.Model = req.Backend == AiBackend.Ollama ? _cbOllamaModel.Text.Trim() : _txtApiModel.Text.Trim();
            req.Temperature = (double)_numTemp.Value;
            req.MaxTokens = (int)_numTokens.Value;
            req.UserPrompt = BuildUserPrompt();
            req.SystemPrompt = "你是严谨的 3D 点云分析助手，只根据给定的统计和视图作答，宁可说无法判断也不要编造。";
            req.Thinking = (ThinkMode)Math.Max(0, Math.Min(2, _cbThink.SelectedIndex));
            lock (_thinkLock) _thinking.Length = 0;
            _txtThink.Text = req.Thinking == ThinkMode.Off
                ? "（已关闭思考链：请求带 think=false，模型不产生推理内容）"
                : "（等待模型输出思考内容…）";
            _lastThinkUi = DateTime.MinValue;
            req.OnThinkingDelta = delegate (string th)
            {
                lock (_thinkLock) _thinking.Append(th);
                if ((DateTime.Now - _lastThinkUi).TotalMilliseconds > 250)
                {
                    _lastThinkUi = DateTime.Now;
                    BeginInvoke((MethodInvoker)delegate
                    {
                        string snapshot;
                        lock (_thinkLock) snapshot = _thinking.ToString();
                        if (_txtThink.Text != snapshot)
                        {
                            _txtThink.Text = snapshot;
                            _txtThink.SelectionStart = _txtThink.TextLength;
                            _txtThink.ScrollToCaret();
                        }
                    });
                }
            };
            if (req.Thinking == ThinkMode.On && _numTokens.Value < 4096)
                AppendLog("提示：思考链开启时建议 Tokens ≥ 4096，当前 " + _numTokens.Value);

            Dictionary<string, Bitmap> views = null;
            if (_chkSendImages.Checked)
            {
                RenderOptions o = _view.Options != null ? _view.Options : new RenderOptions();
                int w = Math.Max(480, _cfg.ViewSize), h = (int)(w * 0.75);
                try
                {
                    views = CloudRenderer.RenderThreeViews(_cloud, o, w, h);
                    req.ImagesB64 = EncodeImages(views);
                    AppendLog(string.Format("已渲染三视图 {0}x{1}，图片 {2} 张", w, h, req.ImagesB64.Count));
                }
                catch (Exception ex)
                {
                    AppendLog("渲染失败：" + ex.Message);
                }
            }

            AppendLog(string.Format("分析请求：{0} / {1}，点数 {2}", req.Backend, req.Model, _cloud.Count));
            _stLabel.Text = "分析中…（模型：" + req.Model + "）";

            DateTime t0 = DateTime.Now;
            _ai.Start(req,
                delegate (string delta)
                {
                    lock (_streamLock) _stream.Append(delta);
                    BeginInvoke((MethodInvoker)delegate
                    {
                        string snapshot;
                        lock (_streamLock) snapshot = _stream.ToString();
                        if (snapshot.Length > 400000) snapshot = snapshot.Substring(snapshot.Length - 400000);
                        _txtNl.Text = snapshot;
                        _txtNl.SelectionStart = _txtNl.TextLength;
                        _txtNl.ScrollToCaret();
                    });
                },
                delegate (string full)
                {
                    double sec = (DateTime.Now - t0).TotalSeconds;
                    BeginInvoke((MethodInvoker)delegate { FinishAnalysis(full, views, sec, req); });
                },
                delegate (string err)
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        _busy = false;
                        _stProgress.Visible = false;
                        UpdateButtons();
                        SetStatus("分析失败");
                        _tabs.SelectedIndex = 6;
                        AppendLog("错误：" + err);
                        MessageBox.Show(this, err, "模型调用失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    });
                });
        }

        List<string> EncodeImages(Dictionary<string, Bitmap> views)
        {
            List<string> list = new List<string>();
            string[] order = { "iso", "top", "front" };
            foreach (string k in order)
            {
                if (!views.ContainsKey(k)) continue;
                using (MemoryStream ms = new MemoryStream())
                {
                    views[k].Save(ms, ImageFormat.Png);
                    list.Add(Convert.ToBase64String(ms.ToArray()));
                }
            }
            return list;
        }

        string BuildUserPrompt()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine(_txtPrompt.Text.Trim());
            sb.AppendLine();
            sb.AppendLine("======== 客观统计（由程序计算，单位米）========");
            sb.AppendLine(_cloud.StatsText());
            if (_clusters != null && _clusters.Count > 0)
            {
                sb.AppendLine("======== 分割结果（体素聚类）========");
                sb.AppendLine(string.Format("共 {0} 个簇（体素 {1:F2} m，最小 {2} 点）。其中最大的几个：",
                    _clusters.Count, _usedVoxel, _usedMinPts));
                int show = Math.Min(6, _clusters.Count);
                for (int k = 0; k < show; k++)
                {
                    ClusterInfo c = _clusters[k];
                    sb.AppendLine(string.Format("#{0}  {1:N0} 点  尺寸 {2:F2}×{3:F2}×{4:F2} m  中心({5:F2},{6:F2},{7:F2})  最近 {8:F2} m",
                        c.Id, c.Points, c.SizeX, c.SizeY, c.SizeZ, c.Cx, c.Cy, c.Cz, c.MinRange));
                }
                sb.AppendLine("（可据此判断物体数量、大小与远近；注意多帧合并会让相邻物体连成一簇。）");
            }
            if (_sel != null && _sel.Count > 0)
            {
                ClusterInfo s = CloudSegmenter.Describe(_cloud, _sel, -1);
                sb.AppendLine("======== 用户框选区域 ========");
                sb.AppendLine(string.Format("{0:N0} 点，尺寸 {1:F2}×{2:F2}×{3:F2} m，中心({4:F2},{5:F2},{6:F2})，最近 {7:F2} m",
                    s.Points, s.SizeX, s.SizeY, s.SizeZ, s.Cx, s.Cy, s.Cz, s.MinRange));
            }

            sb.AppendLine("======== 视图说明 ========");
            if (_chkSendImages.Checked)
                sb.AppendLine("附图顺序：1=等轴测(ISO)，2=俯视(TOP，从上往下看，屏幕右=+X、屏幕下=+Y)，3=正视(FRONT，从 -Y 方向看)。背景为深色，颜色由蓝到红表示高度/强度/距离（按当前设置）。");
            else
                sb.AppendLine("本次未附带图片，仅根据统计数字作答。");
            sb.AppendLine("======== 数据来源 ========");
            sb.AppendLine("文件：" + Path.GetFileName(_cloud.Source));
            return sb.ToString();
        }

        void FinishAnalysis(string full, Dictionary<string, Bitmap> views, double seconds, AiRequest req)
        {
            _busy = false;
            _stProgress.Visible = false;
            UpdateButtons();

            string thinkingText;
            lock (_thinkLock) thinkingText = _thinking.ToString();
            if (req.Thinking == ThinkMode.Off)
            {
                if (_txtThink.Text.Trim().Length == 0 || _txtThink.Text.StartsWith("（"))
                    _txtThink.Text = "（已关闭思考链：请求带 think=false，模型不产生推理内容）";
            }
            else if (thinkingText.Trim().Length > 0)
            {
                _txtThink.Text = thinkingText;
                AppendLog(string.Format("思考内容 {0} 字符，已显示在“思考”页签", thinkingText.Length));
            }
            else
            {
                _txtThink.Text = "（模型本次没有输出思考内容）";
            }


            if (string.IsNullOrEmpty(full) || full.Trim().Length == 0)
            {
                if (thinkingText.Trim().Length > 0)
                {
                    full = "[模型只输出了思考内容，最终答复为空 —— 已回退展示思考过程]\r\n\r\n" + thinkingText;
                    AppendLog(string.Format("警告：模型 content 为空（thinking {0} 字符），已回退为思考内容", thinkingText.Length));
                }
                else
                {
                    SetStatus("模型返回空内容");
                    _tabs.SelectedIndex = 6;
                    AppendLog("错误：模型返回空内容（content 与 thinking 均为空）");
                    MessageBox.Show(this,
                        "模型返回了空内容。\r\n\r\n" +
                        "最常见原因：思考型模型（qwen3 / deepseek-r1 等）把 token 预算全用在思考上。\r\n" +
                        "处理办法：勾选「关闭思考链 (think=false)」，或把 Tokens 调大到 2048 以上。",
                        "空答复", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            string prose, json;
            SplitAnswer(full, out prose, out json);
            _txtNl.Text = prose.Length > 0 ? prose : full;
            _txtJson.Text = json.Length > 0 ? Prettify(json) : "（模型没有返回 ```json 代码块；可修改提示词要求严格输出 JSON）";

            string report = BuildReport(prose.Length > 0 ? prose : full, json, seconds, req);
            _txtReport.Text = report;

            try
            {
                string dir = HistoryStore.Save(_cloud.Source, req.Model,
                    req.Backend == AiBackend.Ollama ? "ollama" : "api",
                    report, full, _cloud.Count, views);
                AppendLog("报告与截图已保存：" + dir);
                if (thinkingText.Length > 0)
                {
                    try { File.WriteAllText(Path.Combine(dir, "thinking.txt"), thinkingText, new UTF8Encoding(false)); }
                    catch { }
                }
                RefreshHistory();
            }
            catch (Exception ex)
            {
                AppendLog("保存历史失败：" + ex.Message);
            }

            SetStatus(string.Format("完成：{0} 点，用时 {1:F1}s，模型 {2}", _cloud.Count, seconds, req.Model));
            _stLabel.Text = _stLabel.Text.Replace("分析中…", "完成");
            _tabs.SelectedIndex = 0;
        }

        static void SplitAnswer(string text, out string prose, out string json)
        {
            prose = text.Trim();
            json = "";
            int start = text.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
            {
                int body = start + 7;
                int end = text.IndexOf("```", body, StringComparison.Ordinal);
                if (end > body)
                {
                    json = text.Substring(body, end - body).Trim();
                    prose = (text.Substring(0, start) + text.Substring(end + 3)).Trim();
                    return;
                }
            }
            int lb = text.IndexOf('{');
            int rb = text.LastIndexOf('}');
            if (lb >= 0 && rb > lb && text.IndexOf("```", StringComparison.Ordinal) < 0)
            {
                string cand = text.Substring(lb, rb - lb + 1).Trim();
                if (cand.Length > 20 && cand.IndexOf("\"summary\"", StringComparison.Ordinal) >= 0)
                {
                    json = cand;
                    prose = (text.Substring(0, lb) + text.Substring(rb + 1)).Trim();
                }
            }
        }

        static string Prettify(string json)
        {
            try
            {
                object o = Json.Parse(json);
                return JsonPretty.Format(o);
            }
            catch
            {
                return json;
            }
        }

        string BuildReport(string prose, string json, double seconds, AiRequest req)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# L2 点云分析报告");
            sb.AppendLine();
            sb.AppendLine("| 项目 | 值 |");
            sb.AppendLine("|---|---|");
            sb.AppendLine("| 生成时间 | " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " |");
            sb.AppendLine("| 数据文件 | " + Path.GetFileName(_cloud.Source) + " |");
            sb.AppendLine("| 点数 | " + _cloud.Count.ToString("N0") + " |");
            sb.AppendLine("| 模型 | " + (req.Backend == AiBackend.Ollama ? "Ollama / " : "API / ") + req.Model + " |");
            sb.AppendLine("| 耗时 | " + seconds.ToString("F1") + " s |");
            sb.AppendLine();
            sb.AppendLine("## 点云统计");
            sb.AppendLine();
            sb.AppendLine("| 指标 | 数值 |");
            sb.AppendLine("|---|---|");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| 包围盒 X | {0:F3} … {1:F3} m |", _cloud.MinX, _cloud.MaxX));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| 包围盒 Y | {0:F3} … {1:F3} m |", _cloud.MinY, _cloud.MaxY));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| 包围盒 Z | {0:F3} … {1:F3} m |", _cloud.MinZ, _cloud.MaxZ));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| 尺寸(宽×深×高) | {0:F3} × {1:F3} × {2:F3} m |", _cloud.SizeX, _cloud.SizeY, _cloud.SizeZ));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| 质心 | ({0:F3}, {1:F3}, {2:F3}) |", _cloud.Cx, _cloud.Cy, _cloud.Cz));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "| 距离 平均/最近/最远 | {0:F3} / {1:F3} / {2:F3} m |", _cloud.MeanRange, _cloud.MinRange, _cloud.MaxRange));
            sb.AppendLine();
            sb.AppendLine("## 分析结论");
            sb.AppendLine();
            sb.AppendLine(prose.Trim());
            sb.AppendLine();
            if (json.Length > 0)
            {
                sb.AppendLine("## 结构化结果");
                sb.AppendLine();
                sb.AppendLine("```json");
                sb.AppendLine(json);
                sb.AppendLine("```");
                sb.AppendLine();
            }
            sb.AppendLine("---");
            sb.AppendLine("由 L2PointAI 生成；三视图截图见同目录 views/。");
            return sb.ToString();
        }

        void StopAnalysis()
        {
            if (!_busy) return;
            _ai.Cancel();
            _busy = false;
            _stProgress.Visible = false;
            UpdateButtons();
            SetStatus("已停止");
            AppendLog("用户停止了分析");
        }

        void SaveReport()
        {
            if (_txtReport.Text.Trim().Length == 0)
            {
                MessageBox.Show(this, "还没有报告。", "提示");
                return;
            }
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Filter = "Markdown|*.md|文本|*.txt";
                dlg.FileName = "L2_report_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".md";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    File.WriteAllText(dlg.FileName, _txtReport.Text, new UTF8Encoding(false));
                    SetStatus("报告已保存：" + dlg.FileName);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "保存失败"); }
            }
        }

        void CopyConclusion()
        {
            if (_txtNl.Text.Length == 0) return;
            try { Clipboard.SetText(_txtNl.Text); SetStatus("结论已复制到剪贴板"); } catch { }
        }

        // ------------------------------------------------------------- history
        void RefreshHistory()
        {
            _lstHistory.Items.Clear();
            foreach (HistoryEntry e in HistoryStore.List(200)) _lstHistory.Items.Add(e);
            if (_lstHistory.Items.Count > 0) _lstHistory.SelectedIndex = 0;
        }

        void OpenHistoryReport()
        {
            HistoryEntry e = _lstHistory.SelectedItem as HistoryEntry;
            if (e == null) return;
            string path = Path.Combine(e.Dir, "report.md");
            try
            {
                if (File.Exists(path))
                {
                    _txtReport.Text = File.ReadAllText(path, Encoding.UTF8);
                    _tabs.SelectedIndex = 2;
                }
                else MessageBox.Show(this, "报告文件不存在。", "提示");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "读取失败"); }
        }

        void DeleteHistory()
        {
            HistoryEntry e = _lstHistory.SelectedItem as HistoryEntry;
            if (e == null) return;
            if (MessageBox.Show(this, "删除这条记录？\n" + e.Dir, "确认", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try
            {
                Directory.Delete(e.Dir, true);
                RefreshHistory();
                SetStatus("已删除");
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "删除失败"); }
        }

        // ------------------------------------------------------------- misc
        void UpdateButtons()
        {
            bool hasData = _cloud != null && _cloud.Count > 0;
            _btnAnalyze.Enabled = !_busy && hasData;
            _btnStop.Enabled = _busy;
            _btnSaveCsv.Enabled = hasData;
            _btnGrab.Enabled = _live.Running;
            _btnLiveStart.Enabled = !_live.Running;
            _btnLiveStop.Enabled = _live.Running;
            _btnSaveReport.Enabled = !_busy && _txtReport != null && _txtReport.Text.Trim().Length > 0;
            _btnCopy.Enabled = _txtNl != null && _txtNl.Text.Length > 0;
        }

        void SetStatus(string s)
        {
            if (_stLabel == null) return;
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { SetStatus(s); }); return; }
            _stLabel.Text = s;
        }

        void AppendLog(string s)
        {
            if (InvokeRequired) { BeginInvoke((MethodInvoker)delegate { AppendLog(s); }); return; }
            _txtLog.AppendText(string.Format("[{0:HH:mm:ss}] {1}{2}", DateTime.Now, s, Environment.NewLine));
            if (_txtLog.TextLength > 200000) _txtLog.Text = _txtLog.Text.Substring(_txtLog.TextLength - 100000);
        }

        static void OpenFolder(string dir)
        {
            try
            {
                Directory.CreateDirectory(dir);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }

        void ShowAbout()
        {
            MessageBox.Show(this,
                "L2 点云 AI 分析器\n\n" +
                "数据：Unitree 4D LiDAR L2（UDP 6201）导出文件或实时直采\n" +
                "模型：本地 Ollama（/api/chat）或任意 OpenAI 兼容接口\n" +
                "渲染：CPU z-buffer 软件光栅化\n\n" +
                "API Key 使用 Windows DPAPI 加密，仅当前用户可解。",
                "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public void DebugSelectBackend(bool ollama)
        {
            if (ollama) _rbOllama.Checked = true; else _rbApi.Checked = true;
            UpdateBackendPanels();
        }

        public void DebugSegmentNow()
        {
            if (_cloud == null || _cloud.Count < 50) return;
            int[] ids;
            List<ClusterInfo> list = CloudSegmenter.Segment(_cloud, 0.2, 30, out ids);
            _cloud.Cluster = ids;
            _clusters = list;
            FillClusterList();
            if (list.Count > 0 && _lvClusters.Items.Count > 0) _lvClusters.Items[0].Selected = true;
        }

        /// <summary>Debug helper: feed the stream buffer from a worker thread (reproduces the old race).</summary>
        public void DebugFakeStream(int chunks, int chunkLen)
        {
            StringBuilder payload = new StringBuilder();
            for (int i = 0; i < chunkLen; i++) payload.Append('测');
            string piece = payload.ToString();
            _busy = true;
            lock (_streamLock) _stream.Clear();
            ThreadPool.QueueUserWorkItem(delegate
            {
                for (int i = 0; i < chunks; i++)
                {
                    lock (_streamLock) _stream.Append(piece);
                    BeginInvoke((MethodInvoker)delegate
                    {
                        string snapshot;
                        lock (_streamLock) snapshot = _stream.ToString();
                        if (snapshot.Length > 400000) snapshot = snapshot.Substring(snapshot.Length - 400000);
                        _txtNl.Text = snapshot;
                    });
                }
                BeginInvoke((MethodInvoker)delegate { _busy = false; AppendLog("streamtest done: " + chunks + " chunks"); });
            });
        }
        public string DebugBuildPrompt() { return BuildUserPrompt(); }

        public void DebugSelectTab(int index)
        {
            if (_tabs != null && index >= 0 && index < _tabs.TabPages.Count) _tabs.SelectedIndex = index;
        }

        public void DebugLoadCloud(string path)
        {
            try
            {
                PointCloud pc;
                if (path.EndsWith(".bag", StringComparison.OrdinalIgnoreCase))
                {
                    using (BagReader r = new BagReader(path))
                    {
                        BagInfo info = r.Scan();
                        BagTopic t = info.FirstCloudTopic();
                        if (t == null) return;
                        pc = r.Load(t.Topic, 0, 1, null);
                    }
                }
                else
                {
                    pc = PointCloud.Load(path);
                }
                if (pc.Count > 0) SetCloud(pc);
            }
            catch { }
        }

        // exposed for the view control
        public Camera Cam { get { return _cam; } }
        public PointCloud Cloud { get { return _cloud; } }
    }

    // ---------------------------------------------------------------- 3D view
    public enum ViewMode { Rotate = 0, Select = 1, Measure = 2 }

    public sealed class PointCloudView : Control
    {
        readonly MainForm _owner;
        PointCloud _cloud;
        Camera _cam = new Camera();
        RenderOptions _opt = new RenderOptions();
        Bitmap _cache;
        bool _dragging, _panning;
        Point _last;

        ViewMode _mode = ViewMode.Rotate;
        public float UiScale = 1f;
        bool _recting;
        Point _rectStart;
        Rectangle _rect;
        readonly List<double[]> _measurePts = new List<double[]>();

        public event Action<List<int>> SelectionChanged;
        public event Action<MeasureResult> Measured;

        MeasureKind _kind = MeasureKind.Distance3D;
        public MeasureKind Kind { get { return _kind; } set { _kind = value; } }
        public bool DepthTestSelection = true;

        public PointCloudView(MainForm owner)
        {
            _owner = owner;
            Dock = DockStyle.Fill;
            BackColor = Color.FromArgb(16, 18, 22);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Resize += delegate { _cache = null; Invalidate(); };
            MouseDown += OnDown;
            MouseMove += OnMove;
            MouseUp += OnUp;
            MouseWheel += OnWheel;
            MouseDoubleClick += delegate { ResetView(); };
        }

        public RenderOptions Options { get { return _opt; } set { _opt = value ?? new RenderOptions(); } }

        public ViewMode Mode
        {
            get { return _mode; }
            set
            {
                _mode = value;
                Cursor = value == ViewMode.Rotate ? Cursors.Default : (value == ViewMode.Select ? Cursors.Cross : Cursors.Hand);
                _recting = false;
                _rect = Rectangle.Empty;
                Invalidate();
            }
        }

        public void SetCloud(PointCloud pc, bool fit)
        {
            _cloud = pc;
            _measurePts.Clear();
            _opt.Measure = null;
            if (fit && pc != null && pc.Count > 0) CloudRenderer.Fit(pc, _cam);
            _cache = null;
            Invalidate();
        }

        public void ResetView()
        {
            if (_cloud != null && _cloud.Count > 0) CloudRenderer.Fit(_cloud, _cam);
            else { _cam.Yaw = 0.65; _cam.Pitch = 1.13; _cam.Dist = 8; }
            _cache = null;
            Invalidate();
        }

        public void RenderOptionsChanged()
        {
            _cache = null;
            Invalidate();
        }

        public void Redraw()
        {
            _cache = null;
            Invalidate();
        }

        // ------------------------------------------------------------ projection
        bool Project(double x, double y, double z, out double sx, out double sy, out double depth)
        {
            sx = sy = depth = 0;
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w < 8 || h < 8) return false;
            double f = 0.5 * h / Math.Tan(55.0 * Math.PI / 360.0);
            double cy = Math.Cos(_cam.Yaw), sy0 = Math.Sin(_cam.Yaw);
            double cp = Math.Cos(_cam.Pitch), sp = Math.Sin(_cam.Pitch);
            double dx = x - _cam.Tx, dy = y - _cam.Ty, dz = z - _cam.Tz;
            double x1 = dx * cy + dy * sy0;
            double y1 = -dx * sy0 + dy * cy;
            double y2 = y1 * cp - dz * sp;
            double z2 = y1 * sp + dz * cp;
            depth = _cam.Dist - z2;
            if (depth < 0.02) return false;
            sx = w * 0.5 + f * x1 / depth;
            sy = h * 0.5 - f * y2 / depth;
            return true;
        }

        // ------------------------------------------------------------ mouse
        void OnDown(object s, MouseEventArgs e)
        {
            if (_cloud == null || _cloud.Count == 0) return;
            _last = e.Location;
            if (_mode == ViewMode.Rotate)
            {
                _dragging = e.Button == MouseButtons.Left;
                _panning = e.Button == MouseButtons.Right;
                if (_dragging || _panning) { _cache = null; Invalidate(); }
                return;
            }
            if (_mode == ViewMode.Select && e.Button == MouseButtons.Left)
            {
                _recting = true;
                _rectStart = e.Location;
                _rect = Rectangle.Empty;
            }
            else if (_mode == ViewMode.Measure && e.Button == MouseButtons.Left)
            {
                PickPoint(e.Location);
            }
        }

        void OnMove(object s, MouseEventArgs e)
        {
            if (_recting)
            {
                _rect = Rectangle.FromLTRB(
                    Math.Min(_rectStart.X, e.X), Math.Min(_rectStart.Y, e.Y),
                    Math.Max(_rectStart.X, e.X), Math.Max(_rectStart.Y, e.Y));
                Invalidate();
                return;
            }
            if (!_dragging && !_panning) return;
            int dx = e.X - _last.X, dy = e.Y - _last.Y;
            _last = e.Location;
            if (_dragging)
            {
                _cam.Yaw -= dx * 0.008;
                _cam.Pitch += dy * 0.008;
                if (_cam.Pitch > 1.55) _cam.Pitch = 1.55;
                if (_cam.Pitch < -1.55) _cam.Pitch = -1.55;
            }
            else
            {
                double f = 0.5 * ClientSize.Height / Math.Tan(55.0 * Math.PI / 360.0);
                double k = _cam.Dist / Math.Max(f, 1);
                double cy = Math.Cos(_cam.Yaw), sy = Math.Sin(_cam.Yaw);
                double cp = Math.Cos(_cam.Pitch), sp = Math.Sin(_cam.Pitch);
                _cam.Tx -= (dx * cy) * k;
                _cam.Ty -= (dx * sy) * k;
                _cam.Tx -= (-sy * sp) * dy * k;
                _cam.Ty -= (cy * sp) * dy * k;
                _cam.Tz += cp * dy * k;
            }
            _cache = null;
            Invalidate();
        }

        void OnUp(object s, MouseEventArgs e)
        {
            if (_recting)
            {
                _recting = false;
                Rectangle r = _rect;
                _rect = Rectangle.Empty;
                if (r.Width > 3 && r.Height > 3) ApplyRectSelection(r);
                Invalidate();
                return;
            }
            _dragging = _panning = false;
            _cache = null;
            Invalidate();
        }

        void OnWheel(object s, MouseEventArgs e)
        {
            _cam.Dist *= Math.Exp(-e.Delta * 0.0012);
            if (_cam.Dist < 0.05) _cam.Dist = 0.05;
            if (_cam.Dist > 1e5) _cam.Dist = 1e5;
            _cache = null;
            Invalidate();
        }

        // ------------------------------------------------------------ actions
        void ApplyRectSelection(Rectangle r)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (w < 16 || h < 16) return;

            List<int> candidates = new List<int>();
            List<float> depths = new List<float>();
            List<int> pixels = new List<int>();
            float[] zbuf = DepthTestSelection ? new float[w * h] : null;
            if (zbuf != null) for (int k = 0; k < zbuf.Length; k++) zbuf[k] = float.MaxValue;

            double sx, sy, depth;
            for (int i = 0; i < _cloud.Count; i++)
            {
                if (!Project(_cloud.X[i], _cloud.Y[i], _cloud.Z[i], out sx, out sy, out depth)) continue;
                if (sx < r.Left || sx > r.Right || sy < r.Top || sy > r.Bottom) continue;
                int px = (int)sx, py = (int)sy;
                if (px < 0 || py < 0 || px >= w || py >= h) continue;
                int pidx = py * w + px;
                candidates.Add(i);
                depths.Add((float)depth);
                pixels.Add(pidx);
                if (zbuf != null && depth < zbuf[pidx]) zbuf[pidx] = (float)depth;
            }

            List<int> hits = new List<int>(candidates.Count);
            for (int k = 0; k < candidates.Count; k++)
            {
                if (zbuf == null)
                {
                    hits.Add(candidates[k]);
                    continue;
                }
                double eps = 0.02 + depths[k] * 0.01;
                if (depths[k] <= zbuf[pixels[k]] + eps) hits.Add(candidates[k]);
            }

            byte[] sel = new byte[_cloud.Count];
            foreach (int i in hits) sel[i] = 1;
            _cloud.Selected = hits.Count > 0 ? sel : null;
            _cache = null;
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(hits);
        }

        void PickPoint(Point p)
        {
            double best = 14.0 * 14.0;
            int bestIdx = -1;
            double bestDepth = double.MaxValue;
            double sx, sy, depth;
            for (int i = 0; i < _cloud.Count; i++)
            {
                if (!Project(_cloud.X[i], _cloud.Y[i], _cloud.Z[i], out sx, out sy, out depth)) continue;
                double dx = sx - p.X, dy = sy - p.Y;
                double d2 = dx * dx + dy * dy;
                if (d2 < best || (d2 < best * 1.5 && depth < bestDepth))
                {
                    best = Math.Min(best, d2);
                    bestDepth = depth;
                    bestIdx = i;
                }
            }
            if (bestIdx < 0)
            {
                if (Measured != null) Measured(null);
                return;
            }

            _measurePts.Add(new double[] { _cloud.X[bestIdx], _cloud.Y[bestIdx], _cloud.Z[bestIdx], bestIdx });
            if (_measurePts.Count > 2) _measurePts.RemoveAt(0);

            if (_measurePts.Count < 2)
            {
                double[] only = _measurePts[0];
                MeasureResult first = new MeasureResult();
                first.Mode = -1;
                first.Ax = only[0]; first.Ay = only[1]; first.Az = only[2];
                if (Measured != null) Measured(first);
                _opt.Measure = null;
                _cache = null;
                Invalidate();
                return;
            }

            double[] a = _measurePts[0], b = _measurePts[1];
            MeasureResult res = new MeasureResult();
            res.Mode = (int)_kind;
            res.Ax = a[0]; res.Ay = a[1]; res.Az = a[2];
            res.Bx = b[0]; res.By = b[1]; res.Bz = b[2];
            res.Dx = b[0] - a[0]; res.Dy = b[1] - a[1]; res.Dz = b[2] - a[2];

            if (_kind == MeasureKind.Horizontal)
            {
                res.Distance = Math.Sqrt(res.Dx * res.Dx + res.Dy * res.Dy);
                res.Label = string.Format("水平 {0:F3} m  (ΔX {1:F3}  ΔY {2:F3})", res.Distance, res.Dx, res.Dy);
            }
            else if (_kind == MeasureKind.PointToPlane)
            {
                double cx, cy, cz, nx, ny, nz;
                int used;
                if (PlaneFit.Around(_cloud, (int)a[3], 0.4, out cx, out cy, out cz, out nx, out ny, out nz, out used))
                {
                    // signed distance of B from plane (point A on the plane)
                    double sd = (b[0] - cx) * nx + (b[1] - cy) * ny + (b[2] - cz) * nz;
                    res.Distance = Math.Abs(sd);
                    res.Nx = nx; res.Ny = ny; res.Nz = nz;
                    res.PlanePoints = used;
                    res.Label = string.Format("点-面 {0:F3} m  (法向 {1:F2},{2:F2},{3:F2})", res.Distance, nx, ny, nz);
                }
                else
                {
                    res.Distance = Math.Sqrt(res.Dx * res.Dx + res.Dy * res.Dy + res.Dz * res.Dz);
                    res.Label = string.Format("拟合失败，退回 3D 距离 {0:F3} m", res.Distance);
                }
            }
            else
            {
                res.Distance = Math.Sqrt(res.Dx * res.Dx + res.Dy * res.Dy + res.Dz * res.Dz);
                res.Label = string.Format("{0:F3} m  (ΔX {1:F3}  ΔY {2:F3}  ΔZ {3:F3})", res.Distance, res.Dx, res.Dy, res.Dz);
            }

            _opt.Measure = new double[] { a[0], a[1], a[2], b[0], b[1], b[2] };
            _opt.MeasureLabel = res.Label;
            if (Measured != null) Measured(res);
            _cache = null;
            Invalidate();
        }

        // ------------------------------------------------------------ paint
        protected override void OnPaint(PaintEventArgs e)
        {
            int w = ClientSize.Width, h = ClientSize.Height;
            if (_cloud == null || _cloud.Count == 0)
            {
                using (SolidBrush bg = new SolidBrush(_opt.Background))
                    e.Graphics.FillRectangle(bg, ClientRectangle);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(120, 130, 145)))
                using (Font f = new Font("Microsoft YaHei UI", 15f * UiScale, GraphicsUnit.Pixel))
                {
                    e.Graphics.DrawString("打开点云文件，或启动直采后抓帧", f, b, 24, 24);
                }
                return;
            }
            if (w < 16) w = 16;
            if (h < 16) h = 16;
            if (_cache == null || _cache.Width != w || _cache.Height != h)
            {
                int drawn;
                _cache = CloudRenderer.Render(_cloud, _cam, w, h, _opt, out drawn);
            }
            e.Graphics.DrawImageUnscaled(_cache, 0, 0);

            if (_recting && _rect.Width > 0 && _rect.Height > 0)
            {
                using (Pen pen = new Pen(Color.FromArgb(90, 200, 255), 1f))
                using (SolidBrush fill = new SolidBrush(Color.FromArgb(40, 90, 200, 255)))
                {
                    e.Graphics.FillRectangle(fill, _rect);
                    e.Graphics.DrawRectangle(pen, _rect);
                }
            }

            string mode = _mode == ViewMode.Select ? "框选：拖拽矩形" : (_mode == ViewMode.Measure ? "测距：点两个点" : "");
            if (mode.Length > 0)
            {
                using (Font f = new Font("Microsoft YaHei UI", 12f * UiScale, GraphicsUnit.Pixel))
                using (SolidBrush b = new SolidBrush(Color.FromArgb(200, 220, 240)))
                {
                    e.Graphics.DrawString(mode, f, b, 8, h - 24);
                }
            }
        }
    }

    // ---------------------------------------------------------------- pretty
    public static class JsonPretty
    {
        public static string Format(object o)
        {
            StringBuilder sb = new StringBuilder();
            Write(sb, o, 0);
            return sb.ToString();
        }

        static void Write(StringBuilder sb, object o, int indent)
        {
            string pad = new string(' ', indent * 2);
            string pad2 = new string(' ', (indent + 1) * 2);
            Dictionary<string, object> map = o as Dictionary<string, object>;
            if (map != null)
            {
                if (map.Count == 0) { sb.Append("{}"); return; }
                sb.Append("{\n");
                int k = 0;
                foreach (KeyValuePair<string, object> kv in map)
                {
                    sb.Append(pad2).Append('"').Append(kv.Key).Append("\": ");
                    Write(sb, kv.Value, indent + 1);
                    if (++k < map.Count) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(pad).Append('}');
                return;
            }
            List<object> list = o as List<object>;
            if (list != null)
            {
                if (list.Count == 0) { sb.Append("[]"); return; }
                sb.Append("[\n");
                for (int i = 0; i < list.Count; i++)
                {
                    sb.Append(pad2);
                    Write(sb, list[i], indent + 1);
                    if (i + 1 < list.Count) sb.Append(',');
                    sb.Append('\n');
                }
                sb.Append(pad).Append(']');
                return;
            }
            if (o is string) { sb.Append(Json.Stringify(o)); return; }
            sb.Append(Json.Stringify(o));
        }
    }
}
