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
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AICall.Core.Base;
using SmartHopper.Infrastructure.AICall.JsonSchemas;
using SmartHopper.Infrastructure.Diagnostics;

namespace SmartHopper.Infrastructure.AICall.Policies.Request
{
    /// <summary>
    /// Validates the request's JSON output schema and prepares provider wrapper info
    /// for consistent response unwrapping/validation.
    /// Non-intrusive: does not mutate capabilities; only records diagnostics on failure.
    /// </summary>
    public sealed class SchemaValidateRequestPolicy : IRequestPolicy
    {
        public Task ApplyAsync(PolicyContext context)
        {
            var rq = context?.Request;
            var svc = JsonSchemaService.Instance;
            svc.SetCurrentWrapperInfo(null);
            if (rq?.Body == null)
            {
                return Task.CompletedTask;
            }

            // Only when JSON output is requested
            if (!rq.Body.RequiresJsonOutput)
            {
                return Task.CompletedTask;
            }

            var schemaText = rq.Body.JsonOutputSchema;
            if (string.IsNullOrWhiteSpace(schemaText))
            {
                // Gating error is handled by core request validation; no duplication here
                return Task.CompletedTask;
            }

            try
            {
                if (!svc.TryParseSchema(schemaText, out JObject schemaObj, out string parseError))
                {
                    RecordError(context, $"Invalid JSON output schema: {parseError}");
                    return Task.CompletedTask;
                }

                // Compute provider-aware wrapping metadata. Providers may need it for decoding.
                var providerName = rq.Provider ?? string.Empty;
                var (_, info) = svc.WrapForProvider(schemaObj, providerName);
                svc.SetCurrentWrapperInfo(info);
            }
            catch (Exception ex)
            {
                RecordError(context, $"Schema validation setup failed: {ex.Message}");
            }

            return Task.CompletedTask;
        }

        private static void RecordError(PolicyContext context, string message)
        {
            var diagnostic = new SHRuntimeMessage(
                SHRuntimeMessageSeverity.Error,
                SHRuntimeMessageOrigin.Validation,
                SHMessageCode.BodyInvalid,
                message);
            context.Diagnostics.Add(diagnostic);
            // PolicyContext diagnostics alone are not propagated by the pipeline.
            var messages = new List<SHRuntimeMessage>(context.Request.Messages ?? new List<SHRuntimeMessage>());
            messages.Add(diagnostic);
            context.Request.Messages = messages;
        }
    }
}
