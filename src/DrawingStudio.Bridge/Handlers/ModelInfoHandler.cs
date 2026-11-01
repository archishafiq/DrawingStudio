using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using DrawingStudio.Shared;

namespace DrawingStudio.Bridge.Handlers
{
    internal static class ModelInfoHandler
    {
        public static ModelInfo Run(HandlerContext ctx)
        {
            var doc = ctx.Doc;

            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder).ToList();

            // view id -> sheet number
            var placedOn = new Dictionary<ElementId, string>();
            foreach (var sheet in sheets)
                foreach (var id in sheet.GetAllPlacedViews())
                    placedOn[id] = sheet.SheetNumber;

            var allViews = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().ToList();
            var sheetInfos = sheets.Select(s => SheetOf(doc, s)).ToList();
            var sizes = allViews.Where(RevitUtil.IsDrawingView).ToDictionary(v => v.Id, PaperSize);

            return new ModelInfo
            {
                DocumentTitle = doc.Title,
                RevitVersion = ctx.App.Application.VersionNumber,
                ActiveViewId = Ids.Of(ctx.UiDoc.ActiveView?.Id),

                Levels = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .OrderBy(l => l.ProjectElevation)
                    .Select(l => new LevelInfo
                    {
                        Id = Ids.Of(l.Id),
                        Name = l.Name,
                        ElevationMm = Mm.FromFt(l.Elevation),
                        Code = Naming.LevelCode(l.Name)
                    }).ToList(),

                Grids = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
                    .Select(GridOf)
                    .NaturalOrder(g => g.Name).ToList(),

                Views = allViews.Where(RevitUtil.IsDrawingView)
                    .Select(v => new ViewInfo
                    {
                        Id = Ids.Of(v.Id),
                        Name = v.Name,
                        ViewType = v.ViewType.ToString(),
                        Scale = v.Scale,
                        LevelName = v.GenLevel?.Name,
                        IsPlaced = placedOn.ContainsKey(v.Id),
                        SheetNumber = placedOn.TryGetValue(v.Id, out var n) ? n : null,
                        HasTemplate = v.ViewTemplateId != ElementId.InvalidElementId,
                        PaperWidthMm = sizes[v.Id].w,
                        PaperHeightMm = sizes[v.Id].h
                    })
                    .OrderBy(v => v.ViewType).ThenBy(v => Naming.NaturalKey(v.Name)).ToList(),

                Templates = allViews.Where(v => v.IsTemplate)
                    .Select(v => new ViewTemplateInfo { Id = Ids.Of(v.Id), Name = v.Name, ViewType = v.ViewType.ToString() })
                    .OrderBy(t => t.Name).ToList(),

                Sheets = sheetInfos.NaturalOrder(s => s.Number).ToList(),

                TitleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsElementType().OfType<FamilySymbol>()
                    .Select(f =>
                    {
                        var t = new TitleBlockInfo { Id = Ids.Of(f.Id), Family = f.FamilyName, Type = f.Name };
                        // real size from a sheet that uses this title block, else from the A-size in its name
                        var used = sheetInfos.FirstOrDefault(si => si.TitleBlockTypeId == t.Id && si.WidthMm > 0);
                        var iso = SheetLayout.IsoSizeFromName(f.FamilyName + " " + f.Name);
                        t.WidthMm = used?.WidthMm ?? iso?.w ?? 0;
                        t.HeightMm = used?.HeightMm ?? iso?.h ?? 0;
                        return t;
                    })
                    .OrderBy(t => t.Family).ThenBy(t => t.Type).ToList(),

                SelectedIds = ctx.UiDoc.Selection.GetElementIds().Select(Ids.Of).ToList()
            };
        }

