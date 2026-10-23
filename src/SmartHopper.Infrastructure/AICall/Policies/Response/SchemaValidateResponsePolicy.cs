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
using System.Linq;
using System.Threading.Tasks;
using SmartHopper.Infrastructure.AICall.Core.Base;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.JsonSchemas;
using SmartHopper.Infrastructure.Diagnostics;

namespace SmartHopper.Infrastructure.AICall.Policies.Response
{
    /// <summary>
    /// Validates decoded provider response content against the request's JSON schema.
    /// </summary>
    public sealed class SchemaValidateResponsePolicy : IResponsePolicy
    {
        public Task ApplyAsync(PolicyContext context)
        {
            var response = context?.Response;
            var request = context?.Request;
            if (response == null || request?.Body?.RequiresJsonOutput != true)
            {
                return Task.CompletedTask;
            }

            try
            {
                var schemaText = request.Body.JsonOutputSchema;
                if (string.IsNullOrWhiteSpace(schemaText)) return Task.CompletedTask;

                var content = response.Body?.Interactions?
                    .OfType<AIInteractionText>()
                    .Where(i => i.Agent == AIAgent.Assistant)
                    .Select(i => i.Content)
                    .LastOrDefault(c => !string.IsNullOrWhiteSpace(c));
                if (string.IsNullOrWhiteSpace(content))
                {
                    response.AddRuntimeMessage(
                        SHRuntimeMessageSeverity.Error,
                        SHRuntimeMessageOrigin.Validation,
                        "Expected JSON structured output from assistant, but content is missing");
                    return Task.CompletedTask;
                }

                var svc = JsonSchemaService.Instance;
                // Mappers may already have unwrapped the response. Validate it before extraction.
                if (svc.Validate(schemaText, content, out string error)) return Task.CompletedTask;

                // Derive metadata from this request, rather than relying on AsyncLocal writes in
                // another execution context or metadata left by a previous request.
                if (svc.TryParseSchema(schemaText, out var schema, out _))
                {
                    var (_, info) = svc.WrapForProvider(schema, request.Provider);
                    var unwrapped = svc.Unwrap(content, info);
                    if (unwrapped != content && svc.Validate(schemaText, unwrapped, out error)) return Task.CompletedTask;
                }

                response.AddRuntimeMessage(
                    SHRuntimeMessageSeverity.Error,
                    SHRuntimeMessageOrigin.Validation,
                    $"Response JSON does not match schema: {error}");
            }
            catch (Exception ex)
            {
                response.AddRuntimeMessage(
                    SHRuntimeMessageSeverity.Error,
                    SHRuntimeMessageOrigin.Validation,
                    $"Schema response validation failed: {ex.Message}");
            }

            return Task.CompletedTask;
        }
    }
}
