using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using DrawingStudio.Shared;

namespace DrawingStudio.Bridge.Handlers
{
    /// <summary>Feeds the app's Model tab: view pictures, 3D geometry, and opening models.</summary>
    internal static class ModelViewHandler
    {
        private static string TempRoot => Path.Combine(Path.GetTempPath(), "DrawingStudio");

        // ---------------------------------------------------------------- 2D pictures

        /// <summary>Renders a plan, section, elevation, 3D view or sheet to a PNG with Revit's own graphics.</summary>
        public static RenderViewResult RenderView(HandlerContext ctx, RenderViewRequest req)
        {
            var doc = ctx.Doc;
            var view = Ids.Get<View>(doc, req.ViewId) ?? throw new BridgeException("That view no longer exists. Reload the model.");
            if (view.IsTemplate || view is ViewSchedule) throw new BridgeException($"'{view.Name}' cannot be shown as a picture.");

            var dir = Path.Combine(TempRoot, "renders", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var options = new ImageExportOptions
            {
                ExportRange = ExportRange.SetOfViews,
                FilePath = Path.Combine(dir, "view"),
                FitDirection = FitDirectionType.Horizontal,
                HLRandWFViewsFileType = ImageFileType.PNG,
                ShadowViewsFileType = ImageFileType.PNG,
                ImageResolution = ImageResolution.DPI_150,
                ZoomType = ZoomFitType.FitToPage,
                PixelSize = Math.Max(400, Math.Min(req.PixelSize > 0 ? req.PixelSize : 2400, 8000))
            };
            options.SetViewsAndSheets(new List<ElementId> { view.Id });
            doc.ExportImage(options);

            // Revit appends the view type and name to the file name, so pick whatever it wrote.
            var file = Directory.GetFiles(dir, "*.png").OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault()
                       ?? throw new BridgeException($"Revit did not produce a picture for '{view.Name}'.");
            return new RenderViewResult { FilePath = file, ViewName = view is ViewSheet s ? s.SheetNumber + " - " + s.Name : view.Name };
        }

        // ---------------------------------------------------------------- 3D geometry

        private sealed class Bucket
        {
            public string Name;
            public byte R, G, B, A = 255;
            public readonly List<float> Xyz = new List<float>();
        }

        private static readonly HashSet<BuiltInCategory> Skip = new HashSet<BuiltInCategory>
        {
            BuiltInCategory.OST_Cameras, BuiltInCategory.OST_RvtLinks, BuiltInCategory.OST_Levels,
            BuiltInCategory.OST_Grids, BuiltInCategory.OST_Lines, BuiltInCategory.OST_SectionBox,
            BuiltInCategory.OST_Rooms, BuiltInCategory.OST_Areas, BuiltInCategory.OST_MEPSpaces
        };

        /// <summary>Triangulates the model (as seen in a 3D view) and writes it to a binary file for the app's 3D viewer.</summary>
        public static Model3DResult ExportModel(HandlerContext ctx, Model3DRequest req)
        {
            var doc = ctx.Doc;
            var view = Ids.Get<View3D>(doc, req.ViewId);
            if (view == null || view.IsTemplate)
            {
                var all3d = new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>().Where(v => !v.IsTemplate).ToList();
                view = all3d.FirstOrDefault(v => v.Name.StartsWith("{3D", StringComparison.OrdinalIgnoreCase)) ?? all3d.FirstOrDefault();
            }

            var options = new Options { ComputeReferences = false, IncludeNonVisibleObjects = false };
            if (view != null) options.View = view; // uses that view's visibility and detail level
            else options.DetailLevel = ParseDetail(req.DetailLevel);

            var collector = view != null ? new FilteredElementCollector(doc, view.Id) : new FilteredElementCollector(doc);
            var elements = collector.WhereElementIsNotElementType().ToElements();

            int maxTriangles = req.MaxTriangles > 0 ? req.MaxTriangles : 2_500_000;
            var buckets = new Dictionary<string, Bucket>();
            int triangles = 0, used = 0;
            bool truncated = false;

            foreach (var e in elements)
            {
                var cat = e.Category;
                if (cat == null || cat.CategoryType != CategoryType.Model) continue;
                var bic = (BuiltInCategory)(int)Ids.Of(cat.Id);
                if (Skip.Contains(bic)) continue;

                GeometryElement geom;
                try { geom = e.get_Geometry(options); } catch { continue; }
                if (geom == null) continue;

                if (!buckets.TryGetValue(cat.Name, out var bucket))
                {
                    bucket = new Bucket { Name = cat.Name };
                    ColorFor(cat, bic, bucket);
                    buckets[cat.Name] = bucket;
                }

                int before = bucket.Xyz.Count;
                Walk(geom, bucket.Xyz, ref triangles, maxTriangles);
                if (bucket.Xyz.Count > before) used++;
                if (triangles >= maxTriangles) { truncated = true; break; }
            }

            // centre the model at the origin, in metres
            double minX = double.MaxValue, minY = double.MaxValue, minZ = double.MaxValue;
            double maxX = double.MinValue, maxY = double.MinValue, maxZ = double.MinValue;
            foreach (var b in buckets.Values)
                for (int i = 0; i < b.Xyz.Count; i += 3)
                {
                    minX = Math.Min(minX, b.Xyz[i]); maxX = Math.Max(maxX, b.Xyz[i]);
                    minY = Math.Min(minY, b.Xyz[i + 1]); maxY = Math.Max(maxY, b.Xyz[i + 1]);
                    minZ = Math.Min(minZ, b.Xyz[i + 2]); maxZ = Math.Max(maxZ, b.Xyz[i + 2]);
                }
            float cx = (float)((minX + maxX) / 2), cy = (float)((minY + maxY) / 2), cz = (float)minZ;
            const float ftToM = 0.3048f;

            var dir = Path.Combine(TempRoot, "models");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".dsm");
            var categories = new List<Model3DCategory>();

            using (var w = new BinaryWriter(File.Create(file), Encoding.UTF8))
            {
                var nonEmpty = buckets.Values.Where(b => b.Xyz.Count > 0).OrderBy(b => b.Name).ToList();
                w.Write(new[] { (byte)'D', (byte)'S', (byte)'M', (byte)'1' });
                w.Write(nonEmpty.Count);
                foreach (var b in nonEmpty)
                {
                    w.Write(b.Name);
                    w.Write(b.R); w.Write(b.G); w.Write(b.B); w.Write(b.A);
                    w.Write(b.Xyz.Count);
                    for (int i = 0; i < b.Xyz.Count; i += 3)
                    {
                        w.Write((b.Xyz[i] - cx) * ftToM);
                        w.Write((b.Xyz[i + 1] - cy) * ftToM);
                        w.Write((b.Xyz[i + 2] - cz) * ftToM);
                    }
                    categories.Add(new Model3DCategory { Name = b.Name, R = b.R, G = b.G, B = b.B, A = b.A, Triangles = b.Xyz.Count / 9 });
                }
            }

            if (truncated) ctx.Warn($"The model is large; the 3D preview shows the first {maxTriangles:N0} triangles.");
            ctx.Info($"3D: {used:N0} elements, {triangles:N0} triangles" + (view != null ? $" from view '{view.Name}'." : "."));
            return new Model3DResult
            {
                FilePath = file,
                ViewName = view?.Name ?? "Whole model",
                Categories = categories,
                Elements = used,
                Triangles = triangles,
                Truncated = truncated
            };
        }

