using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Forms;

namespace LightSvnBrowser
{
    /// <summary>Applies a point-size preference from a stable layout baseline, with scrolling on smaller screens.</summary>
    /// <remarks>Author: Codex; date: 2026-11-02.</remarks>
    public static class UiTypography
    {
        private const int ScreenMargin = 20;
        private const int MinimumPixel = 1;
        private static readonly ConditionalWeakTable<Form, LayoutState> Layouts = new ConditionalWeakTable<Form, LayoutState>();

        private sealed class Node
        {
            public Control Control;
            public Font Font;
            public Rectangle Bounds;
            public Padding Margin;
            public Padding Padding;
            public Size Minimum;
            public Size Maximum;
            public float[] Rows;
            public float[] Columns;
            public int TreeHeight;
            public int TreeIndent;
            public int SplitDistance;
            public int SplitWidth;
            public int FirstMinimum;
            public int SecondMinimum;
        }

        private sealed class LayoutState
        {
            public Form Form;
            public Panel Viewport;
            public Panel Canvas;
            public Size Client;
            public Size Minimum;
            public Size Frame;
            public Padding Padding;
            public Font FormFont;
            public List<Node> Nodes;
            public List<Font> AppliedFonts = new List<Font>();
            public float Ratio = 1F;
            public bool Initialized;
            public bool Applying;
            public bool Fitting;
        }

        /// <summary>Changes application fonts and scales the original layout without accumulating rounding errors.</summary>
        /// <param name="form">The form to update on its UI thread.</param>
        /// <param name="fontSize">The preferred base font size in points.</param>
        /// <returns>No return value.</returns>
        public static void Apply(Form form, float fontSize)
        {
            if (form == null) throw new ArgumentNullException("form");
            if (Single.IsNaN(fontSize) || Single.IsInfinity(fontSize) || fontSize < UiFontSettings.MinimumSize || fontSize > UiFontSettings.MaximumSize)
                throw new ArgumentOutOfRangeException("fontSize");
            LayoutState state = Layouts.GetValue(form, CreateState);
            float ratio = fontSize / UiFontSettings.DefaultSize;
            if (state.Initialized && Math.Abs(state.Ratio - ratio) < 0.0001F) return;
            List<Tuple<ColumnHeader, int>> columnWidths = state.Nodes.Where(node => node.Control is ListView)
                .SelectMany(node => ((ListView)node.Control).Columns.Cast<ColumnHeader>())
                .Select(column => Tuple.Create(column, (int)Math.Round(column.Width / state.Ratio))).ToList();
            state.Applying = true;
            form.SuspendLayout();
            state.Viewport.SuspendLayout();
            foreach (Node node in state.Nodes) node.Control.SuspendLayout();
            try
            {
                foreach (Node node in state.Nodes)
                {
                    SplitContainer split = node.Control as SplitContainer;
                    if (split != null) { split.Panel1MinSize = 0; split.Panel2MinSize = 0; }
                }
                state.Ratio = ratio;
                Font rootFont = ResizeFont(state.FormFont, ratio);
                form.Font = rootFont;
                List<Font> newFonts = new List<Font> { rootFont };
                foreach (Node node in state.Nodes)
                {
                    Font font = ResizeFont(node.Font, ratio);
                    node.Control.Font = font;
                    newFonts.Add(font);
                    RestoreNode(node, ratio);
                    TreeView tree = node.Control as TreeView;
                    if (tree != null) { tree.ItemHeight = Pixels(node.TreeHeight, ratio); tree.Indent = Pixels(node.TreeIndent, ratio); }
                }
                foreach (Font old in state.AppliedFonts) old.Dispose();
                state.AppliedFonts = newFonts;
                form.Padding = Padding.Empty;
                state.Canvas.Padding = ScalePadding(state.Padding, ratio);
                ResizeWindow(state);
                foreach (Node node in state.Nodes.AsEnumerable().Reverse()) node.Control.ResumeLayout(true);
                state.Viewport.ResumeLayout(true);
                FitCanvas(state);
                foreach (Node node in state.Nodes)
                {
                    SplitContainer split = node.Control as SplitContainer;
                    if (split == null) continue;
                    int first = Pixels(node.FirstMinimum, ratio);
                    int second = Pixels(node.SecondMinimum, ratio);
                    split.SplitterWidth = Math.Max(MinimumPixel, Pixels(node.SplitWidth, ratio));
                    int extent = split.Orientation == Orientation.Vertical ? split.Width : split.Height;
                    int preferred = split.FixedPanel == FixedPanel.Panel2
                        ? extent - Pixels((split.Orientation == Orientation.Vertical ? node.Bounds.Width : node.Bounds.Height) - node.SplitDistance, ratio)
                        : Pixels(node.SplitDistance, ratio);
                    split.SplitterDistance = Math.Max(first, Math.Min(preferred, extent - second - split.SplitterWidth));
                    split.Panel1MinSize = first;
                    split.Panel2MinSize = second;
                }
                foreach (Tuple<ColumnHeader, int> column in columnWidths) column.Item1.Width = Pixels(column.Item2, ratio);
                WrapTableLabels(state);
                state.Initialized = true;
            }
            finally
            {
                foreach (Node node in state.Nodes) node.Control.ResumeLayout(true);
                state.Viewport.ResumeLayout(true);
                state.Applying = false;
                form.ResumeLayout(true);
                FitCanvas(state);
            }
        }

