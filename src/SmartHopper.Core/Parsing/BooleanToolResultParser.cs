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

using Newtonsoft.Json.Linq;
using SmartHopper.Core.ComponentBase.Batch;

namespace SmartHopper.Core.Parsing
{
    /// <summary>
    /// Reads the value and fallback flag emitted by the boolean AI tools, including batch sentinels.
    /// </summary>
    public static class BooleanToolResultParser
    {
        /// <summary>
        /// Parses one boolean tool result. Missing or invalid fallback flags default to true,
        /// matching the component contract for incomplete tool responses.
        /// </summary>
        /// <param name="toolResult">The tool result object, or null when execution returned no result.</param>
        /// <returns>The result string, fallback flag, and whether the result is a batch sentinel.</returns>
        public static (string Value, bool UsedFallback, bool IsBatchSentinel) Parse(JObject toolResult)
        {
            var value = toolResult?["result"]?.ToString() ?? string.Empty;
            return (value, ReadBooleanOrDefault(toolResult?["usedFallback"], true), BatchSentinel.Is(value));
        }

        private static bool ReadBooleanOrDefault(JToken token, bool defaultValue)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return defaultValue;
            }

            try
            {
                return token.ToObject<bool>();
            }
            catch
            {
                return defaultValue;
            }
        }
    }
}