        private static void Walk(GeometryElement geom, List<float> xyz, ref int triangles, int max)
        {
            foreach (var obj in geom)
            {
                if (triangles >= max) return;
                switch (obj)
                {
                    case Solid solid when solid.Faces.Size > 0:
                        foreach (Face face in solid.Faces)
                        {
                            Mesh m;
                            try { m = face.Triangulate(0.0); } catch { continue; }
                            AddMesh(m, xyz, ref triangles, max);
                            if (triangles >= max) return;
                        }
                        break;
                    case Mesh mesh:
                        AddMesh(mesh, xyz, ref triangles, max);
                        break;
                    case GeometryInstance instance:
                        var inner = instance.GetInstanceGeometry();
                        if (inner != null) Walk(inner, xyz, ref triangles, max);
                        break;
                }
            }
        }

        private static void AddMesh(Mesh m, List<float> xyz, ref int triangles, int max)
        {
            if (m == null) return;
            for (int i = 0; i < m.NumTriangles && triangles < max; i++)
            {
                var t = m.get_Triangle(i);
                for (int k = 0; k < 3; k++)
                {
                    var p = t.get_Vertex(k);
                    xyz.Add((float)p.X);
                    xyz.Add((float)p.Y);
                    xyz.Add((float)p.Z);
                }
                triangles++;
            }
        }

