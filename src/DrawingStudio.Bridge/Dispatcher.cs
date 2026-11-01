using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using DrawingStudio.Bridge.Handlers;
using DrawingStudio.Shared;

namespace DrawingStudio.Bridge
{
    /// <summary>Routes a request to its handler. Always runs on Revit's API thread.</summary>
    internal static class Dispatcher
    {
        public static BridgeResponse Handle(UIApplication app, BridgeRequest request)
        {
            // Opening a model works even when no project is open yet.
            if (request.Command == Commands.OpenModel)
            {
                try
                {
                    var title = ModelViewHandler.OpenModel(app, Json.Deserialize<OpenModelRequest>(request.Payload));
                    return BridgeResponse.Success(request.Id, Json.Serialize(title ?? ""));
                }
                catch (Exception ex)
                {
                    return BridgeResponse.Fail(request.Id, "Could not open the model: " + ex.Message);
                }
            }

            var uiDoc = app.ActiveUIDocument;
            if (uiDoc == null)
                return BridgeResponse.Fail(request.Id, "No Revit project is open. Open a project and try again.");
            if (uiDoc.Document.IsFamilyDocument)
                return BridgeResponse.Fail(request.Id, "The active document is a family. Switch to a project.");

            var ctx = new HandlerContext(app, uiDoc);

            if (request.Command == Commands.Batch)
                return BridgeResponse.Success(request.Id, Json.Serialize(RunBatch(ctx, Json.Deserialize<BatchRequest>(request.Payload))), ctx.Log);

            var payload = Run(ctx, request);
            return payload == null
                ? BridgeResponse.Fail(request.Id, "Unknown command: " + request.Command)
                : BridgeResponse.Success(request.Id, payload, ctx.Log);
        }

        /// <summary>Runs one command and returns its JSON result, or null for an unknown command.</summary>
        private static string Run(HandlerContext ctx, BridgeRequest request)
        {
            switch (request.Command)
            {
                case Commands.ModelInfo:
                    return Json.Serialize(ModelInfoHandler.Run(ctx));
                case Commands.Selection:
                    return Json.Serialize(ModelInfoHandler.Selection(ctx));
                case Commands.CreatePlans:
                    return Json.Serialize(ViewHandlers.CreatePlans(ctx, Json.Deserialize<CreatePlansRequest>(request.Payload)));
                case Commands.CreateSections:
                    return Json.Serialize(ViewHandlers.CreateSections(ctx, Json.Deserialize<CreateSectionsRequest>(request.Payload)));
                case Commands.CreateSheets:
                    return Json.Serialize(SheetHandlers.CreateSheets(ctx, Json.Deserialize<CreateSheetsRequest>(request.Payload)));
                case Commands.PlaceViews:
                    return Json.Serialize(SheetHandlers.PlaceViews(ctx, Json.Deserialize<PlaceViewsRequest>(request.Payload)));
                case Commands.RenumberSheets:
                    return Json.Serialize(SheetHandlers.RenumberSheets(ctx, Json.Deserialize<RenumberSheetsRequest>(request.Payload)));
                case Commands.RenumberViewports:
                    return Json.Serialize(SheetHandlers.RenumberViewports(ctx, Json.Deserialize<RenumberViewportsRequest>(request.Payload)));
                case Commands.Annotate:
                    return Json.Serialize(AnnotationHandler.Run(ctx, Json.Deserialize<AnnotateRequest>(request.Payload)));
                case Commands.Qa:
                    return Json.Serialize(QaHandler.Run(ctx, Json.Deserialize<QaRequest>(request.Payload)));
                case Commands.RenderView:
                    return Json.Serialize(ModelViewHandler.RenderView(ctx, Json.Deserialize<RenderViewRequest>(request.Payload)));
                case Commands.Model3D:
                    return Json.Serialize(ModelViewHandler.ExportModel(ctx, Json.Deserialize<Model3DRequest>(request.Payload)));
                case Commands.ShowElements:
                    return Json.Serialize(ModelInfoHandler.ShowElements(ctx, Json.Deserialize<ShowElementsRequest>(request.Payload)));
                default:
                    return null;
            }
        }

        /// <summary>
        /// Applies prepared changes in order inside one TransactionGroup, so Revit shows a
        /// single "Drawing Studio: Apply n changes" entry and one Ctrl+Z undoes all of it.
        /// </summary>
        private static BatchResult RunBatch(HandlerContext ctx, BatchRequest batch)
        {
            var items = batch?.Items.OrEmpty() ?? new List<BridgeRequest>();
            var result = new BatchResult { Items = new List<BatchItemResult>() };

            using (var group = new TransactionGroup(ctx.Doc, $"Drawing Studio: Apply {items.Count} change(s)"))
            {
                group.Start();
                bool failed = false;

                foreach (var item in items)
                {
                    if (item.Command == Commands.Batch) continue; // no nesting
                    var r = new BatchItemResult { Id = item.Id };
                    try
                    {
                        r.Payload = Run(ctx, item);
                        r.Ok = r.Payload != null;
                        if (!r.Ok) r.Error = "Unknown command: " + item.Command;
                    }
                    catch (Exception ex)
                    {
                        r.Ok = false;
                        r.Error = ex.Message;
                    }
                    result.Items.Add(r);

                    if (!r.Ok)
                    {
                        failed = true;
                        ctx.Warn("Change failed: " + r.Error);
                        if (batch.AllOrNothing) break;
                    }
                }

                if (failed && batch.AllOrNothing)
                {
                    group.RollBack();
                    result.RolledBack = true;
                    ctx.Warn("Nothing was changed in Revit because one change failed (all-or-nothing is on).");
                }
                else
                {
                    group.Assimilate();
                    ctx.Info($"Applied {result.Items.Count(i => i.Ok)} of {items.Count} change(s). Ctrl+Z in Revit undoes them in one step.");
                }
            }
            return result;
        }
    }

    internal sealed class BridgeException : Exception
    {
        public BridgeException(string message) : base(message) { }
    }
}
