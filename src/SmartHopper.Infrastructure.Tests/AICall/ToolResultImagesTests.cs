using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Utilities;
using SmartHopper.Infrastructure.AITools.External;
using SmartHopper.Infrastructure.Authentication.Codex;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.AICall
{
    public class ToolResultImagesTests
    {
        private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Wl6lksAAAAASUVORK5CYII=";

        [Fact]
        public void PayloadExtraction_PreservesOriginalAndKeepsBase64OutOfText()
        {
            var payload = new JObject { ["imageBase64"] = Png, ["mimeType"] = "image/png", ["imageAudience"] = "model", ["width"] = 1, ["height"] = 1 };
            var result = new AIInteractionToolResult { Result = payload, Id = "call", Name = "canvas_screenshot" };
            Assert.Equal(Png, payload.Value<string>("imageBase64"));
            Assert.Null(result.Result["imageBase64"]);
            Assert.True(result.Result.Value<bool>("imageAttached"));
            Assert.Equal(Png, Assert.Single(result.GetModelImages()).ImageData);
            Assert.DoesNotContain(Png, result.ToString());
            Assert.Contains("data:image/png;base64," + Png, result.GetRawContentForRender());
        }

        [Fact]
        public void DisplayOnlyImage_IsRenderedButNotSentToModel()
        {
            var result = new AIInteractionToolResult { Result = new JObject { ["imageBase64"] = Png, ["mimeType"] = "image/png", ["imageAudience"] = "display" } };
            Assert.Empty(result.GetModelImages());
            Assert.Contains(Png, result.GetRawContentForRender());
            var response = CodexDynamicToolResult.Ok(result.Result, result.Images).ToResponse();
            Assert.Single((JArray)response["contentItems"]);
        }

        [Theory]
        [InlineData("image/svg+xml", "PHN2Zz48L3N2Zz4=")]
        [InlineData("image/png", "invalid-base64!")]
        public void InvalidRasterPayload_IsRejected(string mime, string data)
        {
            Assert.Throws<ArgumentException>(() => new AIInteractionToolResult { Result = new JObject { ["imageBase64"] = data, ["mimeType"] = mime } });
        }

        [Fact]
        public void CodexResponse_ContainsNativeImagePartAndCompactMetadata()
        {
            var image = new ToolResultImage { ImageData = Png, MimeType = "image/png", SendToModel = true };
            var response = CodexDynamicToolResult.Ok(new JObject { ["width"] = 1 }, new[] { image }).ToResponse();
            var content = (JArray)response["contentItems"];
            Assert.Equal("inputText", content[0].Value<string>("type"));
            Assert.DoesNotContain(Png, content[0].Value<string>("text"));
            Assert.Equal("inputImage", content[1].Value<string>("type"));
            Assert.Equal("data:image/png;base64," + Png, content[1].Value<string>("imageUrl"));
        }

        [Fact]
        public void ParallelToolResponses_PrecedeAllTrailingImageMessages()
        {
            var image = OpenAICompatibleImageCodec.ToUserImageMessage(new[] { new ToolResultImage { ImageData = Png, MimeType = "image/png", SendToModel = true } });
            var ordered = OpenAICompatibleImageCodec.OrderToolResultImages(new JArray
            {
                new JObject { ["role"] = "tool", ["tool_call_id"] = "first" }, image,
                new JObject { ["role"] = "tool", ["tool_call_id"] = "second" }, image.DeepClone(),
            });
            Assert.Equal("first", ordered[0].Value<string>("tool_call_id"));
            Assert.Equal("second", ordered[1].Value<string>("tool_call_id"));
            Assert.Equal("user", ordered[2].Value<string>("role"));
            Assert.Equal("user", ordered[3].Value<string>("role"));
        }

#if NET7_WINDOWS
        [Fact]
        public void ProviderCodecs_UseNativeImagePartsAndRetainToolCorrelation()
        {
            var result = new AIInteractionToolResult { Id = "call-1", Name = "canvas_screenshot", Result = new JObject { ["width"] = 1 },
                Images = new List<ToolResultImage> { new ToolResultImage { ImageData = Png, MimeType = "image/png", SendToModel = true } } };
            var openAiProvider = (SmartHopper.Providers.OpenAI.OpenAIProvider)Activator.CreateInstance(typeof(SmartHopper.Providers.OpenAI.OpenAIProvider), nonPublic: true)!;
            var openAi = JToken.Parse(openAiProvider.Encode(result));
            Assert.Equal("function_call_output", openAi.Value<string>("type"));
            Assert.Equal("call-1", openAi.Value<string>("call_id"));
            Assert.Contains(Png, openAi["output"].ToString());
            var chatRequest = new SmartHopper.Infrastructure.AICall.Core.Requests.AIRequestCall
            {
                Endpoint = "/chat/completions", Provider = "OpenAI", Model = "gpt-test",
                Body = AIBodyBuilder.Create().WithToolFilter("-*").Add(result).Build(),
            };
            var openAiChat = JToken.Parse(openAiProvider.Encode(chatRequest))["messages"];
            var mistral = JToken.Parse(((SmartHopper.Providers.MistralAI.MistralAIProvider)Activator.CreateInstance(typeof(SmartHopper.Providers.MistralAI.MistralAIProvider), nonPublic: true)!).Encode(result));
            foreach (var encoded in new[] { openAiChat, mistral })
            {
                Assert.Equal("tool", encoded[0].Value<string>("role"));
                Assert.Equal("call-1", encoded[0].Value<string>("tool_call_id"));
                Assert.DoesNotContain(Png, encoded[0].ToString());
                Assert.Contains(Png, encoded[1].ToString());
            }
            var anthropic = JToken.Parse(((SmartHopper.Providers.Anthropic.AnthropicProvider)Activator.CreateInstance(typeof(SmartHopper.Providers.Anthropic.AnthropicProvider), nonPublic: true)!).Encode(result));
            Assert.Equal("call-1", anthropic["content"][0].Value<string>("tool_use_id"));
            Assert.Contains(Png, anthropic["content"].ToString());
            var gemini = JToken.Parse(((SmartHopper.Providers.Gemini.GeminiProvider)Activator.CreateInstance(typeof(SmartHopper.Providers.Gemini.GeminiProvider), nonPublic: true)!).Encode(result));
            Assert.Contains(Png, gemini.ToString());
        }
#endif
    }
}
