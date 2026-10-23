/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 *
 * This library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the GNU
 * Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with this library; if not, see <https://www.gnu.org/licenses/lgpl-3.0.html>.
 */

using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AIModels;

namespace SmartHopper.Infrastructure.AICall.Fallback
{
    /// <summary>
    /// An ordered list of fallback steps that convert unsupported modalities
    /// into supported ones. Each step carries its own resolved provider and
    /// model, so one chain may span providers.
    /// </summary>
    public sealed class FallbackChain
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FallbackChain"/> class.
        /// </summary>
        public FallbackChain(
            IReadOnlyList<FallbackStep> steps,
            string description,
            AICapability effectiveCapability)
        {
            this.BoundSteps = steps.ToArray();
            this.Steps = this.BoundSteps.Select(step => step.Fallback).ToArray();
            this.Description = description;
            this.EffectiveCapability = effectiveCapability;
        }

        /// <summary>
        /// Compatibility constructor for callers that resolve all steps to one
        /// provider/model.
        /// </summary>
        public FallbackChain(
            IReadOnlyList<IModalityFallback> steps,
            string description,
            string actualProvider,
            string actualModel,
            AICapability effectiveCapability)
            : this(ToBoundSteps(steps, actualProvider, actualModel), description, effectiveCapability)
        {
        }

        /// <summary>Gets the ordered fallback steps.</summary>
        public IReadOnlyList<IModalityFallback> Steps { get; }

        /// <summary>Gets the ordered steps with their resolved provider and model.</summary>
        public IReadOnlyList<FallbackStep> BoundSteps { get; }

        /// <summary>Joined step descriptions for the warning message.</summary>
        public string Description { get; }

        /// <summary>
        /// Gets the compatibility provider value for a single-provider chain.
        /// Mixed-provider chains expose the provider of their first step.
        /// </summary>
        public string ActualProvider => this.BoundSteps.Count > 0 ? this.BoundSteps[0].Provider : null;

        /// <summary>
        /// Gets the compatibility model value for a single-provider chain.
        /// Mixed-provider chains expose the model of their first step.
        /// </summary>
        public string ActualModel => this.BoundSteps.Count > 0 ? this.BoundSteps[0].Model : null;

        /// <summary>True when a different provider from the component's configured one is used.</summary>
        public bool UsesAltProvider { get; init; }

        /// <summary>Capability after all steps applied.</summary>
        public AICapability EffectiveCapability { get; }

        /// <summary>
        /// Applies all steps in order on their resolved provider/model.
        /// </summary>
        public async Task<ModalityFallbackResult> ApplyAsync(AIBody body, CancellationToken ct)
        {
            var combinedResult = new ModalityFallbackResult { TransformedBody = body };

            foreach (var step in this.BoundSteps)
            {
                var stepResult = await step.Fallback.ApplyAsync(
                    combinedResult.TransformedBody,
                    step.Provider,
                    step.Model,
                    ct).ConfigureAwait(false);

                combinedResult.TransformedBody = stepResult.TransformedBody;
                combinedResult.ExtraMetricsList.AddRange(stepResult.ExtraMetricsList);
                combinedResult.Messages.AddRange(stepResult.Messages);
            }

            return combinedResult;
        }

        private static IReadOnlyList<FallbackStep> ToBoundSteps(
            IReadOnlyList<IModalityFallback> steps,
            string provider,
            string model)
        {
            var bound = new List<FallbackStep>();
            if (steps != null)
            {
                foreach (var step in steps)
                {
                    bound.Add(new FallbackStep(step, provider, model));
                }
            }

            return bound;
        }
    }
}