        /// <summary>Elements currently selected in Revit, with their plan axis for the preview.</summary>
        public static List<SelectedElement> Selection(HandlerContext ctx)
        {
            var list = new List<SelectedElement>();
            foreach (var id in ctx.UiDoc.Selection.GetElementIds())
            {
                var e = ctx.Doc.GetElement(id);
                var bb = e?.get_BoundingBox(null);
                if (bb == null || e is View) continue;
                if (!ViewHandlers.TryAxis(ctx, e, bb, out var a, out var b)) continue;

                var d = new XYZ(b.X - a.X, b.Y - a.Y, 0);
                double half = 0;
                if (d.GetLength() > 1e-6)
                {
                    var n = new XYZ(-d.Y, d.X, 0).Normalize();
                    var mid = (a + b) * 0.5;
                    foreach (var x in new[] { bb.Min.X, bb.Max.X })
                        foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                            half = Math.Max(half, Math.Abs((x - mid.X) * n.X + (y - mid.Y) * n.Y));
                }

                list.Add(new SelectedElement
                {
                    Id = Ids.Of(e.Id),
                    Name = (e.Category?.Name ?? e.GetType().Name) + " " + RevitUtil.MarkOrId(e),
                    X1 = Mm.FromFt(a.X), Y1 = Mm.FromFt(a.Y),
                    X2 = Mm.FromFt(b.X), Y2 = Mm.FromFt(b.Y),
                    HalfWidthMm = Mm.FromFt(half)
                });
            }
            return list;
        }

        private static GridInfo GridOf(Grid g)
        {
            var info = new GridInfo { Id = Ids.Of(g.Id), Name = g.Name, IsLinear = g.Curve is Line };
            if (g.Curve != null)
            {
                var p0 = g.Curve.GetEndPoint(0);
                var p1 = g.Curve.GetEndPoint(1);
                info.X1 = Mm.FromFt(p0.X); info.Y1 = Mm.FromFt(p0.Y);
                info.X2 = Mm.FromFt(p1.X); info.Y2 = Mm.FromFt(p1.Y);
            }
            return info;
        }

        /// <summary>
        /// Paper size of a view on a sheet, mm: its crop region divided by the scale. Reading the crop
        /// box is instant; View.Outline would make Revit compute every view's extents.
        /// </summary>
        private static (double w, double h) PaperSize(View v)
        {
            try
            {
                if (v.ViewType == ViewType.ThreeD || v.ViewType == ViewType.DraftingView || v.ViewType == ViewType.Legend) return (0, 0);
                var cb = v.CropBox;
                int scale = v.Scale;
                if (cb == null || scale <= 0) return (0, 0);
                return (Mm.FromFt((cb.Max.X - cb.Min.X) / scale), Mm.FromFt((cb.Max.Y - cb.Min.Y) / scale));
            }
            catch
            {
                return (0, 0);
            }
        }

        private static SheetInfo SheetOf(Document doc, ViewSheet s)
        {
            var info = new SheetInfo
            {
                Id = Ids.Of(s.Id),
                Number = s.SheetNumber,
                Name = s.Name,
                ViewCount = s.GetAllPlacedViews().Count,
                Viewports = new List<SheetViewport>()
            };

            var tb = RevitUtil.TitleBlockOn(doc, s);
            var bb = tb?.get_BoundingBox(s);
            var origin = bb?.Min ?? XYZ.Zero;
            if (bb != null)
            {
                info.WidthMm = Mm.FromFt(bb.Max.X - bb.Min.X);
                info.HeightMm = Mm.FromFt(bb.Max.Y - bb.Min.Y);
                info.TitleBlockTypeId = Ids.Of(tb.GetTypeId());
            }

            foreach (var vp in s.GetAllViewports().Select(id => doc.GetElement(id)).OfType<Viewport>())
            {
                var o = vp.GetBoxOutline();
                info.Viewports.Add(new SheetViewport
                {
                    ViewId = Ids.Of(vp.ViewId),
                    Name = doc.GetElement(vp.ViewId)?.Name,
                    CenterX = Mm.FromFt((o.MinimumPoint.X + o.MaximumPoint.X) / 2 - origin.X),
                    CenterY = Mm.FromFt((o.MinimumPoint.Y + o.MaximumPoint.Y) / 2 - origin.Y),
                    Width = Mm.FromFt(o.MaximumPoint.X - o.MinimumPoint.X),
                    Height = Mm.FromFt(o.MaximumPoint.Y - o.MinimumPoint.Y)
                });
            }
            return info;
        }

        public static bool ShowElements(HandlerContext ctx, ShowElementsRequest request)
        {
            var ids = request.Ids.OrEmpty().Select(Ids.To).Where(id => ctx.Doc.GetElement(id) != null).ToList();
            if (ids.Count == 0) return false;

            if (ids.Count == 1 && ctx.Doc.GetElement(ids[0]) is View view && !view.IsTemplate)
            {
                ctx.UiDoc.RequestViewChange(view);
                return true;
            }

            ctx.UiDoc.Selection.SetElementIds(ids);
            ctx.UiDoc.ShowElements(ids);
            return true;
        }
    }
}
