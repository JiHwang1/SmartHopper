using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Metrics;
using SmartHopper.Infrastructure.AICall.Sessions;
using SmartHopper.Infrastructure.Consent;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.AICall
{
    public class ConsentAndBudgetTests
    {
        [Fact]
        public async Task Consent_CannotApproveItemsOutsideThePreparedProposal()
        {
            var decision = await ConsentGate.RequestAsync(new Proposal(), new MutationInvocationContext { Presenter = new Presenter() });
            Assert.Equal(new[] { "one" }, decision.AcceptedItemKeys);
        }

        [Fact]
        public async Task CancelledConsent_DoesNotApplyEvenIfPresenterApprovesLate()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            var applied = false;
            await MutatingOperationExecutor.ExecuteAsync(new Proposal(), new MutationInvocationContext { Presenter = new Presenter() },
                (_, _) => { applied = true; return Task.FromResult(true); }, _ => false, cts.Token);
            Assert.False(applied);
        }

        [Fact]
        public void Budget_CountsNewUsageAndDoesNotRechargeConversationHistory()
        {
            var old = new AIInteractionText { Metrics = new AIMetrics { InputTokensPrompt = 1000 } };
            var current = new AIInteractionText { Metrics = new AIMetrics { InputTokensPrompt = 20, EstimatedOutputTokens = 10 } };
            var result = new AIReturn();
            result.SetBody(AIBodyBuilder.Create().Add(old, markAsNew: false).Add(current).Build());
            var budget = new RunBudget(0, 10);
            budget.ChargeResult(result);
            Assert.Equal(20, budget.Tokens);
            Assert.Throws<InvalidOperationException>(budget.ThrowIfExhausted);
            var fresh = new RunBudget(0, 10);
            fresh.ThrowIfExhausted();
        }

        private sealed class Proposal : IConsentProposal
        {
            public string Id => "proposal";
            public string Kind => "test";
            public string Title => "Test";
            public IReadOnlyList<string> ItemKeys => new[] { "one" };
        }

        private sealed class Presenter : IConsentPresenter
        {
            public bool CanPresent(IConsentProposal proposal, MutationInvocationContext context) => true;
            public Task<ConsentDecision> PresentAsync(IConsentProposal proposal, MutationInvocationContext context, CancellationToken token)
                => Task.FromResult(new ConsentDecision { Status = ConsentDecisionStatus.Approved, AcceptedItemKeys = new[] { "one", "unreviewed", "one" } });
        }
    }
}
