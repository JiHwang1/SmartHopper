/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 */

namespace SmartHopper.Infrastructure.Tests.AICall
{
    using System;
    using System.Collections.Generic;
    using System.Drawing;
    using System.Net;
    using System.Net.Http;
    using System.Text;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;
    using SmartHopper.Infrastructure.AICall.Core.Base;
    using SmartHopper.Infrastructure.AICall.Core.Interactions;
    using SmartHopper.Infrastructure.AICall.Core.Requests;
    using SmartHopper.Infrastructure.AICall.Core.Returns;
    using SmartHopper.Infrastructure.AICall.Metrics;
    using SmartHopper.Infrastructure.AIModels;
    using SmartHopper.Infrastructure.AIProviders;
    using SmartHopper.Infrastructure.Diagnostics;
    using Xunit;

    /// <summary>
    /// Deterministic tests for the shared audio transport. A fake HTTP handler exercises
    /// the real request-content and response-normalization paths without provider accounts.
    /// </summary>
    public class AudioTransportTests
    {
#if NET7_WINDOWS
        [Fact(DisplayName = "Audio transcription posts multipart file and metadata")]
        public async Task Transcription_PostsMultipartFileAndMetadata()
        {
            var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"text\":\"hello\"}", Encoding.UTF8, "application/json"),
            });
            var provider = new AudioTestProvider(handler);
            var request = AudioTestRequest.Create(
                provider,
                "/audio/transcriptions",
                CreateAudioBody(new AIInteractionAudio
                {
                    Agent = AIAgent.User,
                    Data = Encoding.ASCII.GetBytes("RIFFtest-audio"),
                    FilePath = "sample.wav",
                    MimeType = "audio/wav",
                    LanguageHint = "en",
                }));

            var result = await provider.Call(request);

            var capture = Assert.Single(handler.Requests);
            Assert.Equal(HttpMethod.Post, capture.Method);
            var audioReturn = Assert.IsType<AIReturn>(result);
            Assert.Equal("hello", audioReturn.Raw["text"]?.ToString());
            Assert.StartsWith("multipart/form-data", capture.ContentType, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("name=file", capture.Body, StringComparison.Ordinal);
            Assert.Contains("filename=sample.wav", capture.Body, StringComparison.Ordinal);
            Assert.Contains("Content-Type: audio/wav", capture.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("name=model", capture.Body, StringComparison.Ordinal);
            Assert.Contains("whisper-test", capture.Body, StringComparison.Ordinal);
            Assert.Contains("name=language", capture.Body, StringComparison.Ordinal);
            Assert.Contains("RIFFtest-audio", capture.Body, StringComparison.Ordinal);
        }

        [Fact(DisplayName = "Binary speech response becomes decodable audio envelope")]
        public async Task Speech_BinaryResponseBecomesAudioEnvelope()
        {
            var audioBytes = new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4 };
            var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(audioBytes),
            });
            handler.ResponseContentType = "audio/wav";
            var provider = new AudioTestProvider(handler);
            var request = AudioTestRequest.Create(provider, "/audio/speech", CreateAudioBody(new AIInteractionText
            {
                Agent = AIAgent.User,
                Content = "speak this",
            }));
            request.Capability = AICapability.SpeechOutput;

            var result = await provider.Call(request);

            var audioReturn = Assert.IsType<AIReturn>(result);
            Assert.Equal(Convert.ToBase64String(audioBytes), audioReturn.Raw["audio_data"]?.ToString());
            Assert.Equal("audio/wav", audioReturn.Raw["mime_type"]?.ToString());
            var audio = OpenAICompatibleAudioCodec.TryDecodeAudioDataEnvelope(audioReturn.Raw);
            Assert.NotNull(audio);
            Assert.Equal(audioBytes, audio!.Data);
            Assert.Equal("audio/wav", audio.MimeType);

            // Metrics are mutable on the local contract; ownership can be shared by a provider response.
            var metrics = new AIMetrics { Provider = "audio-test", Model = "speech-test", FinishReason = "stop" };
            audio.Metrics = metrics;
            Assert.Same(metrics, audio.Metrics);
        }

