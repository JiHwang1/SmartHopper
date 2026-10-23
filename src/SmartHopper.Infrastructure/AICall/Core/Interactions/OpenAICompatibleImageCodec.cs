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
using Newtonsoft.Json.Linq;

namespace SmartHopper.Infrastructure.AICall.Core.Interactions
{
    /// <summary>
    /// Shared helpers to encode <see cref="ToolResultImage"/> payloads into OpenAI-compatible
    /// chat message parts. Chat Completions-style APIs cannot attach images to
    /// <c>role:"tool"</c> messages, so images are emitted as a trailing <c>role:"user"</c>
    /// message containing <c>image_url</c> content parts with data URIs.
    /// </summary>
    public static class OpenAICompatibleImageCodec
    {
        /// <summary>Keeps every tool result adjacent before sending trailing image-only user messages.</summary>
        public static JArray OrderToolResultImages(JArray messages)
        {
            var ordered = new JArray();
            var pending = new List<JToken>();
            foreach (var message in messages)
            {
                var role = message.Value<string>("role");
                var parts = message["content"] as JArray;
                if (role == "user" && parts?.Count > 0 && parts.All(p => p.Value<string>("type") == "image_url") &&
                    (pending.Count > 0 || ordered.Last?.Value<string>("role") == "tool"))
                {
                    pending.Add(message);
                    continue;
                }
                if (role != "tool")
                {
                    foreach (var imageMessage in pending) ordered.Add(imageMessage);
                    pending.Clear();
                }
                ordered.Add(message);
            }
            foreach (var imageMessage in pending) ordered.Add(imageMessage);
            return ordered;
        }
        /// <summary>
        /// Builds a single OpenAI-compatible <c>image_url</c> content part.
        /// </summary>
        /// <param name="image">The tool result image to encode.</param>
        /// <returns>A content part JObject with a data URI, or null when the image has no data.</returns>
        public static JObject ToImageUrlPart(ToolResultImage image)
        {
            if (image == null || string.IsNullOrEmpty(image.ImageData))
            {
                return null;
            }

            var mimeType = string.IsNullOrEmpty(image.MimeType) ? "image/png" : image.MimeType;
            return new JObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JObject
                {
                    ["url"] = $"data:{mimeType};base64,{image.ImageData}",
                },
            };
        }

        /// <summary>
        /// Builds a <c>role:"user"</c> message containing one <c>image_url</c> part per image.
        /// </summary>
        /// <param name="images">Images to encode (typically <see cref="AIInteractionToolCallExtensions.GetModelImages"/> output).</param>
        /// <returns>A user message JObject, or null when no image has data.</returns>
        public static JObject ToUserImageMessage(IEnumerable<ToolResultImage> images)
        {
            if (images == null)
            {
                return null;
            }

            var parts = new JArray();
            foreach (var image in images)
            {
                var part = ToImageUrlPart(image);
                if (part != null)
                {
                    parts.Add(part);
                }
            }

            if (parts.Count == 0)
            {
                return null;
            }

            return new JObject
            {
                ["role"] = "user",
                ["content"] = parts,
            };
        }
    }
}
