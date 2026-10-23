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

using System;
using System.Collections.Generic;
using SmartHopper.Infrastructure.AICall.Core.Interactions;

namespace SmartHopper.Core.UI.Chat
{
    /// <summary>
    /// Resubmits cumulative text at a tool or terminal boundary. A delta's dirty marker records
    /// an attempted render, so it cannot prove delivery when the render queue was saturated.
    /// </summary>
    internal static class ChatStreamFlush
    {
        internal static void Flush(
            IEnumerable<KeyValuePair<string, IAIInteraction>> streams,
            Action<string, AIInteractionText> submit,
            string? turnKey = null)
        {
            var prefix = string.IsNullOrWhiteSpace(turnKey) ? null : turnKey + ":";
            foreach (var stream in streams)
            {
                if (string.IsNullOrWhiteSpace(stream.Key) ||
                    (prefix != null && !stream.Key.StartsWith(prefix, StringComparison.Ordinal))) continue;

                if (stream.Value is AIInteractionText text &&
                    (!string.IsNullOrWhiteSpace(text.Content) || !string.IsNullOrWhiteSpace(text.Reasoning)))
                {
                    submit(stream.Key, text);
                }
            }
        }
    }
}