        private static LayoutState CreateState(Form form)
        {
            form.PerformLayout();
            LayoutState state = new LayoutState
            {
                Form = form, Client = form.ClientSize, Minimum = form.MinimumSize,
                Frame = new Size(form.Width - form.ClientSize.Width, form.Height - form.ClientSize.Height),
                Padding = form.Padding, FormFont = (Font)form.Font.Clone()
            };
            Control[] children = form.Controls.Cast<Control>().ToArray();
            form.SuspendLayout();
            state.Viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true, BackColor = form.BackColor };
            state.Canvas = new Panel { Size = form.ClientSize, Location = Point.Empty, BackColor = form.BackColor, Padding = form.Padding };
            form.Padding = Padding.Empty;
            foreach (Control child in children) form.Controls.Remove(child);
            state.Viewport.Controls.Add(state.Canvas);
            form.Controls.Add(state.Viewport);
            state.Canvas.Controls.AddRange(children);
            form.ResumeLayout(true);
            state.Canvas.PerformLayout();
            List<Control> controls = new List<Control>();
            Collect(state.Canvas, controls);
            foreach (Control control in controls) control.PerformLayout();
            foreach (Control control in controls.ToArray())
                if (control.ContextMenuStrip != null && !controls.Contains(control.ContextMenuStrip)) controls.Add(control.ContextMenuStrip);
            state.Nodes = controls.Select(CaptureNode).ToList();
            state.Viewport.SizeChanged += delegate { if (!state.Applying) FitCanvas(state); };
            form.Disposed += delegate
            {
                state.FormFont.Dispose();
                foreach (Node node in state.Nodes) node.Font.Dispose();
                foreach (Font font in state.AppliedFonts) font.Dispose();
            };
            return state;
        }

        private static void Collect(Control parent, List<Control> controls)
        {
            controls.Add(parent);
            foreach (Control child in parent.Controls) Collect(child, controls);
        }

        private static Node CaptureNode(Control control)
        {
            Node node = new Node { Control = control, Font = (Font)control.Font.Clone(), Bounds = control.Bounds, Margin = control.Margin,
                Padding = control.Padding, Minimum = control.MinimumSize, Maximum = control.MaximumSize };
            TableLayoutPanel table = control as TableLayoutPanel;
            if (table != null)
            {
                node.Rows = table.RowStyles.Cast<RowStyle>().Select(style => style.Height).ToArray();
                node.Columns = table.ColumnStyles.Cast<ColumnStyle>().Select(style => style.Width).ToArray();
            }
            TreeView tree = control as TreeView;
            if (tree != null) { node.TreeHeight = tree.ItemHeight; node.TreeIndent = tree.Indent; }
            SplitContainer split = control as SplitContainer;
            if (split != null)
            {
                node.SplitDistance = split.SplitterDistance; node.SplitWidth = split.SplitterWidth;
                node.FirstMinimum = split.Panel1MinSize; node.SecondMinimum = split.Panel2MinSize;
            }
            return node;
        }