        private static ViewDetailLevel ParseDetail(string s) =>
            s == "Fine" ? ViewDetailLevel.Fine : s == "Medium" ? ViewDetailLevel.Medium : ViewDetailLevel.Coarse;

        private static void ColorFor(Category cat, BuiltInCategory bic, Bucket b)
        {
            (byte r, byte g, byte bl, byte a) c;
            switch (bic)
            {
                case BuiltInCategory.OST_Walls: c = (214, 210, 200, 255); break;
                case BuiltInCategory.OST_Floors: c = (176, 176, 170, 255); break;
                case BuiltInCategory.OST_Roofs: c = (150, 96, 80, 255); break;
                case BuiltInCategory.OST_StructuralColumns: c = (120, 150, 190, 255); break;
                case BuiltInCategory.OST_StructuralFraming: c = (90, 130, 175, 255); break;
                case BuiltInCategory.OST_StructuralFoundation: c = (150, 130, 105, 255); break;
                case BuiltInCategory.OST_Windows: c = (150, 200, 230, 140); break;
                case BuiltInCategory.OST_CurtainWallPanels: c = (150, 200, 230, 120); break;
                case BuiltInCategory.OST_Doors: c = (170, 125, 85, 255); break;
                case BuiltInCategory.OST_Stairs:
                case BuiltInCategory.OST_StairsRuns:
                case BuiltInCategory.OST_StairsLandings: c = (200, 190, 160, 255); break;
                case BuiltInCategory.OST_Rebar: c = (200, 60, 50, 255); break;
                case BuiltInCategory.OST_Topography: c = (130, 160, 110, 255); break;
                default:
                    var mc = cat.Material?.Color;
                    if (mc != null && mc.IsValid) c = (mc.Red, mc.Green, mc.Blue, 255);
                    else
                    {
                        // stable pastel from the category name
                        int h = cat.Name.Aggregate(17, (acc, ch) => acc * 31 + ch);
                        c = ((byte)(120 + (h & 0x5F)), (byte)(120 + ((h >> 7) & 0x5F)), (byte)(120 + ((h >> 14) & 0x5F)), 255);
                    }
                    break;
            }
            b.R = c.r; b.G = c.g; b.B = c.bl; b.A = c.a;
        }

        // ---------------------------------------------------------------- open a model

        /// <summary>Opens an .rvt in this Revit session and makes it the active document.</summary>
        public static string OpenModel(UIApplication app, OpenModelRequest req)
        {
            if (string.IsNullOrWhiteSpace(req?.Path) || !File.Exists(req.Path))
                throw new BridgeException("File not found: " + req?.Path);

            UIDocument uiDoc;
            if (req.Detach)
            {
                var path = ModelPathUtils.ConvertUserVisiblePathToModelPath(req.Path);
                var options = new OpenOptions { DetachFromCentralOption = DetachFromCentralOption.DetachAndPreserveWorksets };
                uiDoc = app.OpenAndActivateDocument(path, options, false);
            }
            else
            {
                uiDoc = app.OpenAndActivateDocument(req.Path);
            }
            return uiDoc?.Document?.Title;
        }
    }
}
