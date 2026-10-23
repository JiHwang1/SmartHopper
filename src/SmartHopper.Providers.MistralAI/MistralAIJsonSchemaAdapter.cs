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
using SmartHopper.Infrastructure.AICall.JsonSchemas;

namespace SmartHopper.Providers.MistralAI
{
    internal sealed class MistralAIJsonSchemaAdapter : IJsonSchemaAdapter
    {
        public string ProviderName => "MistralAI";

        public (JObject wrapped, SchemaWrapperInfo info) Wrap(JObject schema)
        {
            var result = JsonSchemaAdapterRegistry.Default.Wrap(schema);
            result.info.ProviderName = this.ProviderName;
            return result;
        }

        public string Unwrap(string content, SchemaWrapperInfo info)
        {
            // Default service logic is sufficient for Mistral
            return content;
        }
    }
}
