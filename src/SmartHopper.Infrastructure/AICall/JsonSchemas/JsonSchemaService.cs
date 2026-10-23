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
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using Json.Schema;
using Newtonsoft.Json.Linq;

namespace SmartHopper.Infrastructure.AICall.JsonSchemas
{
    /// <summary>
    /// Default implementation of IJsonSchemaService.
    /// </summary>
    public sealed class JsonSchemaService : IJsonSchemaService
    {
        private const int MaximumCachedSchemas = 128;
        private const int MaximumCachedSchemaLength = 16 * 1024;
        private static readonly object SchemaCacheLock = new object();
        private static readonly Dictionary<string, CachedSchema> SchemaCache = new Dictionary<string, CachedSchema>(StringComparer.Ordinal);
        private static readonly Queue<string> SchemaCacheOrder = new Queue<string>();
        private static readonly Lazy<JsonSchemaService> _instance = new Lazy<JsonSchemaService>(() => new JsonSchemaService());
        // goinmul: Provider mappers retain ambient metadata; child-context writes do not flow back.
        // Pass metadata explicitly when the provider mapping execution boundaries are redesigned.
        private static readonly AsyncLocal<SchemaWrapperInfo> _currentWrapper = new AsyncLocal<SchemaWrapperInfo>();

        public static JsonSchemaService Instance => _instance.Value;

        private JsonSchemaService()
        {
        }

        public bool TryParseSchema(string schemaJson, out JObject schema, out string error)
        {
            schema = new JObject();
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(schemaJson))
            {
                error = "Schema is empty";
                return false;
            }

            try
            {
                if (!TryLoadSchema(schemaJson, out var cached, out error))
                {
                    return false;
                }

                if (cached.ObjectSchema == null)
                {
                    error = "An object-root schema definition is required for provider output.";
                    return false;
                }

                // Provider transformations must never mutate the cached schema definition.
                schema = (JObject)cached.ObjectSchema.DeepClone();
                return true;
            }
            catch (Exception ex)
            {
                error = $"Invalid JSON schema: {ex.Message}";
                return false;
            }
        }

        public (JObject wrapped, SchemaWrapperInfo info) WrapForProvider(JObject schema, string provider)
        {
            if (schema is null) throw new ArgumentNullException(nameof(schema));
            var adapter = JsonSchemaAdapterRegistry.Get(provider);
            var (wrapped, info) = adapter.Wrap(schema);

            // Ensure provider name is captured
            info.ProviderName = string.IsNullOrWhiteSpace(info.ProviderName) ? (provider ?? string.Empty) : info.ProviderName;
            return (wrapped, info);
        }

        public string Unwrap(string content, SchemaWrapperInfo info)
        {
            if (info == null || !info.IsWrapped || string.IsNullOrWhiteSpace(content))
            {
                return content;
            }

            try
            {
                // Allow provider adapter to pre-process content (e.g., cleanup) before default extraction
                var adapter = JsonSchemaAdapterRegistry.Get(info.ProviderName);
                var preProcessed = adapter.Unwrap(content, info) ?? content;

                var token = JToken.Parse(preProcessed);
                if (token is not JObject obj) return preProcessed;
                var value = obj[info.PropertyName];
                if (value == null) return preProcessed;
                return value.ToString(Newtonsoft.Json.Formatting.None);
            }
            catch
            {
                // On failure, return original content
                return content;
            }
        }

        public bool Validate(string schemaJson, string json, out string error)
        {
            error = string.Empty;

            if (!TryLoadSchema(schemaJson, out var cached, out error))
            {
                return false;
            }

            JsonNode instance;
            try
            {
                instance = JsonNode.Parse(json);
            }
            catch (Exception ex)
            {
                error = $"Invalid instance JSON: {ex.Message}";
                return false;
            }

            try
            {
                // JsonSchema.Net 7.2.2 keeps mutable constraint state on each schema. Keep the
                // original options and serialize evaluation of that schema, including boolean singletons.
                lock (cached.Schema)
                {
                    var results = cached.Schema.Evaluate(instance, cached.Options);
                    if (results.IsValid) return true;
                    error = DescribeErrors(results);
                }
            }
            catch (Exception ex)
            {
                error = $"JSON Schema evaluation failed: {ex.Message}";
            }

            return false;
        }

