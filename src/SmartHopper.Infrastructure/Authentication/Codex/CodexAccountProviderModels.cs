/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public License.
 */

using System.Collections.Generic;
using System.Threading.Tasks;
using SmartHopper.Infrastructure.AIModels;
using SmartHopper.Infrastructure.AIProviders;
using SmartHopper.Infrastructure.Settings;

namespace SmartHopper.Infrastructure.Authentication.Codex
{
    internal sealed class CodexAccountProviderModels : AIProviderModels
    {
        public CodexAccountProviderModels(IAIProvider provider)
            : base(provider)
        {
        }

        public override Task<List<AIModelCapabilities>> RetrieveModels()
        {
            var model = SmartHopperSettings.Instance.SmartHopperAssistant?.CodexModel;
            if (string.IsNullOrWhiteSpace(model))
            {
                model = SmartHopperSettings.Instance.SmartHopperAssistant?.AssistantModel;
            }

            var models = string.IsNullOrWhiteSpace(model)
                ? new List<AIModelCapabilities>()
                : new List<AIModelCapabilities> { CreateCapabilities(model) };
            return Task.FromResult(models);
        }

        public override async Task<List<string>> RetrieveApiModels()
        {
            var models = await this.RetrieveModels().ConfigureAwait(false);
            return models.ConvertAll(item => item.Model);
        }

        internal static AIModelCapabilities CreateCapabilities(string model)
        {
            return new AIModelCapabilities
            {
                Provider = CodexAccountProvider.ProviderName,
                Model = model,
                Capabilities = AICapability.TextInput |
                    AICapability.TextOutput |
                    AICapability.JsonOutput |
                    AICapability.Reasoning,
                Default = AICapability.Text2Text |
                    AICapability.Text2Json |
                    AICapability.ReasoningChat,
                SupportsStreaming = false,
                Verified = true,
                Rank = 100,
            };
        }
    }
}
