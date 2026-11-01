using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DrawingStudio.Shared
{
    /// <summary>
    /// Project drawing standard. Defaults follow ISO 19650-2 (information container
    /// naming and suitability codes), ISO 5455 (preferred scales), ISO 5457 (sheet
    /// borders) and ISO 7200 (title block fields). Everything is editable per project.
    /// </summary>
    public class StandardsConfig
    {
        // ISO 19650 container-name fields
        public string ProjectCode { get; set; }
        public string Originator { get; set; }
        public string Volume { get; set; }
        public string DefaultLevelCode { get; set; }
        public string InfoType { get; set; }
        public string Role { get; set; }
        public int NumberDigits { get; set; }
        public int StartNumber { get; set; }
        public int NumberStep { get; set; }

        // Name templates. Tokens: {Project} {Originator} {Volume} {Level} {Type} {Role} {Number}
        // views: {Role} {PlanKind} {LevelName} {LevelCode} {Ref} {Kind} {Index} {Scale}
        public string SheetNumberTemplate { get; set; }
        public string PlanNameTemplate { get; set; }
        public string SectionNameTemplate { get; set; }
        public string ViewNameRegex { get; set; }
        public bool Uppercase { get; set; }

        // Scales (ISO 5455)
        public int PlanScale { get; set; }
        public int SectionScale { get; set; }
        public string AllowedScalesText { get; set; }

        // Sheet layout (ISO 5457 borders, title strip per ISO 7200)
        public double MarginLeftMm { get; set; }
        public double MarginOtherMm { get; set; }
        public double TitleStripMm { get; set; }
        public string TitleStripSide { get; set; }
        public double ViewportGapMm { get; set; }

        // Title block values and the Revit parameter names that receive them
        public string DrawnBy { get; set; }
        public string CheckedBy { get; set; }
        public string ApprovedBy { get; set; }
        public string Suitability { get; set; }
        public string Revision { get; set; }
        public string ParamDrawnBy { get; set; }
        public string ParamCheckedBy { get; set; }
        public string ParamApprovedBy { get; set; }
        public string ParamSuitability { get; set; }
        public string ParamRevision { get; set; }
        public string RequiredSheetParamsText { get; set; }

        // Annotation and sections
        public double DimOffsetMm { get; set; }
        public double DimGapMm { get; set; }
        public double SectionDepthMm { get; set; }
        public double SectionOffsetMm { get; set; }
        public double SectionPaddingMm { get; set; }

        public static readonly string[] SuitabilityCodes =
        {
            "S0", "S1", "S2", "S3", "S4", "S5", "S6", "S7",
            "A1", "A2", "A3", "A4", "A5", "B1", "B2", "B3", "CR"
        };

        public static StandardsConfig CreateDefault() => new StandardsConfig
        {
            ProjectCode = "PRJ",
            Originator = "ORG",
            Volume = "ZZ",
            DefaultLevelCode = "ZZ",
            InfoType = "DR",
            Role = "S",
            NumberDigits = 4,
            StartNumber = 1,
            NumberStep = 1,
            SheetNumberTemplate = "{Project}-{Originator}-{Volume}-{Level}-{Type}-{Role}-{Number}",
            PlanNameTemplate = "{Role} - {PlanKind} - {LevelName}",
            SectionNameTemplate = "{Role} - {Kind} {Ref}",
            ViewNameRegex = @"^[A-Z]{1,2} - .+",
            Uppercase = true,
            PlanScale = 100,
            SectionScale = 50,
            AllowedScalesText = "1,2,5,10,20,50,100,200,500,1000,2000,5000",
            MarginLeftMm = 20,
            MarginOtherMm = 10,
            TitleStripMm = 190,
            TitleStripSide = "Right",
            ViewportGapMm = 15,
            DrawnBy = "",
            CheckedBy = "",
            ApprovedBy = "",
            Suitability = "S2",
            Revision = "P01",
            ParamDrawnBy = "Drawn By",
            ParamCheckedBy = "Checked By",
            ParamApprovedBy = "Approved By",
            ParamSuitability = "Suitability Code",
            ParamRevision = "Revision Code",
            RequiredSheetParamsText = "Drawn By, Checked By",
            DimOffsetMm = 12,
            DimGapMm = 8,
            SectionDepthMm = 6000,
            SectionOffsetMm = 0,
            SectionPaddingMm = 1000
        };

        /// <summary>DataContract deserialization skips initializers, so fill gaps from defaults.</summary>
        public void FillDefaults()
        {
            var d = CreateDefault();
            foreach (var p in typeof(StandardsConfig).GetProperties())
            {
                if (!p.CanWrite) continue;
                var value = p.GetValue(this, null);
                bool empty =
                    value == null ||
                    (p.PropertyType == typeof(int) && (int)value == 0 && p.Name != nameof(SectionOffsetMm)) ||
                    (p.PropertyType == typeof(double) && (double)value == 0 && p.Name != nameof(SectionOffsetMm));
                if (empty) p.SetValue(this, p.GetValue(d, null), null);
            }
        }

        public List<int> GetAllowedScales() => SplitList(AllowedScalesText)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0)
            .Where(n => n > 0).ToList();

        public List<string> GetRequiredSheetParams() => SplitList(RequiredSheetParamsText);

        public LayoutSettings ToLayout(bool renumberDetails) => new LayoutSettings
        {
            MarginLeftMm = MarginLeftMm,
            MarginOtherMm = MarginOtherMm,
            TitleStripMm = TitleStripMm,
            TitleStripSide = TitleStripSide,
            GapMm = ViewportGapMm,
            RenumberDetails = renumberDetails
        };

        public List<NameValue> TitleBlockValues()
        {
            var list = new List<NameValue>();
            void Add(string param, string value)
            {
                if (!string.IsNullOrWhiteSpace(param) && !string.IsNullOrWhiteSpace(value))
                    list.Add(new NameValue { Name = param.Trim(), Value = value.Trim() });
            }
            Add(ParamDrawnBy, DrawnBy);
            Add(ParamCheckedBy, CheckedBy);
            Add(ParamApprovedBy, ApprovedBy);
            Add(ParamSuitability, Suitability);
            Add(ParamRevision, Revision);
            return list;
        }

        private static List<string> SplitList(string text) =>
            (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
    }
}
