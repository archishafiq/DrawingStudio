using System.Collections.Generic;

namespace DrawingStudio.Shared
{
    // ---------------------------------------------------------------- model info

    public class LevelInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public double ElevationMm { get; set; }
        public string Code { get; set; }
    }

    public class GridInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public bool IsLinear { get; set; }
        /// <summary>Plan coordinates of the grid line ends, mm.</summary>
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
    }

    public class ViewTemplateInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string ViewType { get; set; }
        public override string ToString() => Name;
    }

    public class ViewInfo
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string ViewType { get; set; }
        public int Scale { get; set; }
        public string LevelName { get; set; }
        public bool IsPlaced { get; set; }
        public string SheetNumber { get; set; }
        public bool HasTemplate { get; set; }
        /// <summary>Size of the view when placed on a sheet, mm (0 if unknown).</summary>
        public double PaperWidthMm { get; set; }
        public double PaperHeightMm { get; set; }
    }

    public class SheetInfo
    {
        public long Id { get; set; }
        public string Number { get; set; }
        public string Name { get; set; }
        public int ViewCount { get; set; }
        /// <summary>Title block outline, mm (0 if the sheet has none).</summary>
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public long TitleBlockTypeId { get; set; }
        public List<SheetViewport> Viewports { get; set; }
        public override string ToString() => Number + " - " + Name;
    }

    /// <summary>A viewport already on a sheet; centre relative to the title block's lower-left corner, mm.</summary>
    public class SheetViewport
    {
        public long ViewId { get; set; }
        public string Name { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double Width { get; set; }
        public double Height { get; set; }
    }

    public class TitleBlockInfo
    {
        public long Id { get; set; }
        public string Family { get; set; }
        public string Type { get; set; }
        /// <summary>Paper size, mm (0 if unknown).</summary>
        public double WidthMm { get; set; }
        public double HeightMm { get; set; }
        public override string ToString() => Family + " : " + Type;
    }

    public class ModelInfo
    {
        public string DocumentTitle { get; set; }
        public string RevitVersion { get; set; }
        public long ActiveViewId { get; set; }
        public List<LevelInfo> Levels { get; set; }
        public List<GridInfo> Grids { get; set; }
        public List<ViewInfo> Views { get; set; }
        public List<ViewTemplateInfo> Templates { get; set; }
        public List<SheetInfo> Sheets { get; set; }
        public List<TitleBlockInfo> TitleBlocks { get; set; }
        public List<long> SelectedIds { get; set; }
    }

    // ---------------------------------------------------------------- views

    public class CreatePlansRequest
    {
        public List<long> LevelIds { get; set; }
        /// <summary>FloorPlan | StructuralPlan | CeilingPlan</summary>
        public string PlanKind { get; set; }
        public long ViewTemplateId { get; set; }
        public int Scale { get; set; }
        public string NameTemplate { get; set; }
        public string Role { get; set; }
        public bool Uppercase { get; set; }
    }

    public class CreateSectionsRequest
    {
        /// <summary>Grids | ElementsLongitudinal | ElementsCross</summary>
        public string Mode { get; set; }
        public List<long> GridIds { get; set; }
        /// <summary>Element ids; when empty the current Revit selection is used.</summary>
        public List<long> ElementIds { get; set; }
        public double DepthMm { get; set; }
        public double OffsetMm { get; set; }
        public double PaddingMm { get; set; }
        public bool Flip { get; set; }
        public long ViewTemplateId { get; set; }
        public int Scale { get; set; }
        public string NameTemplate { get; set; }
        public string Role { get; set; }
        public bool Uppercase { get; set; }
    }

    public class CreatedItem
    {
        public long Id { get; set; }
        public string Name { get; set; }
    }

    public class CreateResult
    {
        public List<CreatedItem> Created { get; set; }
        public int Skipped { get; set; }
    }

    // ---------------------------------------------------------------- sheets

    public class NameValue
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }

    public class IdValue
    {
        public long Id { get; set; }
        public string Value { get; set; }
    }

    public class SheetPlan
    {
        public string Number { get; set; }
        public string Name { get; set; }
        public List<long> ViewIds { get; set; }
    }

    public class LayoutSettings
    {
        public double MarginLeftMm { get; set; }
        public double MarginOtherMm { get; set; }
        public double TitleStripMm { get; set; }
        /// <summary>Right | Bottom</summary>
        public string TitleStripSide { get; set; }
        public double GapMm { get; set; }
        public bool RenumberDetails { get; set; }
    }

    public class CreateSheetsRequest
    {
        public long TitleBlockId { get; set; }
        public List<SheetPlan> Sheets { get; set; }
        public List<NameValue> Parameters { get; set; }
        public bool AutoPlace { get; set; }
        public LayoutSettings Layout { get; set; }
    }

    public class PlaceViewsRequest
    {
        public long SheetId { get; set; }
        public List<long> ViewIds { get; set; }
        public LayoutSettings Layout { get; set; }
        /// <summary>Also re-arrange viewports already on the sheet.</summary>
        public bool RelayoutExisting { get; set; }
    }

    public class RenumberSheetsRequest
    {
        public List<IdValue> Assignments { get; set; }
    }

    public class RenumberViewportsRequest
    {
        public List<long> SheetIds { get; set; }
        public int Start { get; set; }
    }

    // ---------------------------------------------------------------- annotation

    public class AnnotateRequest
    {
        public List<long> ViewIds { get; set; }
        public bool DimensionGrids { get; set; }
        public bool OverallDimension { get; set; }
        public bool BothSides { get; set; }
        public bool GridBubblesBothEnds { get; set; }
        public bool TagElements { get; set; }
        /// <summary>BuiltInCategory names, e.g. OST_StructuralColumns.</summary>
        public List<string> TagCategories { get; set; }
        public bool TagLeader { get; set; }
        public bool SkipTagged { get; set; }
        /// <summary>Paper distance from grid ends to the outer dimension string.</summary>
        public double DimOffsetMm { get; set; }
        /// <summary>Paper distance between the overall and chain strings.</summary>
        public double DimGapMm { get; set; }
    }

    public class AnnotateResult
    {
        public int ViewsProcessed { get; set; }
        public int Dimensions { get; set; }
        public int Tags { get; set; }
        public int TagFailures { get; set; }
        public int Bubbles { get; set; }
    }

    // ---------------------------------------------------------------- QA

    public static class QaChecks
    {
        public const string UnplacedViews = "UnplacedViews";
        public const string NoTemplate = "NoTemplate";
        public const string ViewNaming = "ViewNaming";
        public const string SheetNumbering = "SheetNumbering";
        public const string EmptySheets = "EmptySheets";
        public const string SheetParams = "SheetParams";
        public const string NonStandardScale = "NonStandardScale";
        public const string UntaggedElements = "UntaggedElements";
    }

    public class QaRequest
    {
        public List<string> Checks { get; set; }
        public List<string> TagCategories { get; set; }
        public string ViewNameRegex { get; set; }
        public string SheetNumberRegex { get; set; }
        public List<int> AllowedScales { get; set; }
        public List<string> RequiredSheetParams { get; set; }
    }

    public class QaIssue
    {
        public string Severity { get; set; }
        public string Check { get; set; }
        public long ElementId { get; set; }
        public string ElementName { get; set; }
        public string Message { get; set; }
    }

    public class QaResult
    {
        public List<QaIssue> Issues { get; set; }
    }

    /// <summary>An element selected in Revit, with its plan axis for previews (mm).</summary>
    public class SelectedElement
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public double X1 { get; set; }
        public double Y1 { get; set; }
        public double X2 { get; set; }
        public double Y2 { get; set; }
        /// <summary>Half the element's width across its axis, mm.</summary>
        public double HalfWidthMm { get; set; }
    }

    public class ShowElementsRequest
    {
        public List<long> Ids { get; set; }
    }

    // ---------------------------------------------------------------- batch apply

    /// <summary>Several prepared changes applied in one Revit transaction group (one undo step).</summary>
    public class BatchRequest
    {
        public List<BridgeRequest> Items { get; set; }
        /// <summary>Roll back everything if any item fails.</summary>
        public bool AllOrNothing { get; set; }
    }

    public class BatchItemResult
    {
        public string Id { get; set; }
        public bool Ok { get; set; }
        public string Error { get; set; }
        public string Payload { get; set; }
    }

    public class BatchResult
    {
        public List<BatchItemResult> Items { get; set; }
        public bool RolledBack { get; set; }
    }

    /// <summary>A change prepared in the app and waiting to be applied to Revit. Saved between sessions.</summary>
    public class PendingChange
    {
        public string Id { get; set; }
        public string Kind { get; set; }
        public string Title { get; set; }
        public string Summary { get; set; }
        public string Command { get; set; }
        public string Payload { get; set; }
        public string Document { get; set; }
        public bool Included { get; set; }
        public string CreatedLocal { get; set; }
    }

    public class PendingChangeList
    {
        public List<PendingChange> Items { get; set; }
    }
}
