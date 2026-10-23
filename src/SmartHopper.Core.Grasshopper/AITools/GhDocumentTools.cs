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
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Newtonsoft.Json.Linq;
using SmartHopper.Core.Grasshopper.Utils.Canvas;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Tools;
using SmartHopper.Infrastructure.AITools;

namespace SmartHopper.Core.Grasshopper.AITools
{
    /// <summary>Shared execution and lookup logic for document tools.</summary>
    internal static class GhDocumentTools
    {
        internal const string DocumentSchema = @"{ ""type"": ""object"", ""properties"": { ""documentId"": { ""type"": ""string"" }, ""name"": { ""type"": ""string"" }, ""filePath"": { ""type"": [""string"", ""null"" ] }, ""isActive"": { ""type"": ""boolean"" }, ""isModified"": { ""type"": ""boolean"" }, ""objectCount"": { ""type"": ""integer"" } } }";

        internal const string ListOutputSchema = @"{ ""type"": ""object"", ""properties"": { ""documents"": { ""type"": ""array"", ""items"": " + DocumentSchema + @" }, ""activeDocumentId"": { ""type"": [""string"", ""null"" ] } } }";

        internal const string ReadOutputSchema = @"{ ""type"": ""object"", ""properties"": { ""document"": " + DocumentSchema + @", ""ghjson"": { ""type"": ""string"" }, ""pagination"": { ""type"": ""object"", ""properties"": { ""page"": { ""type"": ""integer"" }, ""pageSize"": { ""type"": ""integer"" }, ""totalComponents"": { ""type"": ""integer"" }, ""pageCount"": { ""type"": ""integer"" }, ""returnedComponents"": { ""type"": ""integer"" } } } } }";

        internal const string ActivationOutputSchema = @"{ ""type"": ""object"", ""properties"": { ""document"": { ""anyOf"": [" + DocumentSchema + @", { ""type"": ""null"" }] }, ""rejected"": { ""type"": ""boolean"" } } }";

        internal const string OpenOutputSchema = @"{ ""type"": ""object"", ""properties"": { ""document"": { ""anyOf"": [" + DocumentSchema + @", { ""type"": ""null"" }] }, ""alreadyOpen"": { ""type"": ""boolean"" }, ""rejected"": { ""type"": ""boolean"" } } }";

        internal const string SaveOutputSchema = @"{ ""type"": ""object"", ""properties"": { ""document"": { ""anyOf"": [" + DocumentSchema + @", { ""type"": ""null"" }] }, ""saved"": { ""type"": ""boolean"" }, ""rejected"": { ""type"": ""boolean"" }, ""filePath"": { ""type"": [""string"", ""null"" ] } } }";

        /// <summary>Surface restriction for tools that change which document is active.</summary>
        public const AIToolSurface InteractiveSurfaces = AIToolSurface.Chat | AIToolSurface.Direct | AIToolSurface.Mcp;

        public static Task<AIReturn> Run(AIToolCall toolCall, Func<JObject, JObject> action)
        {
            var output = new AIReturn { Request = toolCall };
            try
            {
                toolCall.SkipMetricsValidation = true;
                var info = toolCall.GetToolCall();
                var payload = action(info.GetArgumentsOrEmpty());
                output.CreateSuccess(AIBodyBuilder.Create().AddToolResult(payload, id: info.Id, name: info.Name).Build(), toolCall);
            }
            catch (Exception ex)
            {
                output.CreateError(ex is InvalidOperationException ? ex.Message : $"Error: {ex.Message}");
            }

            return Task.FromResult(output);
        }

        public static async Task<AIReturn> RunAsync(AIToolCall toolCall, Func<JObject, Task<JObject>> action)
        {
            var output = new AIReturn { Request = toolCall };
            try
            {
                toolCall.SkipMetricsValidation = true;
                toolCall.CancellationToken.ThrowIfCancellationRequested();
                var info = toolCall.GetToolCall();
                var payload = await action(info.GetArgumentsOrEmpty()).ConfigureAwait(false);
                toolCall.CancellationToken.ThrowIfCancellationRequested();
                output.CreateSuccess(AIBodyBuilder.Create().AddToolResult(payload, id: info.Id, name: info.Name).Build(), toolCall);
            }
            catch (OperationCanceledException) when (toolCall.CancellationToken.IsCancellationRequested)
            {
                output.CreateError("Cancelled.");
            }
            catch (Exception ex)
            {
                output.CreateError(ex is InvalidOperationException || ex is ArgumentException ? ex.Message : $"Error: {ex.Message}");
            }

            return output;
        }

        public static GH_Document RequireDocument(string? documentId)
        {
            return GhDocumentAccess.FindById(documentId)
                ?? throw new InvalidOperationException($"No open document with documentId '{documentId}'. Call gh_document_list to get valid ids.");
        }
    }
}
