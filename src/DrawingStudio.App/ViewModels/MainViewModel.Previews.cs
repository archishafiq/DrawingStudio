using System;
using System.Windows.Threading;
using DrawingStudio.App.Controls;

namespace DrawingStudio.App.ViewModels
{
    /// <summary>
    /// Live previews: every few hundred milliseconds the open tab rebuilds what its Add button
    /// would prepare, so the text and drawings always match the current ticks and settings.
    /// </summary>
    public sealed partial class MainViewModel
    {
        private DispatcherTimer _previewTimer;

        private string _plansPreview = "", _sectionsPreview = "", _sheetsPreview = "", _placePreview = "",
                       _annotationPreview = "", _renumberPreview = "", _detailsPreview = "";
        private Diagram _planDiagram, _levelsDiagram, _sheetDiagram;

        public string PlansPreview { get => _plansPreview; private set => Set(ref _plansPreview, value); }
        public string SectionsPreview { get => _sectionsPreview; private set => Set(ref _sectionsPreview, value); }
        public string SheetsPreview { get => _sheetsPreview; private set => Set(ref _sheetsPreview, value); }
        public string PlacePreview { get => _placePreview; private set => Set(ref _placePreview, value); }
        public string AnnotationPreview { get => _annotationPreview; private set => Set(ref _annotationPreview, value); }
        public string RenumberPreview { get => _renumberPreview; private set => Set(ref _renumberPreview, value); }
        public string DetailsPreview { get => _detailsPreview; private set => Set(ref _detailsPreview, value); }

        public Diagram PlanDiagram { get => _planDiagram; private set => Set(ref _planDiagram, value); }
        public Diagram LevelsDiagram { get => _levelsDiagram; private set => Set(ref _levelsDiagram, value); }
        public Diagram SheetDiagram { get => _sheetDiagram; private set => Set(ref _sheetDiagram, value); }

        private void InitPreviews()
        {
            _previewTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
            _previewTimer.Tick += (_, __) => UpdatePreviews();
            _previewTimer.Start();
        }

        private void UpdatePreviews()
        {
            try
            {
                switch (SelectedTab)
                {
                    case 0:
                        PlansPreview = BuildPlans().PreviewText;
                        SectionsPreview = BuildSections().PreviewText;
                        PlanDiagram = Keep(PlanDiagram, BuildPlanDiagram());
                        LevelsDiagram = Keep(LevelsDiagram, BuildLevelsDiagram());
                        break;
                    case 1:
                        SheetsPreview = PlannedSheets.Count == 0 ? "" : BuildSheets().PreviewText;
                        PlacePreview = TargetSheet == null ? "" : BuildPlace(false).PreviewText;
                        SheetDiagram = Keep(SheetDiagram, BuildSheetDiagram());
                        break;
                    case 2:
                        AnnotationPreview = BuildAnnotation().PreviewText;
                        break;
                    case 3:
                        RenumberPreview = BuildRenumberSheets().PreviewText;
                        DetailsPreview = BuildRenumberDetails().PreviewText;
                        break;
                }
            }
            catch (Exception ex)
            {
                // A preview must never break the app; show why it could not be built.
                Status = "Preview: " + ex.Message;
            }
        }

        /// <summary>Only swap the drawing when something changed, so it does not flicker.</summary>
        private static Diagram Keep(Diagram current, Diagram next) =>
            current != null && next != null && current.Key == next.Key ? current : next;
    }
}
