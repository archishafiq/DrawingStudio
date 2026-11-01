using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using DrawingStudio.Shared;

namespace DrawingStudio.Bridge.Handlers
{
    /// <summary>Batch creation of plans and sections.</summary>
    internal static class ViewHandlers
    {
        // ------------------------------------------------------------------ plans

        public static CreateResult CreatePlans(HandlerContext ctx, CreatePlansRequest req)
        {
            var doc = ctx.Doc;
            ViewFamily family;
            switch (req.PlanKind)
            {
                case "StructuralPlan": family = ViewFamily.StructuralPlan; break;
                case "CeilingPlan": family = ViewFamily.CeilingPlan; break;
                default: family = ViewFamily.FloorPlan; break;
            }

            var vft = RevitUtil.FirstViewFamilyType(doc, family)
                      ?? throw new BridgeException($"This model has no {family} view type.");
            var names = RevitUtil.ViewNames(doc);
            var result = new CreateResult { Created = new List<CreatedItem>() };

            using (var t = RevitUtil.StartTransaction(doc, "Create plans"))
            {
                foreach (var id in req.LevelIds.OrEmpty())
                {
                    var level = Ids.Get<Level>(doc, id);
                    if (level == null) { result.Skipped++; continue; }

                    var plan = ViewPlan.Create(doc, vft.Id, level.Id);
                    RevitUtil.ApplyScaleAndTemplate(ctx, plan, req.Scale, req.ViewTemplateId);

                    var tokens = new Dictionary<string, string>
                    {
                        ["Role"] = req.Role,
                        ["PlanKind"] = Naming.PlanKindLabel(req.PlanKind),
                        ["LevelName"] = level.Name,
                        ["LevelCode"] = Naming.LevelCode(level.Name),
                        ["Scale"] = Naming.ScaleLabel(plan.Scale)
                    };
                    var name = Naming.MakeUnique(Naming.Clean(Naming.Fill(req.NameTemplate, tokens), req.Uppercase), names);
                    try { plan.Name = name; }
                    catch (Exception ex) { ctx.Warn($"Could not rename plan for {level.Name}: {ex.Message}"); }

                    result.Created.Add(new CreatedItem { Id = Ids.Of(plan.Id), Name = plan.Name });
                }
                t.Commit();
            }

            ctx.Info($"Created {result.Created.Count} {Naming.PlanKindLabel(req.PlanKind).ToLowerInvariant()}(s).");
            return result;
        }

        // ------------------------------------------------------------------ sections

        private sealed class SectionSpec
        {
            public XYZ Start;
            public XYZ End;
            public double ZMin;
            public double ZMax;
            public double Padding;
            public string Ref;
            public string Category;
        }

