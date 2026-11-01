using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DrawingStudio.App.Controls
{
    public enum DKind
    {
        Grid, GridActive, Section, SectionDepth, Arrow, Element, Level, LevelActive,
        Sheet, Area, Strip, Viewport, ViewportNew, ViewportBad, Text, TextMuted, TextStrong, TextAlert
    }

    public sealed class DLine
    {
        public double X1, Y1, X2, Y2;
        public DKind Kind;
    }

    public sealed class DRect
    {
        public double X, Y, W, H; // lower-left corner, model units (y up)
        public DKind Kind;
        public string Label;
    }

    public sealed class DText
    {
        public double X, Y;
        public string Text;
        public DKind Kind = DKind.Text;
        /// <summary>Pixel offset applied after scaling, so labels sit beside their point.</summary>
        public double Dx, Dy;
    }

    /// <summary>Simple vector picture: lines, rectangles and labels in model units (y up).</summary>
    public sealed class Diagram
    {
        public List<DRect> Rects { get; } = new List<DRect>();
        public List<DLine> Lines { get; } = new List<DLine>();
        public List<DText> Texts { get; } = new List<DText>();
        public string Empty { get; set; } = "Nothing to preview yet.";
        public string Key { get; set; }

        public bool IsEmpty => Rects.Count == 0 && Lines.Count == 0;
    }

    /// <summary>Draws a <see cref="Diagram"/> scaled to fit, keeping proportions.</summary>
    public sealed class DiagramView : FrameworkElement
    {
        public static readonly DependencyProperty DiagramProperty = DependencyProperty.Register(
            nameof(Diagram), typeof(Diagram), typeof(DiagramView),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public Diagram Diagram
        {
            get => (Diagram)GetValue(DiagramProperty);
            set => SetValue(DiagramProperty, value);
        }

        private static readonly Typeface Face = new Typeface("Segoe UI");

        private static Brush B(string hex, byte alpha = 255)
        {
            var c = (Color)ColorConverter.ConvertFromString(hex);
            c.A = alpha;
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Pen P(Brush b, double w, bool dashed = false)
        {
            var p = new Pen(b, w);
            if (dashed) p.DashStyle = new DashStyle(new double[] { 4, 3 }, 0);
            p.Freeze();
            return p;
        }

        private static readonly Brush Ink = B("#1F2933"), Muted = B("#8A96A3"), Accent = B("#0F5C6E"),
            Red = B("#C2410C"), Green = B("#15803D"), Alert = B("#B42318");

        private static (Brush fill, Pen pen) RectStyle(DKind k)
        {
            switch (k)
            {
                case DKind.Sheet: return (Brushes.White, P(Ink, 1.4));
                case DKind.Area: return (null, P(Muted, 1, true));
                case DKind.Strip: return (B("#EEF2F5"), P(Muted, 1));
                case DKind.ViewportNew: return (B("#DCFCE7"), P(Green, 1.4));
                case DKind.ViewportBad: return (B("#FEE2E2"), P(Alert, 1.6));
                case DKind.SectionDepth: return (B("#C2410C", 28), null);
                default: return (B("#E3EFF2"), P(Accent, 1.2));
            }
        }

        private static Pen LinePen(DKind k)
        {
            switch (k)
            {
                case DKind.Grid: return P(B("#B8C0C8"), 1, true);
                case DKind.GridActive: return P(Accent, 1.8);
                case DKind.Section: return P(Red, 2.4);
                case DKind.Arrow: return P(Red, 2.4);
                case DKind.Element: return P(Ink, 3);
                case DKind.Level: return P(B("#B8C0C8"), 1.2);
                case DKind.LevelActive: return P(Accent, 2.2);
                default: return P(Ink, 1);
            }
        }

        private static Brush TextBrush(DKind k) =>
            k == DKind.TextMuted ? Muted : k == DKind.TextStrong ? Accent : k == DKind.TextAlert ? Alert : Ink;

        protected override void OnRender(DrawingContext dc)
        {
            var size = RenderSize;
            dc.DrawRectangle(B("#FAFBFC"), P(B("#DDE2E7"), 1), new Rect(size));
            var d = Diagram;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            if (d == null || d.IsEmpty)
            {
                var ft = new FormattedText(d?.Empty ?? "Nothing to preview yet.", CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, Face, 12, Muted, dpi) { MaxTextWidth = Math.Max(10, size.Width - 24), TextAlignment = TextAlignment.Center };
                dc.DrawText(ft, new Point(12, Math.Max(0, size.Height / 2 - ft.Height / 2)));
                return;
            }

            // bounds of all geometry
            var xs = new List<double>();
            var ys = new List<double>();
            foreach (var r in d.Rects) { xs.Add(r.X); xs.Add(r.X + r.W); ys.Add(r.Y); ys.Add(r.Y + r.H); }
            foreach (var l in d.Lines) { xs.Add(l.X1); xs.Add(l.X2); ys.Add(l.Y1); ys.Add(l.Y2); }
            double minX = xs.Min(), maxX = xs.Max(), minY = ys.Min(), maxY = ys.Max();
            double bw = Math.Max(maxX - minX, 1e-6), bh = Math.Max(maxY - minY, 1e-6);

            const double pad = 26;
            double scale = Math.Min((size.Width - 2 * pad) / bw, (size.Height - 2 * pad) / bh);
            if (scale <= 0 || double.IsInfinity(scale)) return;
            double ox = pad + ((size.Width - 2 * pad) - bw * scale) / 2;
            double oy = pad + ((size.Height - 2 * pad) - bh * scale) / 2;
            Point Map(double x, double y) => new Point(ox + (x - minX) * scale, size.Height - oy - (y - minY) * scale);

            dc.PushClip(new RectangleGeometry(new Rect(size)));

            foreach (var r in d.Rects)
            {
                var (fill, pen) = RectStyle(r.Kind);
                var rect = new Rect(Map(r.X, r.Y + r.H), Map(r.X + r.W, r.Y));
                dc.DrawRectangle(fill, pen, rect);
                if (!string.IsNullOrEmpty(r.Label) && rect.Width > 30 && rect.Height > 14)
                {
                    var ft = new FormattedText(r.Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face, 10.5,
                        r.Kind == DKind.ViewportBad ? Alert : Ink, dpi)
                    { MaxTextWidth = rect.Width - 6, MaxTextHeight = Math.Max(1, rect.Height - 4), Trimming = TextTrimming.CharacterEllipsis };
                    dc.DrawText(ft, new Point(rect.X + 3, rect.Y + 2));
                }
            }

            foreach (var l in d.Lines)
                dc.DrawLine(LinePen(l.Kind), Map(l.X1, l.Y1), Map(l.X2, l.Y2));

            foreach (var t in d.Texts)
            {
                if (string.IsNullOrEmpty(t.Text)) continue;
                var ft = new FormattedText(t.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face,
                    t.Kind == DKind.TextStrong ? 11.5 : 10.5, TextBrush(t.Kind), dpi);
                if (t.Kind == DKind.TextStrong) ft.SetFontWeight(FontWeights.SemiBold);
                var p = Map(t.X, t.Y);
                dc.DrawText(ft, new Point(p.X + t.Dx - ft.Width / 2, p.Y + t.Dy - ft.Height / 2));
            }

            dc.Pop();
        }
    }
}