        private static void RestoreNode(Node node, float ratio)
        {
            Control control = node.Control;
            control.Margin = ScalePadding(node.Margin, ratio);
            control.Padding = ScalePadding(node.Padding, ratio);
            if (!(control is SplitterPanel))
            {
                control.MinimumSize = new Size(Pixels(node.Minimum.Width, ratio), Pixels(node.Minimum.Height, ratio));
                control.MaximumSize = new Size(Pixels(node.Maximum.Width, ratio), Pixels(node.Maximum.Height, ratio));
                control.Bounds = new Rectangle(Pixels(node.Bounds.X, ratio), Pixels(node.Bounds.Y, ratio), Pixels(node.Bounds.Width, ratio), Pixels(node.Bounds.Height, ratio));
            }
            TableLayoutPanel table = control as TableLayoutPanel;
            if (table != null)
            {
                for (int index = 0; index < node.Rows.Length; index++) table.RowStyles[index].Height = node.Rows[index] * (table.RowStyles[index].SizeType == SizeType.Absolute ? ratio : 1F);
                for (int index = 0; index < node.Columns.Length; index++) table.ColumnStyles[index].Width = node.Columns[index] * (table.ColumnStyles[index].SizeType == SizeType.Absolute ? ratio : 1F);
            }
            SplitContainer split = control as SplitContainer;
            if (split != null) split.SplitterWidth = Math.Max(MinimumPixel, Pixels(node.SplitWidth, ratio));
        }

        internal static bool IsApplying(Form form)
        {
            LayoutState state;
            return Layouts.TryGetValue(form, out state) && state.Applying;
        }

        private static void ResizeWindow(LayoutState state)
        {
            Rectangle area = Screen.FromControl(state.Form).WorkingArea;
            int availableWidth = Math.Max(MinimumPixel, area.Width - ScreenMargin * 2);
            int availableHeight = Math.Max(MinimumPixel, area.Height - ScreenMargin * 2);
            state.Form.MinimumSize = new Size(Math.Min(Pixels(state.Minimum.Width, state.Ratio), availableWidth), Math.Min(Pixels(state.Minimum.Height, state.Ratio), availableHeight));
            state.Form.ClientSize = new Size(Math.Min(Pixels(state.Client.Width, state.Ratio), availableWidth - state.Frame.Width), Math.Min(Pixels(state.Client.Height, state.Ratio), availableHeight - state.Frame.Height));
            if (state.Form.Visible)
                state.Form.Location = new Point(Math.Max(area.Left, Math.Min(state.Form.Left, area.Right - state.Form.Width)), Math.Max(area.Top, Math.Min(state.Form.Top, area.Bottom - state.Form.Height)));
        }

        private static void FitCanvas(LayoutState state)
        {
            if (state.Fitting || state.Form.IsDisposed) return;
            state.Fitting = true;
            try
            {
                int baselineWidth = state.Minimum.Width > 0 ? state.Minimum.Width - state.Frame.Width : state.Client.Width;
                int baselineHeight = state.Minimum.Height > 0 ? state.Minimum.Height - state.Frame.Height : state.Client.Height;
                Size minimum = new Size(Pixels(baselineWidth, state.Ratio), Pixels(baselineHeight, state.Ratio));
                state.Viewport.AutoScrollMinSize = minimum;
                state.Canvas.Size = new Size(Math.Max(minimum.Width, state.Viewport.ClientSize.Width - state.Viewport.Padding.Horizontal), Math.Max(minimum.Height, state.Viewport.ClientSize.Height - state.Viewport.Padding.Vertical));
            }
            finally { state.Fitting = false; }
        }

        private static void WrapTableLabels(LayoutState state)
        {
            foreach (Node node in state.Nodes)
            {
                Label label = node.Control as Label;
                TableLayoutPanel table = label == null ? null : label.Parent as TableLayoutPanel;
                if (table == null || !label.AutoSize) continue;
                int column = table.GetColumn(label);
                int[] widths = table.GetColumnWidths();
                if (column >= 0 && column < widths.Length)
                    label.MaximumSize = new Size(Math.Max(MinimumPixel, widths[column] - label.Margin.Horizontal), 0);
            }
        }

        private static Font ResizeFont(Font original, float ratio)
        {
            return new Font(original.FontFamily, original.SizeInPoints * ratio, original.Style, GraphicsUnit.Point);
        }

        private static int Pixels(int value, float ratio) { return (int)Math.Round(value * ratio); }

        private static Padding ScalePadding(Padding value, float ratio)
        {
            return new Padding(Pixels(value.Left, ratio), Pixels(value.Top, ratio), Pixels(value.Right, ratio), Pixels(value.Bottom, ratio));
        }
    }
}
