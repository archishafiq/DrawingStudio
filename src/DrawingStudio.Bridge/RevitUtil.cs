using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

namespace DrawingStudio.Bridge
{
    internal sealed class HandlerContext
    {
        public HandlerContext(UIApplication app, UIDocument uiDoc)
        {
            App = app;
            UiDoc = uiDoc;
        }

        public UIApplication App { get; }
        public UIDocument UiDoc { get; }
        public Document Doc => UiDoc.Document;
        public List<string> Log { get; } = new List<string>();

        public void Info(string message) => Log.Add(message);
        public void Warn(string message) => Log.Add("WARNING: " + message);
    }

    /// <summary>ElementId changed from int to long in Revit 2024.</summary>
    internal static class Ids
    {
        public static long Of(ElementId id)
        {
            if (id == null) return -1;
#if REVIT2024_OR_GREATER
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        public static ElementId To(long value)
        {
            if (value <= 0) return ElementId.InvalidElementId;
#if REVIT2024_OR_GREATER
            return new ElementId(value);
#else
            return new ElementId((int)value);
#endif
        }

        public static T Get<T>(Document doc, long id) where T : Element =>
            id <= 0 ? null : doc.GetElement(To(id)) as T;
    }

    internal static class Mm
    {
        public const double PerFoot = 304.8;
        public static double ToFt(double mm) => mm / PerFoot;
        public static double FromFt(double ft) => ft * PerFoot;
    }

    /// <summary>Accepts warnings silently so batch operations are not interrupted by dialogs.</summary>
    internal sealed class WarningSwallower : IFailuresPreprocessor
    {
        public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
        {
            foreach (var failure in accessor.GetFailureMessages())
                if (failure.GetSeverity() == FailureSeverity.Warning)
                    accessor.DeleteWarning(failure);
            return FailureProcessingResult.Continue;
        }
    }

    internal static class RevitUtil
    {
        public static Transaction StartTransaction(Document doc, string name)
        {
            var t = new Transaction(doc, "Drawing Studio: " + name);
            t.Start();
            var options = t.GetFailureHandlingOptions();
            options.SetFailuresPreprocessor(new WarningSwallower());
            options.SetClearAfterRollback(true);
            t.SetFailureHandlingOptions(options);
            return t;
        }

        public static ViewFamilyType FirstViewFamilyType(Document doc, ViewFamily family) =>
            new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType))
                .Cast<ViewFamilyType>()
                .FirstOrDefault(v => v.ViewFamily == family);

        public static HashSet<string> ViewNames(Document doc) =>
            new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name),
                StringComparer.OrdinalIgnoreCase);

        public static HashSet<string> SheetNumbers(Document doc) =>
            new HashSet<string>(
                new FilteredElementCollector(doc).OfClass(typeof(ViewSheet)).Cast<ViewSheet>().Select(s => s.SheetNumber),
                StringComparer.OrdinalIgnoreCase);

        public static void ApplyScaleAndTemplate(HandlerContext ctx, View view, int scale, long templateId)
        {
            if (scale > 0)
            {
                try { view.Scale = scale; }
                catch (Exception ex) { ctx.Warn($"{view.Name}: scale not set ({ex.Message})"); }
            }
            if (templateId > 0)
            {
                var template = Ids.Get<View>(ctx.Doc, templateId);
                if (template == null || !template.IsTemplate) { ctx.Warn("Selected view template no longer exists."); return; }
                try { view.ViewTemplateId = template.Id; }
                catch (Exception ex) { ctx.Warn($"{view.Name}: template '{template.Name}' not applied ({ex.Message})"); }
            }
        }

        public static bool SetParameter(Element element, string name, string value)
        {
            if (element == null || string.IsNullOrWhiteSpace(name)) return false;
            var p = element.LookupParameter(name);
            if (p == null || p.IsReadOnly) return false;
            switch (p.StorageType)
            {
                case StorageType.String:
                    return p.Set(value ?? string.Empty);
                case StorageType.Integer:
                    return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && p.Set(i);
                case StorageType.Double:
                    return p.SetValueString(value);
                default:
                    return false;
            }
        }

        public static string ParameterText(Element element, string name)
        {
            var p = element?.LookupParameter(name);
            if (p == null) return null;
            return p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
        }

        public static FamilyInstance TitleBlockOn(Document doc, ViewSheet sheet) =>
            new FilteredElementCollector(doc, sheet.Id)
                .OfCategory(BuiltInCategory.OST_TitleBlocks)
                .WhereElementIsNotElementType()
                .FirstElement() as FamilyInstance;

        public static bool IsDrawingView(View v) =>
            !v.IsTemplate && v.CanBePrinted &&
            (v.ViewType == ViewType.FloorPlan ||
             v.ViewType == ViewType.CeilingPlan ||
             v.ViewType == ViewType.EngineeringPlan ||
             v.ViewType == ViewType.AreaPlan ||
             v.ViewType == ViewType.Section ||
             v.ViewType == ViewType.Elevation ||
             v.ViewType == ViewType.Detail ||
             v.ViewType == ViewType.DraftingView ||
             v.ViewType == ViewType.ThreeD);

        public static string MarkOrId(Element e)
        {
            var mark = e.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString();
            return string.IsNullOrWhiteSpace(mark) ? Ids.Of(e.Id).ToString(CultureInfo.InvariantCulture) : mark;
        }
    }
}
