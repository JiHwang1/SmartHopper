using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Eto.Drawing;
using Eto.Forms;
using Newtonsoft.Json;
using Rhino;
using SmartHopper.Infrastructure.AITools;
using SmartHopper.Infrastructure.Consent;
using SmartHopper.Infrastructure.Planning;

namespace SmartHopper.Core.UI.Chat
{
    internal partial class WebChatDialog
    {
        private int maxRunSeconds = 300;
        private long maxRunTokens = 100000;
        private readonly string toolOwnerId = Guid.NewGuid().ToString("N");

        private MutationInvocationContext CreateInvocationContext()
        {
            return new MutationInvocationContext
            {
                Source = MutationInvocationSource.WebChat,
                Surface = AIToolSurface.Chat,
                OwnerId = this.toolOwnerId,
                Presenter = new WebChatPlanPresenter(this),
                TaskPlanPresenter = new WebChatTaskPlanPresenter(this),
            };
        }

        private async Task<MutationInvocationContext> CreateBoundInvocationContextAsync(CancellationToken token)
        {
            var context = this.CreateInvocationContext();
            var boundDocument = await CaptureActiveDocumentAsync(token).ConfigureAwait(false);
            context.ValidateMutationAsync = async cancellationToken =>
            {
                var active = await CaptureActiveDocumentAsync(cancellationToken).ConfigureAwait(false);
                return active == boundDocument ? null : "The active Grasshopper document changed during this run. Return to the original document or send a new message.";
            };
            context.RebindDocument = documentId => boundDocument = documentId;
            return context;
        }

        private static async Task<Guid?> CaptureActiveDocumentAsync(CancellationToken token)
        {
            var completion = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = token.Register(() => completion.TrySetCanceled(token));
            RhinoApp.InvokeOnUiThread(() =>
            {
                if (!token.IsCancellationRequested) completion.TrySetResult(global::Grasshopper.Instances.ActiveCanvas?.Document?.DocumentID);
            });
            return await completion.Task.ConfigureAwait(false);
        }

        private sealed class WebChatTaskPlanPresenter : ITaskPlanPresenter
        {
            private readonly WebChatDialog dialog;
            public WebChatTaskPlanPresenter(WebChatDialog dialog) { this.dialog = dialog; }

            public Task ShowAsync(TaskPlan plan, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var payload = new { id = plan.Id, goal = plan.Goal, tasks = plan.Tasks.Select(t => new
                {
                    id = t.Id, description = t.Description, status = t.Status.ToString(),
                }) };
                this.dialog.RunWhenWebViewReady(() =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                        this.dialog.ExecuteScript($"updateTaskPlan({JsonConvert.SerializeObject(payload)});");
                });
                return Task.CompletedTask;
            }
        }

        private sealed class WebChatPlanPresenter : IConsentPresenter
        {
            private readonly WebChatDialog owner;
            public WebChatPlanPresenter(WebChatDialog owner) { this.owner = owner; }
            public bool CanPresent(IConsentProposal proposal, MutationInvocationContext context) => proposal is PlanConsentProposal;

            public async Task<ConsentDecision> PresentAsync(IConsentProposal proposal, MutationInvocationContext context, CancellationToken cancellationToken)
            {
                var plan = (PlanConsentProposal)proposal;
                var completion = new TaskCompletionSource<ConsentDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
                Form? form = null;
                using var registration = cancellationToken.Register(() =>
                {
                    completion.TrySetResult(ConsentDecision.Cancelled());
                    RhinoApp.InvokeOnUiThread(() => form?.Close());
                });
                RhinoApp.InvokeOnUiThread(() =>
                {
                    if (cancellationToken.IsCancellationRequested || this.owner._isClosing)
                    {
                        completion.TrySetResult(ConsentDecision.Cancelled());
                        return;
                    }
                    var text = plan.Goal + "\n\n" + plan.Summary + "\n\n" +
                        string.Join("\n", plan.Steps.Select((s, i) => $"{i + 1}. {s.Description}" + (s.MutatesCanvas ? " [Canvas change]" : string.Empty))) +
                        "\n\nAssumptions:\n" + string.Join("\n", plan.Assumptions) +
                        "\n\nSuccess criteria:\n" + string.Join("\n", plan.SuccessCriteria) +
                        "\n\nApproving this plan does not approve Canvas changes. Each change is reviewed separately.";
                    var accept = new Button { Text = "Approve plan" };
                    var reject = new Button { Text = "Reject" };
                    var layout = new DynamicLayout { Padding = new Padding(16), DefaultSpacing = new Size(8, 8) };
                    layout.Add(new TextArea { Text = text, ReadOnly = true, Wrap = true }, yscale: true);
                    layout.AddRow(null, reject, accept);
                    form = new Form { Title = plan.Title, ClientSize = new Size(520, 480), Content = layout, Owner = this.owner };
                    accept.Click += (_, _) =>
                    {
                        completion.TrySetResult(new ConsentDecision { Status = ConsentDecisionStatus.Approved, AcceptedItemKeys = plan.ItemKeys.ToArray() });
                        form.Close();
                    };
                    reject.Click += (_, _) => form.Close();
                    form.Closed += (_, _) => completion.TrySetResult(ConsentDecision.Cancelled("Plan rejected or closed."));
                    form.Show();
                });
                return await completion.Task.ConfigureAwait(false);
            }
        }
    }
}
