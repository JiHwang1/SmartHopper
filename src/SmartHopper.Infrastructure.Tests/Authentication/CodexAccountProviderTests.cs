/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 * Licensed under the GNU Lesser General Public License.
 */

using Newtonsoft.Json;
using SmartHopper.Infrastructure.AIModels;
using SmartHopper.Infrastructure.AIProviders;
using SmartHopper.Infrastructure.Authentication.Codex;
using SmartHopper.Infrastructure.Settings;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.Authentication
{
    public class CodexAccountProviderTests
    {
        [Fact]
        public void ProviderManager_RegistersBuiltInCodexAccountProvider()
        {
            var provider = ProviderManager.Instance.GetProvider(CodexAccountProvider.ProviderName);

            Assert.Same(CodexAccountProvider.Instance, provider);
        }

        [Fact]
        public void SelectModel_AllowsTextAndRejectsUnsupportedImageOutput()
        {
            var provider = CodexAccountProvider.Instance;

            Assert.Equal("gpt-test", provider.SelectModel(AICapability.Text2Text, "gpt-test"));
            Assert.Equal(string.Empty, provider.SelectModel(AICapability.Text2Image, "gpt-test"));
        }

        [Fact]
        public void SharedCodexProfile_IsSerializedWithoutCredentials()
        {
            var settings = new SmartHopperAssistantSettings
            {
                AssistantProvider = CodexAccountProvider.ProviderName,
                CodexModel = "gpt-test",
                CodexReasoningEffort = "high",
            };

            var json = JsonConvert.SerializeObject(settings);

            Assert.Contains("gpt-test", json);
            Assert.Contains("high", json);
            Assert.DoesNotContain("token", json.ToLowerInvariant());
            Assert.DoesNotContain("apikey", json.ToLowerInvariant());
        }
    }
}
