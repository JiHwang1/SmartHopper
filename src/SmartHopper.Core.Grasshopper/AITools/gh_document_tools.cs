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
using System.Threading.Tasks;
using Grasshopper.Kernel;
using Newtonsoft.Json.Linq;
using SmartHopper.Core.Grasshopper.Utils.Canvas;
using SmartHopper.Infrastructure.AICall.Core.Returns;
using SmartHopper.Infrastructure.AICall.Tools;
using SmartHopper.Infrastructure.AITools;

namespace SmartHopper.Core.Grasshopper.AITools
{
    /// <summary>Lists, selects, creates, opens and reads open Grasshopper documents.</summary>
    public class gh_document_tools : IAIToolProvider
    {
        internal const string ListSchema = @"{ ""type"": ""object"", ""properties"": {} }";

        internal const string SwitchSchema = @"{ ""type"": ""object"", ""properties"": { ""documentId"": { ""type"": ""string"", ""description"": ""Document id from gh_document_list."" } }, ""required"": [""documentId""] }";

        internal const string NewSchema = @"{ ""type"": ""object"", ""properties"": {} }";

        internal const string OpenSchema = @"{ ""type"": ""object"", ""properties"": { ""filePath"": { ""type"": ""string"", ""description"": ""Absolute path to a .gh or .ghx file."" } }, ""required"": [""filePath""] }";

        internal const string ReadSchema = @"{ ""type"": ""object"", ""properties"": { ""documentId"": { ""type"": ""string"", ""description"": ""Document id from gh_document_list."" }, ""page"": { ""type"": ""integer"", ""default"": 1, ""minimum"": 1 }, ""pageSize"": { ""type"": ""integer"", ""default"": 25, ""minimum"": 1, ""maximum"": 100 }, ""includeInternalizedData"": { ""type"": ""boolean"", ""default"": false } }, ""required"": [""documentId""] }";

        /// <inheritdoc/>
        public IEnumerable<AITool> GetTools()
        {
            yield return Build("gh_document_list", "List open Grasshopper documents with stable document ids and the active document. Read-only.", ListSchema, GhDocumentTools.ListOutputSchema, this.ListAsync, mutatesCanvas: false);
            yield return Build("gh_document_switch", "Switch the active Grasshopper document by documentId after host confirmation. A confirmed switch applies to subsequent tools in this chat.", SwitchSchema, GhDocumentTools.ActivationOutputSchema, this.SwitchAsync, mutatesCanvas: true);
            yield return Build("gh_document_new", "Create and activate a blank Grasshopper document after host confirmation.", NewSchema, GhDocumentTools.ActivationOutputSchema, this.NewAsync, mutatesCanvas: true);
            yield return Build("gh_document_open", "Open and activate a .gh or .ghx file after host confirmation, or activate it when its path is already open.", OpenSchema, GhDocumentTools.OpenOutputSchema, this.OpenAsync, mutatesCanvas: true);
            yield return Build("gh_document_read", "Read a page of components from one open document by documentId. This remains bound to that document even when another document becomes active.", ReadSchema, GhDocumentTools.ReadOutputSchema, this.ReadAsync, mutatesCanvas: false);
        }

        private static AITool Build(string name, string description, string parametersSchema, string outputSchema, Func<AIToolCall, Task<AIReturn>> execute, bool mutatesCanvas)
        {
            return new AITool(
                name: name,
                description: description,
                category: "Document",
                parametersSchema: parametersSchema,
                execute: execute,
                mutatesCanvas: mutatesCanvas,
                tags: new[] { "document", mutatesCanvas ? "active" : "read-only" },
                outputSchema: outputSchema,
                annotations: new AIToolAnnotations(readOnlyHint: !mutatesCanvas, destructiveHint: mutatesCanvas),
                surfaces: GhDocumentTools.InteractiveSurfaces);
        }

        private Task<AIReturn> ListAsync(AIToolCall toolCall)
        {
            return GhDocumentTools.RunAsync(toolCall, _ =>
            {
                var (documents, activeId) = GhDocumentAccess.DescribeOpenDocuments(toolCall.CancellationToken);
                var payload = new JObject
                {
                    ["documents"] = documents,
                    ["activeDocumentId"] = activeId,
                };
                payload.WithEnvelope(ToolResultEnvelope.Create("gh_document_list", ToolResultContentType.List, payloadPath: "documents"));
                return Task.FromResult(payload);
            });
        }

