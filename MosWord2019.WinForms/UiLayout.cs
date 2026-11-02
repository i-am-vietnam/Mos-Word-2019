using System;
using System.Drawing;
using System.Windows.Forms;

namespace MosWord2019
{
    internal static class UiLayout
    {
        internal static void ScaleForm(Form form, float fontSize = 9)
        {
            form.Font = new Font("Segoe UI", fontSize);
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.AutoScaleDimensions = new SizeF(96, 96);
        }
        internal static T Text<T>(T control) where T : Control
        {
            control.AutoSize = true; control.Margin = new Padding(1);
            if (control is Label) control.Padding = Padding.Empty;
            if (control is Button) control.Padding = new Padding(10, 2, 10, 2);
            control.HandleCreated += (s, e) => Fit(control);
            control.DpiChangedAfterParent += (s, e) => Fit(control);
            control.TextChanged += (s, e) => Fit(control);
            control.FontChanged += (s, e) => Fit(control);
            Fit(control); return control;
        }
        private static void Fit(Control control)
        {
            if (control.IsDisposed) return;
            bool wrap = control is Label && control.MaximumSize.Width > 0;
            var text = TextRenderer.MeasureText(control.Text, control.Font,
                new Size(wrap ? Math.Max(1, control.MaximumSize.Width - control.Padding.Horizontal) : int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPrefix | (wrap ? TextFormatFlags.WordBreak : TextFormatFlags.SingleLine));
            int glyph = control is CheckBox || control is RadioButton ? SystemInformation.MenuCheckSize.Width + 3 : 0;
            control.MinimumSize = new Size(wrap ? 0 : text.Width + control.Padding.Horizontal + glyph,
                text.Height + control.Padding.Vertical + (control is Button ? 6 : 0));
        }
        internal static FlowLayoutPanel Flow(params Control[] controls)
        {
            var flow = new BufferedFlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                WrapContents = false, Anchor = AnchorStyles.None, Margin = Padding.Empty };
            flow.Controls.AddRange(controls); return flow;
        }
        internal static void Wrap(Label label, Control container)
        {
            label.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            Action resize = () => { label.MaximumSize = new Size(Math.Max(1, container.ClientSize.Width - container.Padding.Horizontal - label.Margin.Horizontal), 0); Fit(label); };
            container.SizeChanged += (s, e) => resize(); resize();
        }
        internal static TableLayoutPanel Header(Control left, Control center, Control right, string name, bool training = false)
        {
            var bar = new BufferedTableLayoutPanel { Name = name, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(1), BackColor = Color.White };
            bar.ColumnStyles.Add(new ColumnStyle(training ? SizeType.AutoSize : SizeType.Percent, training ? 0 : 35));
            bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, training ? 100 : 30));
            bar.ColumnStyles.Add(new ColumnStyle(training ? SizeType.AutoSize : SizeType.Percent, training ? 0 : 35));
            bar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Anchor = AnchorStyles.Left; center.Anchor = AnchorStyles.None; right.Anchor = AnchorStyles.Right;
            bar.Controls.Add(left, 0, 0); bar.Controls.Add(center, 1, 0); bar.Controls.Add(right, 2, 0);
            return bar;
        }
        internal static void TabHeaders(TabControl tabs)
        {
            Action size = () =>
            {
                int width = 1;
                foreach (TabPage page in tabs.TabPages) width = Math.Max(width, TextRenderer.MeasureText(page.Text, tabs.Font).Width);
                tabs.SizeMode = TabSizeMode.Fixed; tabs.ItemSize = new Size(width + 12, tabs.Font.Height + 8);
            };
            tabs.HandleCreated += (s, e) => size(); tabs.DpiChangedAfterParent += (s, e) => size(); tabs.FontChanged += (s, e) => size(); tabs.ControlAdded += (s, e) => size(); tabs.ControlRemoved += (s, e) => size(); size();
        }
        internal static int PanelHeight(Form form, Rectangle area)
        {
            form.Width = area.Width; form.PerformLayout();
            var layout = (TableLayoutPanel)form.Controls[0];
            int height = form.Height - form.ClientSize.Height + layout.Padding.Vertical;
            for (int row = 0; row < layout.RowCount; row++)
            {
                if (layout.RowStyles[row].SizeType != SizeType.AutoSize) continue;
                Control control = layout.GetControlFromPosition(0, row);
                if (control != null && control.Visible) height += control.GetPreferredSize(new Size(Math.Max(1, area.Width - layout.Padding.Horizontal), 0)).Height + control.Margin.Vertical;
            }
            // Keep one complete instruction line and the native tab header after content-sized bars.
            var tabs = Find<TabControl>(form);
            var instruction = tabs != null && tabs.SelectedTab != null ? Find<RichTextBox>(tabs.SelectedTab) : Find<RichTextBox>(form);
            height += (tabs?.ItemSize.Height ?? form.Font.Height) + (instruction?.Font.Height ?? form.Font.Height) + form.Font.Height;
            return Math.Min(area.Height, Math.Max((int)(area.Height * .32), height));
        }
        private static T Find<T>(Control parent) where T : Control
        {
            foreach (Control child in parent.Controls) { if (child is T found) return found; var nested = Find<T>(child); if (nested != null) return nested; }
            return null;
        }
        internal static void Clock(Label clock, string sample = "00:00")
        {
            clock.AutoSize = false;
            Action fit = () => clock.Size = clock.MinimumSize = TextRenderer.MeasureText(sample, clock.Font) + clock.Padding.Size;
            clock.HandleCreated += (s, e) => fit(); clock.FontChanged += (s, e) => fit(); clock.DpiChangedAfterParent += (s, e) => fit(); fit();
        }
        internal static void EqualButtons(Button first, Button second)
        {
            Action fit = () =>
            {
                int width = Math.Max(120, Math.Max(TextRenderer.MeasureText(first.Text, first.Font).Width + first.Padding.Horizontal + 6,
                    TextRenderer.MeasureText(second.Text, second.Font).Width + second.Padding.Horizontal + 6));
                first.MinimumSize = new Size(width, first.MinimumSize.Height); second.MinimumSize = new Size(width, second.MinimumSize.Height);
            };
            first.TextChanged += (s, e) => fit(); second.TextChanged += (s, e) => fit();
            first.FontChanged += (s, e) => fit(); second.FontChanged += (s, e) => fit(); fit();
        }
    }
    internal class BufferedTableLayoutPanel : TableLayoutPanel
    {
        internal BufferedTableLayoutPanel() { DoubleBuffered = true; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    }
    internal sealed class BufferedFlowLayoutPanel : FlowLayoutPanel
    {
        internal BufferedFlowLayoutPanel() { DoubleBuffered = true; SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true); }
    }
    // A narrow screen moves the center group onto its own row; the right button never stretches or disappears.
    internal sealed class TrainerActionBar : BufferedTableLayoutPanel
    {
        private readonly Control left, center, right;
        private bool arranging;
        internal TrainerActionBar(string name, Control left, Control center, Control right)
        {
            this.left = left; this.center = center; this.right = right;
            Name = name; AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Fill;
            Margin = Padding.Empty; Padding = new Padding(1); ColumnCount = 3; RowCount = 2;
            ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            RowStyles.Add(new RowStyle(SizeType.AutoSize)); RowStyles.Add(new RowStyle(SizeType.AutoSize));
            left.Anchor = AnchorStyles.Left; center.Anchor = AnchorStyles.None; right.Anchor = AnchorStyles.Right;
            Controls.Add(left, 0, 0); Controls.Add(center, 1, 0); Controls.Add(right, 2, 0);
        }
        public override Size GetPreferredSize(Size proposedSize)
        {
            ConfigureColumns(proposedSize.Width > 0 ? proposedSize.Width : ClientSize.Width);
            return base.GetPreferredSize(proposedSize);
        }
        protected override void OnLayout(LayoutEventArgs e)
        {
            ConfigureColumns(ClientSize.Width); base.OnLayout(e);
        }
        private void ConfigureColumns(int width)
        {
            if (!arranging && left != null && center.Parent == this)
            {
                arranging = true;
                try
                {
                    int needed = left.GetPreferredSize(Size.Empty).Width + center.GetPreferredSize(Size.Empty).Width + right.GetPreferredSize(Size.Empty).Width + Padding.Horizontal + 24;
                    bool narrow = needed > width;
                    var cell = new TableLayoutPanelCellPosition(narrow ? 0 : 1, narrow ? 1 : 0);
                    if (GetCellPosition(center) != cell) SetCellPosition(center, cell);
                    if (GetColumnSpan(center) != (narrow ? 3 : 1)) SetColumnSpan(center, narrow ? 3 : 1);
                    center.MaximumSize = new Size(Math.Max(1, width - Padding.Horizontal), 0);
                    if (center is FlowLayoutPanel flow) flow.WrapContents = narrow;
                }
                finally { arranging = false; }
            }
        }
    }
}
