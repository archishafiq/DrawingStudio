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
                    .Select(g => new GridInfo { Id = Ids.Of(g.Id), Name = g.Name, IsLinear = g.Curve is Line })
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
                        HasTemplate = v.ViewTemplateId != ElementId.InvalidElementId
                    })
                    .OrderBy(v => v.ViewType).ThenBy(v => Naming.NaturalKey(v.Name)).ToList(),

                Templates = allViews.Where(v => v.IsTemplate)
                    .Select(v => new ViewTemplateInfo { Id = Ids.Of(v.Id), Name = v.Name, ViewType = v.ViewType.ToString() })
                    .OrderBy(t => t.Name).ToList(),

                Sheets = sheets
                    .Select(s => new SheetInfo
                    {
                        Id = Ids.Of(s.Id),
                        Number = s.SheetNumber,
                        Name = s.Name,
                        ViewCount = s.GetAllPlacedViews().Count
                    })
                    .NaturalOrder(s => s.Number).ToList(),

                TitleBlocks = new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_TitleBlocks)
                    .WhereElementIsElementType().OfType<FamilySymbol>()
                    .Select(f => new TitleBlockInfo { Id = Ids.Of(f.Id), Family = f.FamilyName, Type = f.Name })
                    .OrderBy(t => t.Family).ThenBy(t => t.Type).ToList(),

                SelectedIds = ctx.UiDoc.Selection.GetElementIds().Select(Ids.Of).ToList()
            };
        }

        /// <summary>Elements currently selected in Revit, with a readable label each.</summary>
        public static List<CreatedItem> Selection(HandlerContext ctx) =>
            ctx.UiDoc.Selection.GetElementIds()
                .Select(id => ctx.Doc.GetElement(id))
                .Where(e => e != null && !(e is View))
                .Select(e => new CreatedItem
                {
                    Id = Ids.Of(e.Id),
                    Name = (e.Category?.Name ?? e.GetType().Name) + " " + RevitUtil.MarkOrId(e)
                })
                .ToList();

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
