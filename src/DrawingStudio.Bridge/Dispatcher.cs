using System;
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
            var uiDoc = app.ActiveUIDocument;
            if (uiDoc == null)
                return BridgeResponse.Fail(request.Id, "No Revit project is open. Open a project and try again.");
            if (uiDoc.Document.IsFamilyDocument)
                return BridgeResponse.Fail(request.Id, "The active document is a family. Switch to a project.");

            var ctx = new HandlerContext(app, uiDoc);
            string payload;

            switch (request.Command)
            {
                case Commands.ModelInfo:
                    payload = Json.Serialize(ModelInfoHandler.Run(ctx));
                    break;
                case Commands.CreatePlans:
                    payload = Json.Serialize(ViewHandlers.CreatePlans(ctx, Json.Deserialize<CreatePlansRequest>(request.Payload)));
                    break;
                case Commands.CreateSections:
                    payload = Json.Serialize(ViewHandlers.CreateSections(ctx, Json.Deserialize<CreateSectionsRequest>(request.Payload)));
                    break;
                case Commands.CreateSheets:
                    payload = Json.Serialize(SheetHandlers.CreateSheets(ctx, Json.Deserialize<CreateSheetsRequest>(request.Payload)));
                    break;
                case Commands.PlaceViews:
                    payload = Json.Serialize(SheetHandlers.PlaceViews(ctx, Json.Deserialize<PlaceViewsRequest>(request.Payload)));
                    break;
                case Commands.RenumberSheets:
                    payload = Json.Serialize(SheetHandlers.RenumberSheets(ctx, Json.Deserialize<RenumberSheetsRequest>(request.Payload)));
                    break;
                case Commands.RenumberViewports:
                    payload = Json.Serialize(SheetHandlers.RenumberViewports(ctx, Json.Deserialize<RenumberViewportsRequest>(request.Payload)));
                    break;
                case Commands.Annotate:
                    payload = Json.Serialize(AnnotationHandler.Run(ctx, Json.Deserialize<AnnotateRequest>(request.Payload)));
                    break;
                case Commands.Qa:
                    payload = Json.Serialize(QaHandler.Run(ctx, Json.Deserialize<QaRequest>(request.Payload)));
                    break;
                case Commands.ShowElements:
                    payload = Json.Serialize(ModelInfoHandler.ShowElements(ctx, Json.Deserialize<ShowElementsRequest>(request.Payload)));
                    break;
                default:
                    return BridgeResponse.Fail(request.Id, "Unknown command: " + request.Command);
            }

            return BridgeResponse.Success(request.Id, payload, ctx.Log);
        }
    }

    internal sealed class BridgeException : Exception
    {
        public BridgeException(string message) : base(message) { }
    }
}
