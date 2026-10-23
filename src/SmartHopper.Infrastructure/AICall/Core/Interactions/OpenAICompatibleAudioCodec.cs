/*
 * SmartHopper - AI-powered Grasshopper Plugin
 * Copyright (C) 2024-2026 Marc Roca Musach
 *
 * This library is free software; you can redistribute it and/or
 * modify it under the terms of the GNU Lesser General Public
 * License as published by the Free Software Foundation; either
 * version 3 of the License, or (at your option) any later version.
 */

using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AICall.Core.Base;

namespace SmartHopper.Infrastructure.AICall.Core.Interactions
{
    /// <summary>
    /// Encodes and decodes audio payloads for providers using OpenAI-compatible
    /// chat audio shapes: <c>input_audio</c> on input and <c>message.audio</c> on output.
    /// </summary>
    public static class OpenAICompatibleAudioCodec
    {
        /// <summary>The default voice used for chat audio output.</summary>
        public const string DefaultVoice = "alloy";

        /// <summary>The default audio format used for chat audio input and output.</summary>
        public const string DefaultFormat = "wav";

        /// <summary>Resolves base64 audio and its compatible format token.</summary>
        public static bool TryResolveAudioData(AIInteractionAudio audio, out string base64Data, out string format)
        {
            base64Data = null;
            if (!TryResolveAudioBytes(audio, out var bytes, out format) || bytes == null)
            {
                return false;
            }

            base64Data = Convert.ToBase64String(bytes);
            return true;
        }

        /// <summary>Resolves audio bytes from memory or the referenced file.</summary>
        public static bool TryResolveAudioBytes(AIInteractionAudio audio, out byte[] bytes, out string format)
        {
            bytes = null;
            format = MapAudioFormat(audio?.MimeType, audio?.FilePath);
            if (audio == null)
            {
                return false;
            }

            bytes = audio.Data;
            if ((bytes == null || bytes.Length == 0) && !string.IsNullOrWhiteSpace(audio.FilePath))
            {
                try
                {
                    bytes = File.ReadAllBytes(audio.FilePath);
                }
                catch (Exception)
                {
                    bytes = null;
                }
            }

            return bytes != null && bytes.Length > 0;
        }

        /// <summary>Detects a common audio MIME type from its container signature.</summary>
        public static string DetectAudioMimeType(byte[] data, string fallback = "audio/mpeg")
        {
            if (data == null || data.Length < 4)
            {
                return fallback;
            }

            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F')
            {
                return "audio/wav";
            }

            if (data[0] == 'f' && data[1] == 'L' && data[2] == 'a' && data[3] == 'C')
            {
                return "audio/flac";
            }

            if (data[0] == 'O' && data[1] == 'g' && data[2] == 'g' && data[3] == 'S')
            {
                return "audio/ogg";
            }

            if ((data[0] == 'I' && data[1] == 'D' && data[2] == '3')
                || (data[0] == 0xFF && (data[1] & 0xE0) == 0xE0))
            {
                return "audio/mpeg";
            }

            return fallback;
        }

        /// <summary>Decodes a <c>{ audio_data, mime_type }</c> speech response envelope.</summary>
        public static AIInteractionAudio TryDecodeAudioDataEnvelope(JObject response)
        {
            var data = response?["audio_data"]?.ToString();
            if (string.IsNullOrWhiteSpace(data))
            {
                return null;
            }

            byte[] bytes = null;
            try
            {
                bytes = Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                // Preserve the interaction envelope even when a malformed payload cannot decode.
            }

            var mimeType = response["mime_type"]?.ToString();
            if (string.IsNullOrWhiteSpace(mimeType))
            {
                mimeType = DetectAudioMimeType(bytes);
            }

            return new AIInteractionAudio
            {
                Agent = AIAgent.Assistant,
                Data = bytes,
                MimeType = mimeType,
            };
        }

