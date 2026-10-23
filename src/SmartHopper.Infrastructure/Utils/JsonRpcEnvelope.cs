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

namespace SmartHopper.Infrastructure.Utils
{
    /// <summary>Transport-independent JSON-RPC response envelopes.</summary>
    internal static class JsonRpcEnvelope
    {
        internal const string Version = "2.0";

        internal static JObject Result(JToken? id, JToken? result)
        {
            return new JObject
            {
                ["jsonrpc"] = Version,
                ["id"] = id?.DeepClone() ?? JValue.CreateNull(),
                ["result"] = result?.DeepClone() ?? JValue.CreateNull(),
            };
        }

        internal static JObject Error(JToken? id, int code, string message)
        {
            return new JObject
            {
                ["jsonrpc"] = Version,
                ["id"] = id?.DeepClone() ?? JValue.CreateNull(),
                ["error"] = new JObject
                {
                    ["code"] = code,
                    ["message"] = message,
                },
            };
        }
    }
}
