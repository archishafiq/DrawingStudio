using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using DrawingStudio.Shared;

namespace DrawingStudio.Bridge.Handlers
{
    /// <summary>Sheet creation, view placement and renumbering.</summary>
    internal static class SheetHandlers
    {
        public static CreateResult CreateSheets(HandlerContext ctx, CreateSheetsRequest req)
        {
            var doc = ctx.Doc;
            var titleBlock = Ids.Get<FamilySymbol>(doc, req.TitleBlockId)
                             ?? new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                                 .WhereElementIsElementType().OfType<FamilySymbol>().FirstOrDefault();
            if (titleBlock == null) ctx.Warn("No title block family is loaded; sheets are created without one.");

            var used = RevitUtil.SheetNumbers(doc);
            var result = new CreateResult { Created = new List<CreatedItem>() };
            var layout = req.Layout ?? StandardsConfig.CreateDefault().ToLayout(true);

            using (var t = RevitUtil.StartTransaction(doc, "Create sheets"))
            {
                if (titleBlock != null && !titleBlock.IsActive) titleBlock.Activate();

                foreach (var plan in req.Sheets.OrEmpty())
                {
                    var number = Naming.Clean(plan.Number, false);
                    if (string.IsNullOrWhiteSpace(number) || used.Contains(number))
                    {
                        ctx.Warn($"Sheet number '{number}' is empty or already used; skipped.");
                        result.Skipped++;
                        continue;
                    }

                    var sheet = ViewSheet.Create(doc, titleBlock?.Id ?? ElementId.InvalidElementId);
                    sheet.SheetNumber = number;
                    sheet.Name = Naming.Clean(plan.Name, false);
                    used.Add(number);

                    doc.Regenerate();
                    var tbInstance = RevitUtil.TitleBlockOn(doc, sheet);
                    foreach (var p in req.Parameters.OrEmpty())
                    {
                        if (!RevitUtil.SetParameter(sheet, p.Name, p.Value) && !RevitUtil.SetParameter(tbInstance, p.Name, p.Value))
                            ctx.Warn($"{number}: parameter '{p.Name}' not found or read-only.");
                    }

                    if (req.AutoPlace && plan.ViewIds.OrEmpty().Count > 0)
                        Layout.Place(ctx, sheet, plan.ViewIds, layout, false);

                    result.Created.Add(new CreatedItem { Id = Ids.Of(sheet.Id), Name = number + " - " + sheet.Name });
                }
                t.Commit();
            }

            ctx.Info($"Created {result.Created.Count} sheet(s), skipped {result.Skipped}.");
            return result;
        }

        public static CreateResult PlaceViews(HandlerContext ctx, PlaceViewsRequest req)
        {
            var sheet = Ids.Get<ViewSheet>(ctx.Doc, req.SheetId) ?? throw new BridgeException("Sheet not found.");
            var result = new CreateResult { Created = new List<CreatedItem>() };
            using (var t = RevitUtil.StartTransaction(ctx.Doc, "Place views"))
            {
                var placed = Layout.Place(ctx, sheet, req.ViewIds.OrEmpty(),
                    req.Layout ?? StandardsConfig.CreateDefault().ToLayout(true), req.RelayoutExisting);
                foreach (var vp in placed)
                    result.Created.Add(new CreatedItem { Id = Ids.Of(vp.ViewId), Name = ctx.Doc.GetElement(vp.ViewId)?.Name });
                t.Commit();
            }
            return result;
        }

        /// <summary>Two-pass rename so swapping numbers never collides.</summary>
        public static CreateResult RenumberSheets(HandlerContext ctx, RenumberSheetsRequest req)
        {
            var doc = ctx.Doc;
            var items = req.Assignments.OrEmpty()
                .Select(a => new { Sheet = Ids.Get<ViewSheet>(doc, a.Id), Number = Naming.Clean(a.Value, false) })
                .Where(x => x.Sheet != null && !string.IsNullOrWhiteSpace(x.Number))
                .ToList();

            var dupes = items.GroupBy(x => x.Number, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
            if (dupes.Count > 0) throw new BridgeException("Duplicate target numbers: " + string.Join(", ", dupes));

            var movingIds = new HashSet<ElementId>(items.Select(x => x.Sheet.Id));
            var others = new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                    .Where(s => !movingIds.Contains(s.Id)).Select(s => s.SheetNumber),
                StringComparer.OrdinalIgnoreCase);
            var clash = items.Where(x => others.Contains(x.Number)).Select(x => x.Number).ToList();
            if (clash.Count > 0) throw new BridgeException("Numbers already used by other sheets: " + string.Join(", ", clash));

            var result = new CreateResult { Created = new List<CreatedItem>() };
            using (var t = RevitUtil.StartTransaction(doc, "Renumber sheets"))
            {
                int i = 0;
                foreach (var x in items) x.Sheet.SheetNumber = "DSTMP-" + (i++);
                foreach (var x in items)
                {
                    x.Sheet.SheetNumber = x.Number;
                    result.Created.Add(new CreatedItem { Id = Ids.Of(x.Sheet.Id), Name = x.Number });
                }
                t.Commit();
            }
            ctx.Info($"Renumbered {items.Count} sheet(s).");
            return result;
        }