        /// <summary>Maps audio MIME type or extension to an OpenAI audio format token.</summary>
        public static string MapAudioFormat(string mimeType, string filePath = null)
        {
            var mime = (mimeType ?? string.Empty).Trim().ToLowerInvariant();
            var semicolon = mime.IndexOf(';');
            if (semicolon >= 0)
            {
                mime = mime.Substring(0, semicolon).Trim();
            }

            switch (mime)
            {
                case "audio/wav":
                case "audio/x-wav":
                case "audio/wave":
                case "audio/vnd.wave":
                    return "wav";
                case "audio/mpeg":
                case "audio/mp3":
                case "audio/mpeg3":
                case "audio/x-mpeg":
                    return "mp3";
                case "audio/ogg":
                case "audio/opus":
                    return "opus";
                case "audio/flac":
                case "audio/x-flac":
                    return "flac";
                case "audio/aac":
                case "audio/aacp":
                case "audio/mp4":
                case "audio/m4a":
                case "audio/x-m4a":
                    return "aac";
                case "audio/pcm":
                case "audio/l16":
                    return "pcm16";
            }

            if (!string.IsNullOrWhiteSpace(filePath))
            {
                switch (Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant())
                {
                    case "mp3":
                        return "mp3";
                    case "ogg":
                        return "opus";
                    case "flac":
                        return "flac";
                    case "aac":
                    case "m4a":
                        return "aac";
                    case "pcm":
                        return "pcm16";
                }
            }

            return DefaultFormat;
        }

        /// <summary>Builds an OpenAI-compatible <c>input_audio</c> content part.</summary>
        public static JObject ToInputAudioContentPart(AIInteractionAudio audio)
        {
            if (!TryResolveAudioData(audio, out var base64Data, out var format) || base64Data == null)
            {
                return null;
            }

            return new JObject
            {
                ["type"] = "input_audio",
                ["input_audio"] = new JObject
                {
                    ["data"] = base64Data,
                    ["format"] = format,
                },
            };
        }

        /// <summary>Adds chat completion parameters that request audio output.</summary>
        public static void ApplyAudioOutputParameters(JObject body, IReadOnlyDictionary<string, JToken> extras)
        {
            var voice = DefaultVoice;
            var format = DefaultFormat;
            if (extras != null)
            {
                if (extras.TryGetValue("voice", out var voiceToken) && !string.IsNullOrWhiteSpace(voiceToken?.ToString()))
                {
                    voice = voiceToken.ToString();
                }

                if (extras.TryGetValue("format", out var formatToken) && !string.IsNullOrWhiteSpace(formatToken?.ToString()))
                {
                    format = formatToken.ToString();
                }
                else if (extras.TryGetValue("response_format", out var responseFormatToken) && !string.IsNullOrWhiteSpace(responseFormatToken?.ToString()))
                {
                    format = responseFormatToken.ToString();
                }
            }

            body["modalities"] = new JArray { "text", "audio" };
            body["audio"] = new JObject
            {
                ["voice"] = voice,
                ["format"] = format,
            };
        }

        /// <summary>Extracts assistant audio and transcript from <c>message.audio</c>.</summary>
        public static AIInteractionAudio ExtractAudioOutput(JObject message, out string transcript)
        {
            transcript = null;
            var audioObj = message?["audio"] as JObject;
            if (audioObj == null)
            {
                return null;
            }

            transcript = audioObj["transcript"]?.ToString();
            var data = audioObj["data"]?.ToString();
            if (string.IsNullOrWhiteSpace(data))
            {
                return null;
            }

            byte[] bytes = null;
            try
            {
                bytes = Convert.FromBase64String(data);
            }
            catch (FormatException)
            {
                // Preserve the interaction envelope even when a malformed payload cannot decode.
            }

            return new AIInteractionAudio
            {
                Agent = AIAgent.Assistant,
                Data = bytes,
                MimeType = DetectAudioMimeType(bytes, $"audio/{DefaultFormat}"),
            };
        }
    }
}
