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
 * You should have received a copy of the GNU Lesser General Public
 * License along with this program. If not, see <https://www.gnu.org/licenses/lgpl-3.0.html>.
 */

namespace SmartHopper.Infrastructure.Tests.AICall
{
    using System.Collections.Generic;
    using System.Threading;
    using System.Threading.Tasks;
    using SmartHopper.Infrastructure.AICall.Core.Interactions;
    using SmartHopper.Infrastructure.AICall.Fallback;
    using SmartHopper.Infrastructure.AIModels;
    using Xunit;

    /// <summary>Tests for per-step fallback provider/model binding.</summary>
    public class FallbackChainTests
    {
        private sealed class RecordingFallback : IModalityFallback
        {
            public string RecordedProvider { get; private set; }

            public string RecordedModel { get; private set; }

            public string Name => "Recording";

            public AICapability Handles => AICapability.ImageInput;

            public AICapability RequiresCapability => AICapability.Image2Text;

            public AICapability ResultsIn => AICapability.TextInput;

            public string Description => "records invocation";

            public bool IsAvailable(string providerName) => true;

            public Task<ModalityFallbackResult> ApplyAsync(
                AIBody body,
                string providerName,
                string modelName,
                CancellationToken ct)
            {
                this.RecordedProvider = providerName;
                this.RecordedModel = modelName;
                return Task.FromResult(new ModalityFallbackResult { TransformedBody = body });
            }
        }

        [Fact(DisplayName = "ApplyAsync uses each step's resolved provider and model")]
        public async Task ApplyAsync_UsesEachStepsResolvedProviderAndModel()
        {
            var first = new RecordingFallback();
            var second = new RecordingFallback();
            var chain = new FallbackChain(
                new List<FallbackStep>
                {
                    new FallbackStep(first, "provider-a", "model-a"),
                    new FallbackStep(second, "provider-b", "model-b"),
                },
                "mixed conversion",
                AICapability.TextInput);

            var result = await chain.ApplyAsync(AIBody.Empty, CancellationToken.None);

            Assert.Equal(("provider-a", "model-a"), (first.RecordedProvider, first.RecordedModel));
            Assert.Equal(("provider-b", "model-b"), (second.RecordedProvider, second.RecordedModel));
            Assert.NotNull(result.TransformedBody);
        }

        [Fact(DisplayName = "Legacy constructor binds all steps to one provider and model")]
        public async Task LegacyConstructor_BindsAllSteps()
        {
            var fallback = new RecordingFallback();
            var chain = new FallbackChain(
                new List<IModalityFallback> { fallback },
                "legacy",
                "provider-a",
                "model-a",
                AICapability.TextInput);

            await chain.ApplyAsync(AIBody.Empty, CancellationToken.None);

            IReadOnlyList<IModalityFallback> legacySteps = chain.Steps;
            Assert.Same(fallback, Assert.Single(legacySteps));
            Assert.Equal("provider-a", fallback.RecordedProvider);
            Assert.Equal("model-a", fallback.RecordedModel);
        }
    }
}