        public static CreateResult CreateSections(HandlerContext ctx, CreateSectionsRequest req)
        {
            var doc = ctx.Doc;
            var vft = RevitUtil.FirstViewFamilyType(doc, ViewFamily.Section)
                      ?? throw new BridgeException("This model has no Section view type.");

            double padFt = Mm.ToFt(req.PaddingMm > 0 ? req.PaddingMm : 1000);
            double depthFt = Mm.ToFt(req.DepthMm > 0 ? req.DepthMm : 6000);
            double offsetFt = Mm.ToFt(req.OffsetMm);
            var result = new CreateResult { Created = new List<CreatedItem>() };
            var specs = new List<SectionSpec>();
            string kind;

            if (req.Mode == "Grids")
            {
                kind = "SECTION";
                var elevations = new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>()
                    .Select(l => l.ProjectElevation).ToList();
                double zMin = elevations.Count > 0 ? elevations.Min() - padFt : -padFt;
                double zMax = elevations.Count > 0 ? elevations.Max() + Math.Max(padFt, Mm.ToFt(3000)) : Mm.ToFt(10000);

                foreach (var id in req.GridIds.OrEmpty())
                {
                    var grid = Ids.Get<Grid>(doc, id);
                    if (!(grid?.Curve is Line line))
                    {
                        ctx.Warn($"Grid {grid?.Name ?? id.ToString()} is not straight and was skipped.");
                        result.Skipped++;
                        continue;
                    }
                    specs.Add(new SectionSpec
                    {
                        Start = line.GetEndPoint(0),
                        End = line.GetEndPoint(1),
                        ZMin = zMin,
                        ZMax = zMax,
                        Padding = 0,
                        Ref = grid.Name,
                        Category = "GRID"
                    });
                }
            }
            else
            {
                bool cross = req.Mode == "ElementsCross";
                kind = cross ? "CROSS SECTION" : "LONG SECTION";

                var ids = req.ElementIds.OrEmpty().Select(Ids.To).ToList();
                if (ids.Count == 0) ids = ctx.UiDoc.Selection.GetElementIds().ToList();
                if (ids.Count == 0) throw new BridgeException("Select beams, walls, columns or footings in Revit first.");

                foreach (var id in ids)
                {
                    var e = doc.GetElement(id);
                    var bb = e?.get_BoundingBox(null);
                    if (bb == null || e is View || !TryAxis(ctx, e, bb, out var a, out var b))
                    {
                        result.Skipped++;
                        continue;
                    }

                    if (cross)
                    {
                        var mid = (a + b) * 0.5;
                        var d = Flat(b - a).Normalize();
                        var n = new XYZ(-d.Y, d.X, 0);
                        double half = Corners(bb).Max(c => Math.Abs(Flat(c - mid).DotProduct(n)));
                        a = mid - n * half;
                        b = mid + n * half;
                    }

                    specs.Add(new SectionSpec
                    {
                        Start = a,
                        End = b,
                        ZMin = bb.Min.Z - padFt,
                        ZMax = bb.Max.Z + padFt,
                        Padding = padFt,
                        Ref = RevitUtil.MarkOrId(e),
                        Category = e.Category?.Name ?? ""
                    });
                }
            }

            var names = RevitUtil.ViewNames(doc);
            using (var t = RevitUtil.StartTransaction(doc, "Create sections"))
            {
                int index = 1;
                foreach (var spec in specs)
                {
                    var s = Flat(spec.Start);
                    var e = Flat(spec.End);
                    if (s.DistanceTo(e) < 1e-3) { result.Skipped++; continue; }
                    if (req.Flip) { var tmp = s; s = e; e = tmp; }

                    var dir = (e - s).Normalize();
                    var up = XYZ.BasisZ;
                    var towardViewer = dir.CrossProduct(up); // section looks along -towardViewer

                    var transform = Transform.Identity;
                    transform.Origin = (s + e) * 0.5 + towardViewer * offsetFt;
                    transform.BasisX = dir;
                    transform.BasisY = up;
                    transform.BasisZ = towardViewer;

                    double half = s.DistanceTo(e) / 2 + spec.Padding;
                    var box = new BoundingBoxXYZ
                    {
                        Transform = transform,
                        // origin sits at Z = 0, so local Y equals model Z
                        Min = new XYZ(-half, spec.ZMin, -depthFt),
                        Max = new XYZ(half, spec.ZMax, 0)
                    };

                    ViewSection section;
                    try { section = ViewSection.CreateSection(doc, vft.Id, box); }
                    catch (Exception ex)
                    {
                        ctx.Warn($"Section {spec.Ref} failed: {ex.Message}");
                        result.Skipped++;
                        continue;
                    }

                    RevitUtil.ApplyScaleAndTemplate(ctx, section, req.Scale, req.ViewTemplateId);

                    var tokens = new Dictionary<string, string>
                    {
                        ["Role"] = req.Role,
                        ["Kind"] = kind,
                        ["Ref"] = spec.Ref,
                        ["Category"] = spec.Category,
                        ["Index"] = index.ToString("00"),
                        ["Scale"] = Naming.ScaleLabel(section.Scale)
                    };
                    var name = Naming.MakeUnique(Naming.Clean(Naming.Fill(req.NameTemplate, tokens), req.Uppercase), names);
                    try { section.Name = name; }
                    catch (Exception ex) { ctx.Warn($"Could not rename section {spec.Ref}: {ex.Message}"); }

                    result.Created.Add(new CreatedItem { Id = Ids.Of(section.Id), Name = section.Name });
                    index++;
                }
                t.Commit();
            }

            ctx.Info($"Created {result.Created.Count} section(s), skipped {result.Skipped}.");
            return result;
        }

        /// <summary>Horizontal axis of an element: location line, column orientation, or longest side of its box.</summary>
        private static bool TryAxis(HandlerContext ctx, Element e, BoundingBoxXYZ bb, out XYZ a, out XYZ b)
        {
            a = b = null;
            if (e.Location is LocationCurve lc && lc.Curve != null)
            {
                if (!(lc.Curve is Line)) ctx.Warn($"{RevitUtil.MarkOrId(e)} is curved; the section uses its chord.");
                a = lc.Curve.GetEndPoint(0);
                b = lc.Curve.GetEndPoint(1);
                if (Flat(b - a).GetLength() > 1e-3) return true;
            }

            var center = (bb.Min + bb.Max) * 0.5;
            if (e.Location is LocationPoint lp)
            {
                var dir = (e as FamilyInstance)?.HandOrientation ?? XYZ.BasisX;
                dir = Flat(dir);
                dir = dir.GetLength() < 1e-6 ? XYZ.BasisX : dir.Normalize();
                double h = Corners(bb).Max(c => Math.Abs(Flat(c - lp.Point).DotProduct(dir)));
                if (h < 1e-3) h = 0.5;
                a = lp.Point - dir * h;
                b = lp.Point + dir * h;
                return true;
            }

            double dx = bb.Max.X - bb.Min.X, dy = bb.Max.Y - bb.Min.Y;
            if (dx < 1e-3 && dy < 1e-3) return false;
            a = dx >= dy ? new XYZ(bb.Min.X, center.Y, 0) : new XYZ(center.X, bb.Min.Y, 0);
            b = dx >= dy ? new XYZ(bb.Max.X, center.Y, 0) : new XYZ(center.X, bb.Max.Y, 0);
            return true;
        }

        private static XYZ Flat(XYZ p) => new XYZ(p.X, p.Y, 0);

        private static IEnumerable<XYZ> Corners(BoundingBoxXYZ bb)
        {
            var t = bb.Transform ?? Transform.Identity;
            foreach (var x in new[] { bb.Min.X, bb.Max.X })
                foreach (var y in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (var z in new[] { bb.Min.Z, bb.Max.Z })
                        yield return t.OfPoint(new XYZ(x, y, z));
        }
    }
}
