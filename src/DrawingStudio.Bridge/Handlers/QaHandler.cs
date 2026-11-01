using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.Revit.DB;
using DrawingStudio.Shared;

namespace DrawingStudio.Bridge.Handlers
{
    /// <summary>Drawing-set checks against the project standard. Read-only.</summary>
    internal static class QaHandler
    {
        private const int MaxElementIssuesPerView = 50;

        public static QaResult Run(HandlerContext ctx, QaRequest req)
        {
            var doc = ctx.Doc;
            var checks = new HashSet<string>(req.Checks.OrEmpty());
            var issues = new List<QaIssue>();

            var sheets = new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>()
                .Where(s => !s.IsPlaceholder).ToList();
            var placed = new HashSet<ElementId>(sheets.SelectMany(s => s.GetAllPlacedViews()));
            var views = new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>()
                .Where(RevitUtil.IsDrawingView).ToList();

            void Add(string severity, string check, Element e, string message) =>
                issues.Add(new QaIssue
                {
                    Severity = severity,
                    Check = check,
                    ElementId = Ids.Of(e?.Id),
                    ElementName = e is ViewSheet sh ? sh.SheetNumber + " - " + sh.Name : e?.Name,
                    Message = message
                });

            if (checks.Contains(QaChecks.UnplacedViews))
                foreach (var v in views.Where(v => !placed.Contains(v.Id) && v.ViewType != ViewType.ThreeD))
                    Add("Info", QaChecks.UnplacedViews, v, "View is not placed on any sheet.");

            if (checks.Contains(QaChecks.NoTemplate))
                foreach (var v in views.Where(v => placed.Contains(v.Id) && v.ViewTemplateId == ElementId.InvalidElementId
                                                   && v.ViewType != ViewType.DraftingView))
                    Add("Warning", QaChecks.NoTemplate, v, "Placed view has no view template.");

            if (checks.Contains(QaChecks.ViewNaming) && !string.IsNullOrWhiteSpace(req.ViewNameRegex))
            {
                var rx = SafeRegex(ctx, req.ViewNameRegex);
                if (rx != null)
                    foreach (var v in views.Where(v => placed.Contains(v.Id) && !rx.IsMatch(v.Name)))
                        Add("Warning", QaChecks.ViewNaming, v, "View name does not follow the naming standard.");
            }

            if (checks.Contains(QaChecks.SheetNumbering) && !string.IsNullOrWhiteSpace(req.SheetNumberRegex))
            {
                var rx = SafeRegex(ctx, req.SheetNumberRegex);
                if (rx != null)
                    foreach (var s in sheets.Where(s => !rx.IsMatch(s.SheetNumber)))
                        Add("Error", QaChecks.SheetNumbering, s, "Sheet number is not an ISO 19650 container name.");
            }

            if (checks.Contains(QaChecks.EmptySheets))
                foreach (var s in sheets.Where(s => s.GetAllPlacedViews().Count == 0 && !HasSchedules(doc, s)))
                    Add("Warning", QaChecks.EmptySheets, s, "Sheet has no views or schedules.");

            if (checks.Contains(QaChecks.SheetParams))
            {
                foreach (var s in sheets)
                {
                    var tb = RevitUtil.TitleBlockOn(doc, s);
                    foreach (var p in req.RequiredSheetParams.OrEmpty())
                    {
                        var value = RevitUtil.ParameterText(s, p) ?? RevitUtil.ParameterText(tb, p);
                        if (string.IsNullOrWhiteSpace(value))
                            Add("Warning", QaChecks.SheetParams, s, $"'{p}' is empty or missing.");
                    }
                }
            }

            if (checks.Contains(QaChecks.NonStandardScale) && req.AllowedScales.OrEmpty().Count > 0)
            {
                var allowed = new HashSet<int>(req.AllowedScales);
                foreach (var v in views.Where(v => placed.Contains(v.Id) && v.ViewType != ViewType.ThreeD
                                                   && v.ViewType != ViewType.DraftingView && !allowed.Contains(v.Scale)))
                    Add("Warning", QaChecks.NonStandardScale, v, $"Scale 1:{v.Scale} is not an ISO 5455 preferred scale.");
            }

            if (checks.Contains(QaChecks.UntaggedElements))
                CheckUntagged(doc, views.Where(v => placed.Contains(v.Id)), req.TagCategories.OrEmpty(), Add);

            ctx.Info($"QA found {issues.Count} issue(s).");
            return new QaResult { Issues = issues };
        }

        private static void CheckUntagged(Document doc, IEnumerable<View> views, List<string> categoryNames,
            Action<string, string, Element, string> add)
        {
            var categories = new List<BuiltInCategory>();
            foreach (var name in categoryNames)
                if (Enum.TryParse(name, out BuiltInCategory bic)) categories.Add(bic);
            if (categories.Count == 0) return;

            foreach (var view in views.Where(v => v.ViewType != ViewType.ThreeD && v.ViewType != ViewType.DraftingView))
            {
                var tagged = new HashSet<ElementId>(
                    new FilteredElementCollector(doc, view.Id).OfClass(typeof(IndependentTag)).Cast<IndependentTag>()
                        .SelectMany(t => t.GetTaggedLocalElementIds()));

                int missing = 0;
                foreach (var bic in categories)
                    missing += new FilteredElementCollector(doc, view.Id).OfCategory(bic).WhereElementIsNotElementType()
                        .ToElementIds().Count(id => !tagged.Contains(id));

                if (missing > 0)
                    add("Info", QaChecks.UntaggedElements, view,
                        missing > MaxElementIssuesPerView
                            ? $"{missing} elements untagged (run Auto annotation on this view)."
                            : $"{missing} element(s) untagged.");
            }
        }

        private static bool HasSchedules(Document doc, ViewSheet sheet) =>
            new FilteredElementCollector(doc, sheet.Id).OfClass(typeof(ScheduleSheetInstance))
                .Cast<ScheduleSheetInstance>().Any(s => !s.IsTitleblockRevisionSchedule);

        private static Regex SafeRegex(HandlerContext ctx, string pattern)
        {
            try { return new Regex(pattern); }
            catch (ArgumentException ex)
            {
                ctx.Warn($"Invalid pattern '{pattern}': {ex.Message}");
                return null;
            }
        }
    }
}