        private static bool TryLoadSchema(string schemaJson, out CachedSchema cached, out string error)
        {
            cached = null;
            if (string.IsNullOrWhiteSpace(schemaJson))
            {
                error = "Invalid JSON schema: Schema is empty";
                return false;
            }

            // Bound retained input to 128 definitions of at most 16K characters each. Larger
            // schemas still validate normally, but are not retained. Do not cache invalid schemas.
            // Misses also serialize built-in meta-schema evaluation, whose constraints are mutable.
            lock (SchemaCacheLock)
            {
                if (SchemaCache.TryGetValue(schemaJson, out cached))
                {
                    error = string.Empty;
                    return true;
                }

                if (!TryCompileSchema(schemaJson, out cached, out error)) return false;

                if (schemaJson.Length <= MaximumCachedSchemaLength)
                {
                    if (SchemaCache.Count == MaximumCachedSchemas)
                    {
                        SchemaCache.Remove(SchemaCacheOrder.Dequeue());
                    }

                    SchemaCache.Add(schemaJson, cached);
                    SchemaCacheOrder.Enqueue(schemaJson);
                }

                return true;
            }
        }

        private static bool TryCompileSchema(string schemaJson, out CachedSchema cached, out string error)
        {
            cached = null;
            error = string.Empty;
            var options = new EvaluationOptions
            {
                EvaluateAs = SpecVersion.Draft202012,
                OutputFormat = OutputFormat.List,
                Culture = CultureInfo.InvariantCulture,
            };

            // Throw rather than return null: 7.2.2 otherwise falls back to Global.Fetch.
            // Embedded definitions and the library's built-in meta-schemas remain available.
            options.SchemaRegistry.Fetch = uri => throw new InvalidOperationException($"External schema resolution is disabled: {uri}");

            try
            {
                var schemaNode = JsonNode.Parse(schemaJson);
                var schema = JsonSchema.FromText(schemaJson);
                var dialect = schemaNode is JsonObject obj && obj["$schema"] != null
                    ? new Uri(obj["$schema"].GetValue<string>(), UriKind.Absolute)
                    : MetaSchemas.Draft202012Id;

                if (options.SchemaRegistry.Get(dialect) is not JsonSchema metaSchema)
                {
                    error = $"Unsupported JSON Schema dialect: {dialect}";
                    return false;
                }

                // Evaluate the definition as the instance of its meta-schema. The 7.2.2
                // ValidateAgainstMetaSchema switch instead evaluates the data instance.
                var definitionResult = metaSchema.Evaluate(schemaNode, options);
                if (!definitionResult.IsValid)
                {
                    error = $"Invalid JSON schema: {DescribeErrors(definitionResult)}";
                    return false;
                }

                // Build constraints now to reject unresolved references even before data arrives.
                // Whether null satisfies the schema is irrelevant to definition validity.
                lock (schema)
                {
                    _ = schema.Evaluate((JsonNode)null, options);
                }

                cached = new CachedSchema(schema, options, schemaNode is JsonObject ? JObject.Parse(schemaJson) : null);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Invalid JSON schema: {ex.Message}";
                return false;
            }
        }

        private static string DescribeErrors(EvaluationResults results)
        {
            var errors = EnumerateErrors(results).Take(8).ToList();
            return errors.Count > 0 ? string.Join("; ", errors) : "JSON does not satisfy the schema.";
        }

        private sealed class CachedSchema
        {
            public CachedSchema(JsonSchema schema, EvaluationOptions options, JObject objectSchema)
            {
                this.Schema = schema;
                this.Options = options;
                this.ObjectSchema = objectSchema;
            }

            public JsonSchema Schema { get; }

            public EvaluationOptions Options { get; }

            public JObject ObjectSchema { get; }
        }

        private static IEnumerable<string> EnumerateErrors(EvaluationResults results)
        {
            if (results.HasErrors)
            {
                foreach (var entry in results.Errors)
                {
                    yield return $"At '{results.InstanceLocation}' ({entry.Key}): {entry.Value}";
                }
            }

            if (results.HasDetails)
            {
                foreach (var detail in results.Details)
                {
                    foreach (var error in EnumerateErrors(detail))
                    {
                        yield return error;
                    }
                }
            }
        }

        public void SetCurrentWrapperInfo(SchemaWrapperInfo info)
        {
            _currentWrapper.Value = info;
        }

        public SchemaWrapperInfo GetCurrentWrapperInfo()
        {
            return _currentWrapper.Value;
        }
    }
}
