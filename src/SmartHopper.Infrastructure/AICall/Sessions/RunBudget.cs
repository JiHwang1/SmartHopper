using System;
using System.Diagnostics;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Metrics;
using SmartHopper.Infrastructure.AICall.Utilities;

namespace SmartHopper.Infrastructure.AICall.Sessions
{
    /// <summary>Accounts one autonomous run independently of persisted conversation history.</summary>
    public sealed class RunBudget
    {
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly int maxSeconds;
        private readonly long maxTokens;

        public RunBudget(int maxSeconds, long maxTokens)
        {
            if (maxSeconds < 0 || maxTokens < 0) throw new ArgumentOutOfRangeException(nameof(maxSeconds));
            this.maxSeconds = maxSeconds;
            this.maxTokens = maxTokens;
        }

        public long Tokens { get; private set; }
        public bool UsesEstimates { get; private set; }
        public double ElapsedSeconds => this.clock.Elapsed.TotalSeconds;

        public void Charge(AIMetrics? metrics)
        {
            if (metrics == null || !metrics.HasProviderCallEvidence) return;
            this.Tokens += Math.Max(0, metrics.EffectiveTotalTokens);
            this.UsesEstimates |= metrics.TotalEstimatedTokens > metrics.TotalTokens;
        }

        /// <summary>Counts only response-owned interactions, never accumulated history.</summary>
        public void ChargeResult(SmartHopper.Infrastructure.AICall.Core.Returns.AIReturn? result)
        {
            var usage = new AIMetrics();
            foreach (var interaction in result?.Body?.GetNewInteractions() ?? new System.Collections.Generic.List<IAIInteraction>())
                if (interaction.Metrics?.HasProviderCallEvidence == true) usage.Combine(interaction.Metrics);
            this.Charge(usage);
        }

        public void ThrowIfExhausted()
        {
            if ((this.maxSeconds > 0 && this.ElapsedSeconds >= this.maxSeconds) ||
                (this.maxTokens > 0 && this.Tokens >= this.maxTokens))
                throw new InvalidOperationException("Autonomous run budget exhausted. Send a new message to continue.");
        }
    }
}