        private Task<AIReturn> SwitchAsync(AIToolCall toolCall)
        {
            return GhDocumentTools.RunAsync(toolCall, args =>
            {
                var documentId = RequireDocumentId(args);
                var activated = GhDocumentAccess.Activate(documentId, toolCall.CancellationToken, doc => Rebind(toolCall, doc));
                if (activated.Rejected) return Task.FromResult(RejectedResult("gh_document_switch"));
                var document = activated.Document
                    ?? throw new InvalidOperationException($"No open document with documentId '{documentId}'. Call gh_document_list to get valid ids.");
                return Task.FromResult(DescribeResult("gh_document_switch", document));
            });
        }

        private Task<AIReturn> NewAsync(AIToolCall toolCall)
        {
            return GhDocumentTools.RunAsync(toolCall, _ =>
            {
                var created = GhDocumentAccess.CreateBlank(toolCall.CancellationToken, doc => Rebind(toolCall, doc));
                if (created.Rejected) return Task.FromResult(RejectedResult("gh_document_new"));
                var document = created.Document
                    ?? throw new InvalidOperationException("Could not create a Grasshopper document. Ensure the Grasshopper canvas is available.");
                return Task.FromResult(DescribeResult("gh_document_new", document));
            });
        }

        private Task<AIReturn> OpenAsync(AIToolCall toolCall)
        {
            return GhDocumentTools.RunAsync(toolCall, args =>
            {
                var path = args["filePath"]?.ToString();
                if (!GrasshopperFilePath.TryNormalize(path, mustExist: true, out var fullPath, out var error))
                {
                    throw new ArgumentException(error);
                }

                var opened = GhDocumentAccess.Open(fullPath, toolCall.CancellationToken, doc => Rebind(toolCall, doc));
                if (opened.Rejected)
                {
                    var rejected = RejectedResult("gh_document_open");
                    rejected["alreadyOpen"] = opened.AlreadyOpen;
                    return Task.FromResult(rejected);
                }

                var document = opened.Document
                    ?? throw new InvalidOperationException($"Could not open Grasshopper document: {fullPath}");
                var result = DescribeResult("gh_document_open", document);
                result["alreadyOpen"] = opened.AlreadyOpen;
                return Task.FromResult(result);
            });
        }

        private Task<AIReturn> ReadAsync(AIToolCall toolCall)
        {
            return GhDocumentTools.RunAsync(toolCall, args =>
            {
                var documentId = RequireDocumentId(args);
                var page = ReadPositiveInt(args, "page", 1, int.MaxValue);
                var pageSize = ReadPositiveInt(args, "pageSize", 25, 100);
                var includeInternalizedData = args["includeInternalizedData"]?.Value<bool?>() ?? false;
                var document = GhDocumentAccess.FindById(documentId, toolCall.CancellationToken)
                    ?? throw new InvalidOperationException($"No open document with documentId '{documentId}'. Call gh_document_list to get valid ids.");
                var payload = GhDocumentAccess.ReadPage(document, page, pageSize, includeInternalizedData, toolCall.CancellationToken);
                payload.WithEnvelope(ToolResultEnvelope.Create("gh_document_read", ToolResultContentType.Object, payloadPath: "ghjson"));
                return Task.FromResult(payload);
            });
        }

        private static void Rebind(AIToolCall toolCall, GH_Document document)
        {
            toolCall.InvocationContext?.RebindDocument?.Invoke(document.DocumentID);
        }

        private static JObject DescribeResult(string toolName, GH_Document document)
        {
            var payload = new JObject
            {
                ["document"] = GhDocumentAccess.Describe(document, document),
                ["rejected"] = false,
            };
            payload.WithEnvelope(ToolResultEnvelope.Create(toolName, ToolResultContentType.Object, payloadPath: "document"));
            return payload;
        }

        private static JObject RejectedResult(string toolName)
        {
            var payload = new JObject
            {
                ["document"] = null,
                ["rejected"] = true,
            };
            payload.WithEnvelope(ToolResultEnvelope.Create(toolName, ToolResultContentType.Object, payloadPath: "document"));
            return payload;
        }

        private static string RequireDocumentId(JObject args)
        {
            var documentId = args["documentId"]?.ToString();
            if (!Guid.TryParse(documentId, out _)) throw new ArgumentException("A valid documentId is required. Call gh_document_list first.");
            return documentId!;
        }

        private static int ReadPositiveInt(JObject args, string name, int fallback, int maximum)
        {
            var value = args[name]?.Value<int?>() ?? fallback;
            if (value < 1 || value > maximum) throw new ArgumentException($"{name} must be between 1 and {maximum}.");
            return value;
        }
    }
}
