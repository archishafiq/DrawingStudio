using System;
using System.Collections.Generic;

namespace DrawingStudio.Shared
{
    public sealed class LayoutRect
    {
        public double MinX, MinY, MaxX, MaxY;
        public double Width => MaxX - MinX;
        public double Height => MaxY - MinY;
    }

    public struct PlacedBox
    {
        public double CenterX, CenterY, Width, Height;
        public bool Fits;
    }

    /// <summary>
    /// Sheet layout maths shared by the Revit add-in (which places the viewports) and the
    /// app (which draws the preview), so the preview matches what Revit will do.
    /// Units are whatever the caller uses (feet in Revit, mm in the app).
    /// </summary>
    public static class SheetLayout
    {
        /// <summary>Drawing area: sheet outline minus ISO 5457 margins and the title strip.</summary>
        public static LayoutRect DrawingArea(LayoutRect sheet, LayoutSettings s, double unitsPerMm)
        {
            var r = new LayoutRect
            {
                MinX = sheet.MinX + s.MarginLeftMm * unitsPerMm,
                MaxX = sheet.MaxX - s.MarginOtherMm * unitsPerMm,
                MinY = sheet.MinY + s.MarginOtherMm * unitsPerMm,
                MaxY = sheet.MaxY - s.MarginOtherMm * unitsPerMm
            };
            if (string.Equals(s.TitleStripSide, "Bottom", StringComparison.OrdinalIgnoreCase))
                r.MinY += s.TitleStripMm * unitsPerMm;
            else
                r.MaxX -= s.TitleStripMm * unitsPerMm;
            return r;
        }

        /// <summary>Shelf packing: left to right, then the next row down, in the order given.</summary>
        public static List<PlacedBox> Pack(LayoutRect area, IList<(double w, double h)> sizes, double gap)
        {
            var result = new List<PlacedBox>(sizes.Count);
            double x = area.MinX, top = area.MaxY, rowHeight = 0;
            foreach (var (w, h) in sizes)
            {
                if (x > area.MinX + 1e-9 && x + w > area.MaxX)
                {
                    top -= rowHeight + gap;
                    x = area.MinX;
                    rowHeight = 0;
                }
                result.Add(new PlacedBox
                {
                    CenterX = x + w / 2,
                    CenterY = top - h / 2,
                    Width = w,
                    Height = h,
                    Fits = w <= area.Width + 1e-9 && top - h >= area.MinY - 1e-9
                });
                x += w + gap;
                rowHeight = Math.Max(rowHeight, h);
            }
            return result;
        }

        /// <summary>ISO 216 landscape size from a title block name (A0–A4), or null.</summary>
        public static (double w, double h)? IsoSizeFromName(string name)
        {
            var n = (name ?? "").ToUpperInvariant();
            if (n.Contains("A0")) return (1189, 841);
            if (n.Contains("A1")) return (841, 594);
            if (n.Contains("A2")) return (594, 420);
            if (n.Contains("A3")) return (420, 297);
            if (n.Contains("A4")) return (297, 210);
            return null;
        }
    }
}
