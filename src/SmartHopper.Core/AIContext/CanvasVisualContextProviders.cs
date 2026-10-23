using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Grasshopper;
using Newtonsoft.Json;
using Rhino;
using SmartHopper.Infrastructure.AIContext;

namespace SmartHopper.Core.AIContext
{
    /// <summary>Captures bounded metadata on the UI thread, without screenshots or document serialization.</summary>
    internal static class VisualContextCapture
    {
        internal static Dictionary<string, string> Capture(Func<Dictionary<string, string>> capture)
        {
            var completion = new TaskCompletionSource<Dictionary<string, string>>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Run()
            {
                try { completion.TrySetResult(capture()); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[VisualContext] {ex.Message}"); completion.TrySetResult(new Dictionary<string, string>()); }
            }
            if (!RhinoApp.InvokeRequired) Run();
            else RhinoApp.InvokeOnUiThread((Action)Run);
            return completion.Task.Wait(TimeSpan.FromSeconds(5)) ? completion.Task.Result : new Dictionary<string, string>();
        }
    }

    public sealed class SelectionContextProvider : IAIContextProvider
    {
        public string ProviderId => "selection";

        public Dictionary<string, string> GetContext() => VisualContextCapture.Capture(() =>
        {
            var document = Instances.ActiveCanvas?.Document;
            var selected = document?.Objects.Where(o => o.Attributes?.Selected == true).ToList();
            return new Dictionary<string, string>
            {
                ["documentId"] = document?.DocumentID.ToString("D") ?? string.Empty,
                ["count"] = (selected?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["objects"] = JsonConvert.SerializeObject(selected?.Take(25).Select(o => new
                {
                    guid = o.InstanceGuid, name = o.Name, nickname = o.NickName,
                    x = o.Attributes.Pivot.X, y = o.Attributes.Pivot.Y,
                })),
                ["truncated"] = ((selected?.Count ?? 0) > 25).ToString(),
            };
        });
    }

    public sealed class ViewportContextProvider : IAIContextProvider
    {
        public string ProviderId => "viewport";

        public Dictionary<string, string> GetContext() => VisualContextCapture.Capture(() =>
        {
            var document = RhinoDoc.ActiveDoc;
            var view = document?.Views.ActiveView?.ActiveViewport;
            if (view == null) return new Dictionary<string, string>();
            return new Dictionary<string, string>
            {
                ["name"] = view.Name,
                ["projection"] = view.IsPerspectiveProjection ? "perspective" : "parallel",
                ["camera"] = JsonConvert.SerializeObject(new { position = new[] { view.CameraLocation.X, view.CameraLocation.Y, view.CameraLocation.Z }, direction = new[] { view.CameraDirection.X, view.CameraDirection.Y, view.CameraDirection.Z } }),
                ["units"] = document.ModelUnitSystem.ToString(),
            };
        });
    }
}