#endif
        [Theory(DisplayName = "Audio MIME and extension map to provider format")]
        [InlineData("audio/wav", null, "wav")]
        [InlineData("audio/mpeg", null, "mp3")]
        [InlineData("audio/ogg", null, "opus")]
        [InlineData("audio/flac", null, "flac")]
        [InlineData("audio/mp4", null, "aac")]
        [InlineData(null, "voice.m4a", "aac")]
        [InlineData("application/octet-stream", "voice.pcm", "pcm16")]
        [InlineData(null, null, "wav")]
        public void MapAudioFormat_UsesLocalCodecContract(string mimeType, string filePath, string expected)
        {
            Assert.Equal(expected, OpenAICompatibleAudioCodec.MapAudioFormat(mimeType, filePath));
        }

        [Fact(DisplayName = "Audio chat input and output use OpenAI-compatible shapes")]
        public void ChatAudioCodec_EncodesInputAndDecodesOutput()
        {
            var inputBytes = new byte[] { (byte)'I', (byte)'D', (byte)'3', 1, 2, 3 };
            var part = OpenAICompatibleAudioCodec.ToInputAudioContentPart(new AIInteractionAudio
            {
                Data = inputBytes,
                MimeType = "audio/mp3",
            });

            Assert.NotNull(part);
            Assert.Equal("input_audio", part!["type"]?.ToString());
            Assert.Equal(Convert.ToBase64String(inputBytes), part["input_audio"]?["data"]?.ToString());
            Assert.Equal("mp3", part["input_audio"]?["format"]?.ToString());

            var body = new JObject();
            OpenAICompatibleAudioCodec.ApplyAudioOutputParameters(
                body,
                new Dictionary<string, JToken> { ["voice"] = "nova", ["format"] = "opus" });
            Assert.Equal("nova", body["audio"]?["voice"]?.ToString());
            Assert.Equal("opus", body["audio"]?["format"]?.ToString());
            Assert.Contains("audio", body["modalities"]!.Values<string>());

            var output = OpenAICompatibleAudioCodec.ExtractAudioOutput(
                new JObject
                {
                    ["audio"] = new JObject
                    {
                        ["transcript"] = "spoken text",
                        ["data"] = Convert.ToBase64String(inputBytes),
                    },
                },
                out var transcript);
            Assert.NotNull(output);
            Assert.Equal("spoken text", transcript);
            Assert.Equal(inputBytes, output!.Data);
            Assert.Equal("audio/mpeg", output.MimeType);
        }

#if NET7_WINDOWS
        private static AIBody CreateAudioBody(IAIInteraction interaction)
        {
            return AIBodyBuilder.Create()
                .WithTurnId("audio-test-turn")
                .Add(interaction)
                .Build();
        }

        private sealed class AudioTestRequest : AIRequestCall
        {
            private IAIProvider providerInstance;

            public static AudioTestRequest Create(IAIProvider provider, string endpoint, AIBody body)
            {
                var request = new AudioTestRequest { providerInstance = provider };
                request.Provider = provider.Name;
                request.Model = "whisper-test";
                request.Endpoint = endpoint;
                request.Body = body;
                request.Authentication = "none";
                return request;
            }

            public override IAIProvider ProviderInstance => this.providerInstance;

            public override (bool IsValid, List<SHRuntimeMessage> Errors) IsValid() =>
                (true, new List<SHRuntimeMessage>());
        }

        private sealed class AudioTestProvider : AIProvider
        {
            private readonly CapturingHandler handler;

            public AudioTestProvider(CapturingHandler handler)
            {
                this.handler = handler;
            }

            public override string Name => "audio-test";

            public override Image Icon => null!;

            public override bool IsEnabled => true;

            public override Uri DefaultServerUrl => new Uri("https://audio.test/v1/");

            public override string Encode(AIRequestCall request) =>
                "{\"model\":\"whisper-test\",\"response_format\":\"text\"}";

            public override string Encode(IAIInteraction interaction) => "{}";

            public override List<IAIInteraction> Decode(JObject response) => new List<IAIInteraction>();

            internal override HttpClient CreateProviderHttpClient() => new HttpClient(this.handler, false);
        }

        private sealed class CapturingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> responseFactory;

            public CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            {
                this.responseFactory = responseFactory;
            }

            public List<RequestCapture> Requests { get; } = new List<RequestCapture>();

            public string ResponseContentType { get; set; }

            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = request.Content == null
                    ? string.Empty
                    : Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken));
                this.Requests.Add(new RequestCapture(
                    request.Method,
                    request.Content?.Headers.ContentType?.MediaType,
                    body));

                var response = this.responseFactory(request);
                if (this.ResponseContentType != null)
                {
                    response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(this.ResponseContentType);
                }

                return response;
            }

            public sealed class RequestCapture
            {
                public RequestCapture(HttpMethod method, string contentType, string body)
                {
                    this.Method = method;
                    this.ContentType = contentType;
                    this.Body = body;
                }

                public HttpMethod Method { get; }

                public string ContentType { get; }

                public string Body { get; }
            }
        }
#endif
    }
}
