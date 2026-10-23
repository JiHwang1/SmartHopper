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
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AICall.Core.Base;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.JsonSchemas;
using SmartHopper.Infrastructure.AICall.Utilities;
using SmartHopper.Infrastructure.AITools;
using SmartHopper.Infrastructure.Diagnostics;

namespace SmartHopper.Infrastructure.AICall.Validation
{
    /// <summary>
    /// Validates a tool call's arguments against the tool's JSON parameters schema.
    /// </summary>
    public sealed class ToolJsonSchemaValidator : IValidator<AIInteractionToolCall>
    {
        public SHRuntimeMessageSeverity FailOn { get; } = SHRuntimeMessageSeverity.Error;

        public Task<ValidationResult> ValidateAsync(AIInteractionToolCall instance, ValidationContext context, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var messages = new List<SHRuntimeMessage>();

            if (instance == null)
            {
                messages.Add(new SHRuntimeMessage(
                    SHRuntimeMessageSeverity.Error,
                    SHRuntimeMessageOrigin.Validation,
                    SHMessageCode.ToolValidationError,
                    "Tool call instance is null"));
                var early = new ValidationResult
                {
                    Messages = messages,
                };
                early.IsValid = false;
                return Task.FromResult(early);
            }

            var tools = AIToolManager.GetTools();
            if (string.IsNullOrWhiteSpace(instance.Name) || !tools.ContainsKey(instance.Name))
            {
                // Defer unknown tool handling to ToolExistsValidator; avoid double-reporting here
                var pass = new ValidationResult { Messages = messages };
                pass.IsValid = true;
                return Task.FromResult(pass);
            }

            var tool = tools[instance.Name];
            var schemaText = tool.ParametersSchema ?? string.Empty;

            if (string.IsNullOrWhiteSpace(schemaText))
            {
                // No schema to validate; treat as pass
                var ok = new ValidationResult { Messages = messages };
                ok.IsValid = true;
                return Task.FromResult(ok);
            }

            var createdArguments = instance.Arguments == null;
            if (createdArguments)
            {
                // Preserve optional-argument normalization, then validate the actual normalized object.
                instance.Arguments = new JObject();
            }

            var json = instance.Arguments.ToString(Formatting.None);
            if (!JsonSchemaService.Instance.Validate(schemaText, json, out string error))
            {
                messages.Add(new SHRuntimeMessage(
                    SHRuntimeMessageSeverity.Error,
                    SHRuntimeMessageOrigin.Validation,
                    SHMessageCode.ToolValidationError,
                    $"Arguments for tool '{instance.Name}' do not match schema: {error}. Retry the tool call with arguments that satisfy the schema."));
            }
            else if (createdArguments)
            {
                messages.Add(new SHRuntimeMessage(
                    SHRuntimeMessageSeverity.Info,
                    SHRuntimeMessageOrigin.Validation,
                    SHMessageCode.ToolValidationError,
                    $"No arguments provided for tool '{instance.Name}'. Created default empty arguments {{}} to satisfy the schema."));
            }

            var result = new ValidationResult
            {
                Messages = messages,
            };
            result.IsValid = !RuntimeMessageUtility.HasSeverityAtOrAbove(messages, this.FailOn);

            return Task.FromResult(result);
        }
    }
}