        public static CreateResult RenumberViewports(HandlerContext ctx, RenumberViewportsRequest req)
        {
            var result = new CreateResult { Created = new List<CreatedItem>() };
            using (var t = RevitUtil.StartTransaction(ctx.Doc, "Renumber details"))
            {
                foreach (var id in req.SheetIds.OrEmpty())
                {
                    var sheet = Ids.Get<ViewSheet>(ctx.Doc, id);
                    if (sheet == null) continue;
                    int n = Layout.RenumberDetails(ctx.Doc, sheet, Math.Max(1, req.Start));
                    result.Created.Add(new CreatedItem { Id = id, Name = sheet.SheetNumber + ": " + n + " viewport(s)" });
                }
                t.Commit();
            }
            return result;
        }
    }

    /// <summary>
    /// Shelf packing inside the sheet's drawing area: ISO 5457 border margins, minus the
    /// ISO 7200 title strip. Views fill left→right, then top→bottom, in the order given.
    /// </summary>
    internal static class Layout
    {
        public static List<Viewport> Place(HandlerContext ctx, ViewSheet sheet, IList<long> viewIds, LayoutSettings s, bool relayoutExisting)
        {
            var doc = ctx.Doc;
            doc.Regenerate();
            var area = DrawingArea(doc, sheet, s);
            var start = new XYZ((area.MinX + area.MaxX) / 2, (area.MinY + area.MaxY) / 2, 0);

            var viewports = new List<Viewport>();
            if (relayoutExisting)
                viewports.AddRange(sheet.GetAllViewports().Select(id => doc.GetElement(id)).OfType<Viewport>());

            var created = new List<Viewport>();
            foreach (var vid in viewIds)
            {
                var view = Ids.Get<View>(doc, vid);
                if (view == null) continue;
                if (view is ViewSchedule) { ctx.Warn($"{view.Name}: schedules are not placed by the layout tool."); continue; }
                if (!Viewport.CanAddViewToSheet(doc, sheet.Id, view.Id))
                {
                    ctx.Warn($"{view.Name} is already on a sheet or cannot be placed; skipped.");
                    continue;
                }
                var vp = Viewport.Create(doc, sheet.Id, view.Id, start);
                created.Add(vp);
                viewports.Add(vp);
            }

            doc.Regenerate();
            double gap = Mm.ToFt(s.GapMm);
            double x = area.MinX, top = area.MaxY, rowHeight = 0;

            foreach (var vp in viewports)
            {
                var outline = vp.GetBoxOutline();
                double w = outline.MaximumPoint.X - outline.MinimumPoint.X;
                double h = outline.MaximumPoint.Y - outline.MinimumPoint.Y;

                if (x > area.MinX + 1e-6 && x + w > area.MaxX)
                {
                    top -= rowHeight + gap;
                    x = area.MinX;
                    rowHeight = 0;
                }

                vp.SetBoxCenter(new XYZ(x + w / 2, top - h / 2, 0));
                if (w > area.MaxX - area.MinX || top - h < area.MinY)
                    ctx.Warn($"{sheet.SheetNumber}: '{doc.GetElement(vp.ViewId)?.Name}' does not fit the drawing area. Check its scale or crop.");

                x += w + gap;
                rowHeight = Math.Max(rowHeight, h);
            }

            if (s.RenumberDetails) RenumberDetails(doc, sheet, 1);
            return created;
        }

        /// <summary>Detail numbers 1..n reading order: rows top to bottom, then left to right.</summary>
        public static int RenumberDetails(Document doc, ViewSheet sheet, int start)
        {
            var viewports = sheet.GetAllViewports().Select(id => doc.GetElement(id)).OfType<Viewport>().ToList();
            double rowTol = Mm.ToFt(50);
            var ordered = viewports
                .Select(vp => new { vp, c = vp.GetBoxCenter() })
                .OrderByDescending(v => Math.Round(v.c.Y / rowTol))
                .ThenBy(v => v.c.X)
                .Select(v => v.vp).ToList();

            int i = 0;
            foreach (var vp in ordered)
                vp.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.Set("DSTMP" + (i++));
            int n = start;
            foreach (var vp in ordered)
                vp.get_Parameter(BuiltInParameter.VIEWPORT_DETAIL_NUMBER)?.Set((n++).ToString());
            return ordered.Count;
        }

        private sealed class Rect
        {
            public double MinX, MinY, MaxX, MaxY;
        }

        private static Rect DrawingArea(Document doc, ViewSheet sheet, LayoutSettings s)
        {
            XYZ min, max;
            var bb = RevitUtil.TitleBlockOn(doc, sheet)?.get_BoundingBox(sheet);
            if (bb != null) { min = bb.Min; max = bb.Max; }
            else { min = XYZ.Zero; max = new XYZ(Mm.ToFt(841), Mm.ToFt(594), 0); } // A1 landscape

            double left = min.X + Mm.ToFt(s.MarginLeftMm);
            double right = max.X - Mm.ToFt(s.MarginOtherMm);
            double bottom = min.Y + Mm.ToFt(s.MarginOtherMm);
            double topY = max.Y - Mm.ToFt(s.MarginOtherMm);

            if (string.Equals(s.TitleStripSide, "Bottom", StringComparison.OrdinalIgnoreCase))
                bottom += Mm.ToFt(s.TitleStripMm);
            else
                right -= Mm.ToFt(s.TitleStripMm);

            return new Rect { MinX = left, MinY = bottom, MaxX = right, MaxY = topY };
        }
    }
}
