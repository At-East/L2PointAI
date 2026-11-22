// Dialog: choose which bag topic / frame range to import.
using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace L2PointAI
{
    public sealed class BagImportForm : Form
    {
        readonly BagInfo _info;
        ComboBox _cbTopic;
        NumericUpDown _numStart, _numCount;
        Label _lblInfo;
        Button _btnOk, _btnCancel;

        public string SelectedTopic { get { return _cbTopic.SelectedItem == null ? null : _cbTopic.SelectedItem.ToString(); } }
        public int StartIndex { get { return (int)_numStart.Value; } }
        public int FrameCount { get { return (int)_numCount.Value; } }

        readonly float _ui;

        public BagImportForm(string path) : this(path, 0f) { }

        public BagImportForm(string path, float ui)
        {
            // 与主窗口同一套缩放：关闭自动缩放，按 ui 手动放大，字体用像素单位
            AutoScaleMode = AutoScaleMode.None;
            if (ui <= 0f)
            {
                try { using (Graphics g = CreateGraphics()) ui = g.DpiX / 96f; } catch { ui = 1f; }
            }
            if (ui < 0.75f) ui = 0.75f;
            _ui = ui;

            Text = "导入 ROS bag 点云";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            ClientSize = new Size(620, 300);
            Font = new Font("Microsoft YaHei UI", 12f * _ui, GraphicsUnit.Pixel);

            using (BagReader r = new BagReader(path)) { _info = r.Scan(); }

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = new Padding(12);
            root.ColumnCount = 1;
            root.RowCount = 7;
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));

            root.Controls.Add(MkLabel("话题（PointCloud2）："), 0, 0);
            _cbTopic = new ComboBox();
            _cbTopic.Dock = DockStyle.Fill;
            _cbTopic.DropDownStyle = ComboBoxStyle.DropDownList;
            foreach (BagTopic t in _info.Topics)
                if (BagInfo.IsCloud(t.Type)) _cbTopic.Items.Add(t.Topic);
            if (_cbTopic.Items.Count == 0)
                foreach (BagTopic t in _info.Topics) _cbTopic.Items.Add(t.Topic);
            if (_cbTopic.Items.Count > 0) _cbTopic.SelectedIndex = 0;
            _cbTopic.SelectedIndexChanged += delegate { UpdateHint(); };
            root.Controls.Add(_cbTopic, 0, 1);

            FlowLayoutPanel nums = new FlowLayoutPanel();
            nums.Dock = DockStyle.Fill;
            nums.WrapContents = false;
            nums.Margin = new Padding(0);
            nums.Padding = new Padding(0, 2, 0, 0);

            Label lbStart = new Label();
            lbStart.Text = "起始帧";
            lbStart.AutoSize = true;
            lbStart.Margin = new Padding(0, 8, 8, 0);
            nums.Controls.Add(lbStart);

            _numStart = new NumericUpDown();
            _numStart.Minimum = 0;
            _numStart.Maximum = 100000;
            _numStart.Value = 0;
            _numStart.Width = 88;
            _numStart.Margin = new Padding(0, 4, 24, 0);
            _numStart.ValueChanged += delegate { UpdateHint(); };
            nums.Controls.Add(_numStart);

            Label lbCount = new Label();
            lbCount.Text = "帧数";
            lbCount.AutoSize = true;
            lbCount.Margin = new Padding(0, 8, 8, 0);
            nums.Controls.Add(lbCount);

            _numCount = new NumericUpDown();
            _numCount.Minimum = 1;
            _numCount.Maximum = 2000;
            _numCount.Value = 1;
            _numCount.Increment = 10;
            _numCount.Width = 88;
            _numCount.Margin = new Padding(0, 4, 16, 0);
            _numCount.ValueChanged += delegate { UpdateHint(); };
            nums.Controls.Add(_numCount);

            Label lbTip = new Label();
            lbTip.Text = "（帧数 > 1 会把多帧点叠加）";
            lbTip.AutoSize = true;
            lbTip.ForeColor = Color.DimGray;
            lbTip.Margin = new Padding(0, 8, 0, 0);
            nums.Controls.Add(lbTip);
            root.Controls.Add(nums, 0, 2);
            root.SetColumnSpan(nums, 1);

            _lblInfo = new Label();
            _lblInfo.Dock = DockStyle.Fill;
            _lblInfo.ForeColor = Color.DimGray;
            _lblInfo.AutoEllipsis = true;
            root.Controls.Add(_lblInfo, 0, 3);
            root.SetColumnSpan(_lblInfo, 1);

            Label tip = new Label();
            tip.Dock = DockStyle.Fill;
            tip.Text =
                "说明：1 帧 = 一次 360° 扫描（约 4,000 点）。\r\n" +
                "帧数 > 1 会把多帧点位直接叠加（合并）——传感器在移动时会出现拖影。\r\n" +
                "只想看清一帧请保持“帧数 = 1”。";
            tip.ForeColor = Color.DimGray;
            root.Controls.Add(tip, 0, 4);

            FlowLayoutPanel btns = new FlowLayoutPanel();
            btns.Dock = DockStyle.Fill;
            btns.FlowDirection = FlowDirection.RightToLeft;
            _btnCancel = new Button(); _btnCancel.Text = "取消"; _btnCancel.Width = 88;
            _btnCancel.DialogResult = DialogResult.Cancel;
            _btnOk = new Button(); _btnOk.Text = "导入"; _btnOk.Width = 88;
            _btnOk.DialogResult = DialogResult.OK;
            btns.Controls.Add(_btnCancel);
            btns.Controls.Add(_btnOk);
            root.Controls.Add(btns, 0, 6);

            Controls.Add(root);
            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
            if (Math.Abs(_ui - 1f) > 0.01f)
            {
                SuspendLayout();
                Scale(new SizeF(_ui, _ui));
                ResumeLayout(true);
            }
            UpdateHint();
        }

        static Label MkLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Dock = DockStyle.Fill;
            return l;
        }

        void UpdateHint()
        {
            if (_lblInfo == null || _cbTopic == null) return;
            BagTopic t = _info.Find(_cbTopic.SelectedItem as string);
            if (t == null) { _lblInfo.Text = "没有可用话题"; return; }
            _lblInfo.Text = string.Format("{0} · 共 {1:N0} 帧 · 约 {2:F1} 分钟",
                Path.GetFileName(_info.Path), t.Messages, t.Messages / 5.55 / 60.0);
            _lblInfo.Tag = t.Type;
        }
    }
}
