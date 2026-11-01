using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using DrawingStudio.Shared;

namespace DrawingStudio.Bridge.Handlers
{
    /// <summary>Grid dimensions, grid bubbles and element tags across many views.</summary>
    internal static class AnnotationHandler
    {
        public static AnnotateResult Run(HandlerContext ctx, AnnotateRequest req)
        {
            var doc = ctx.Doc;
            var result = new AnnotateResult();
            var categories = new List<BuiltInCategory>();
            foreach (var name in req.TagCategories.OrEmpty())
                if (Enum.TryParse(name, out BuiltInCategory bic)) categories.Add(bic);

            var failedCategories = new HashSet<BuiltInCategory>();

            using (var t = RevitUtil.StartTransaction(doc, "Annotate views"))
            {
                foreach (var id in req.ViewIds.OrEmpty())
                {
                    var view = Ids.Get<View>(doc, id);
                    if (view == null || view.IsTemplate || view is ViewSheet) continue;
                    if (view.ViewType == ViewType.ThreeD || view.ViewType == ViewType.DraftingView)
                    {
                        ctx.Warn($"{view.Name}: 3D and drafting views are skipped.");
                        continue;
                    }

                    if (req.GridBubblesBothEnds) result.Bubbles += ShowBubbles(doc, view);
                    if (req.DimensionGrids && view is ViewPlan) result.Dimensions += DimensionGrids(ctx, view, req);
                    if (req.TagElements && categories.Count > 0)
                    {
                        var (tags, failures) = TagElements(ctx, view, categories, req, failedCategories);
                        result.Tags += tags;
                        result.TagFailures += failures;
                    }
                    result.ViewsProcessed++;
                }
                t.Commit();
            }

            foreach (var c in failedCategories)
                ctx.Warn($"Some {c} elements could not be tagged. Load a tag family for that category.");
            ctx.Info($"{result.ViewsProcessed} view(s): {result.Dimensions} dimension strings, {result.Tags} tags, {result.Bubbles} grid ends.");
            return result;
        }

        private static int ShowBubbles(Document doc, View view)
        {
            int count = 0;
            foreach (var grid in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>())
            {
                foreach (var end in new[] { DatumEnds.End0, DatumEnds.End1 })
                {
                    try
                    {
                        if (!grid.IsBubbleVisibleInView(end, view)) { grid.ShowBubbleInView(end, view); count++; }
                    }
                    catch { /* datum not editable in this view */ }
                }
            }
            return count;
        }

        /// <summary>
        /// Groups parallel straight grids and adds a chain string (every grid) and an
        /// overall string (first to last) just inside the grid heads.
        /// </summary>
        private static int DimensionGrids(HandlerContext ctx, View view, AnnotateRequest req)
        {
            var doc = ctx.Doc;
            int count = 0;
            double scale = Math.Max(1, view.Scale);
            double offset = Mm.ToFt(req.DimOffsetMm > 0 ? req.DimOffsetMm : 12) * scale;
            double gap = Mm.ToFt(req.DimGapMm > 0 ? req.DimGapMm : 8) * scale;

            var items = new List<(Grid grid, Curve curve, XYZ dir)>();
            foreach (var g in new FilteredElementCollector(doc, view.Id).OfClass(typeof(Grid)).Cast<Grid>())
            {
                if (!(g.Curve is Line)) continue;
                Curve c = null;
                try { c = g.GetCurvesInView(DatumExtentType.ViewSpecific, view).FirstOrDefault(); } catch { }
                c = c ?? g.Curve;
                var d = c.GetEndPoint(1) - c.GetEndPoint(0);
                d = new XYZ(d.X, d.Y, 0);
                if (d.GetLength() < 1e-6) continue;
                items.Add((g, c, d.Normalize()));
            }

            // group by direction, treating opposite directions as the same
            var groups = items.GroupBy(i =>
            {
                double a = Math.Atan2(i.dir.Y, i.dir.X);
                if (a < 0) a += Math.PI;
                if (a >= Math.PI - 1e-4) a -= Math.PI;
                return Math.Round(a, 3);
            });

            foreach (var group in groups)
            {
                var list = group.ToList();
                if (list.Count < 2) continue;

                var d = list[0].dir;
                var n = new XYZ(-d.Y, d.X, 0);
                double z = list[0].curve.GetEndPoint(0).Z;

                var across = list
                    .Select(i => new { i.grid, s = i.curve.GetEndPoint(0).DotProduct(n) })
                    .OrderBy(i => i.s).ToList();
                // drop grids that coincide
                var distinct = new List<(Grid grid, double s)>();
                foreach (var a in across)
                    if (distinct.Count == 0 || Math.Abs(a.s - distinct[distinct.Count - 1].s) > 1e-3)
                        distinct.Add((a.grid, a.s));
                if (distinct.Count < 2) continue;

                var along = list.SelectMany(i => new[] { i.curve.GetEndPoint(0), i.curve.GetEndPoint(1) })
                    .Select(p => p.DotProduct(d)).ToList();
                double tMax = along.Max(), tMin = along.Min();
                double sMin = distinct.First().s, sMax = distinct.Last().s;

                var sides = new List<(double overall, double chain)>
                {
                    (tMax - offset, tMax - offset - (req.OverallDimension ? gap : 0))
                };
                if (req.BothSides) sides.Add((tMin + offset, tMin + offset + (req.OverallDimension ? gap : 0)));

                foreach (var side in sides)
                {
                    if (Make(doc, view, d, n, z, side.chain, sMin, sMax, distinct.Select(x => x.grid))) count++;
                    if (req.OverallDimension && distinct.Count > 2 &&
                        Make(doc, view, d, n, z, side.overall, sMin, sMax, new[] { distinct.First().grid, distinct.Last().grid })) count++;
                }
            }
            return count;
        }

        private static bool Make(Document doc, View view, XYZ d, XYZ n, double z, double t, double sMin, double sMax, IEnumerable<Grid> grids)
        {
            XYZ P(double s) => new XYZ(d.X * t + n.X * s, d.Y * t + n.Y * s, z);
            var refs = new ReferenceArray();
            foreach (var g in grids) refs.Append(new Reference(g));
            try
            {
                return doc.Create.NewDimension(view, Line.CreateBound(P(sMin), P(sMax)), refs) != null;
            }
            catch
            {
                return false;
            }
        }

        private static (int tags, int failures) TagElements(
            HandlerContext ctx, View view, List<BuiltInCategory> categories, AnnotateRequest req, HashSet<BuiltInCategory> failedCategories)
        {
            var doc = ctx.Doc;
            int tags = 0, failures = 0;

            var tagged = new HashSet<ElementId>();
            if (req.SkipTagged)
            {
                foreach (var tag in new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>())
                    foreach (var id in tag.GetTaggedLocalElementIds())
                        tagged.Add(id);
            }

            foreach (var bic in categories)
            {
                var elements = new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType().ToElements();
                foreach (var e in elements)
                {
                    if (tagged.Contains(e.Id)) continue;
                    var bb = e.get_BoundingBox(view) ?? e.get_BoundingBox(null);
                    if (bb == null) continue;
                    var point = (bb.Min + bb.Max) * 0.5;
                    try
                    {
                        IndependentTag.Create(doc, view.Id, new Reference(e), req.TagLeader,
                            TagMode.TM_ADDBY_CATEGORY, TagOrientation.Horizontal, point);
                        tagged.Add(e.Id);
                        tags++;
                    }
                    catch
                    {
                        failures++;
                        failedCategories.Add(bic);
                    }
                }
            }
            return (tags, failures);
        }
    }
}
