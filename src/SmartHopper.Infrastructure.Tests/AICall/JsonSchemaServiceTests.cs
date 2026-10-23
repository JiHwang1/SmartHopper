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

namespace SmartHopper.Infrastructure.Tests.AICall
{
    using System;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;
    using SmartHopper.Infrastructure.AICall.JsonSchemas;
    using Xunit;

    public class JsonSchemaServiceTests
    {
        [Theory]
        [InlineData("{\"type\":\"integer\"}", "3", true)]
        [InlineData("{\"type\":\"integer\"}", "3.5", false)]
        [InlineData("{\"type\":\"number\",\"minimum\":1,\"maximum\":3}", "0", false)]
        [InlineData("{\"type\":\"number\",\"minimum\":1,\"maximum\":3}", "4", false)]
        [InlineData("{\"type\":\"string\",\"minLength\":2,\"maxLength\":4}", "\"a\"", false)]
        [InlineData("{\"type\":\"string\",\"minLength\":2,\"maxLength\":4}", "\"abcde\"", false)]
        [InlineData("{\"enum\":[\"red\",\"blue\"]}", "\"green\"", false)]
        [InlineData("{\"enum\":[\"red\",\"blue\"]}", "\"red\"", true)]
        [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"minItems\":1}", "[]", false)]
        [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}", "[1,\"two\"]", false)]
        [InlineData("{\"type\":\"null\"}", "null", true)]
        [InlineData("true", "null", true)]
        [InlineData("false", "{}", false)]
        [InlineData("{}", "not-json", false)]
        public void Validate_EvaluatesConstraints(string schema, string json, bool expected)
        {
            var valid = JsonSchemaService.Instance.Validate(schema, json, out var error);

            Assert.Equal(expected, valid);
            Assert.Equal(expected, string.IsNullOrEmpty(error));
        }

        [Theory]
        [InlineData("{}", false)]
        [InlineData("{\"profile\":{}}", false)]
        [InlineData("{\"profile\":{\"age\":\"adult\"}}", false)]
        [InlineData("{\"profile\":{\"age\":17}}", false)]
        [InlineData("{\"profile\":{\"age\":21,\"extra\":true}}", false)]
        [InlineData("{\"profile\":{\"age\":21}}", true)]
        public void Validate_EnforcesNestedRequiredTypesAndBounds(string json, bool expected)
        {
            const string schema = """
                {"type":"object","required":["profile"],"properties":{
                  "profile":{"type":"object","required":["age"],"additionalProperties":false,
                    "properties":{"age":{"type":"integer","minimum":18}}}}}
                """;

            Assert.Equal(expected, JsonSchemaService.Instance.Validate(schema, json, out _));
        }

        [Theory]
        [InlineData("{\"type\":\"not-a-type\"}")]
        [InlineData("{\"type\":\"object\",\"required\":\"name\"}")]
        [InlineData("{\"minLength\":-1}")]
        [InlineData("{\"required\":[\"name\",\"name\"]}")]
        [InlineData("{\"properties\":{\"nested\":{\"minItems\":-1}}}")]
        [InlineData("[]")]
        [InlineData("null")]
        [InlineData("{broken")]
        public void InvalidSchema_IsRejectedByParsingAndValidation(string schema)
        {
            Assert.False(JsonSchemaService.Instance.TryParseSchema(schema, out _, out var parseError));
            Assert.False(string.IsNullOrWhiteSpace(parseError));
            Assert.False(JsonSchemaService.Instance.Validate(schema, "{}", out var error));
            Assert.Contains("schema", error, StringComparison.OrdinalIgnoreCase);
        }

        [Theory]
        [InlineData("{\"$ref\":\"https://example.invalid/schema.json\"}")]
        [InlineData("{\"$ref\":\"file:///C:/private/schema.json\"}")]
        [InlineData("{\"$schema\":\"https://example.invalid/meta-schema\"}")]
        public void ExternalReferences_AreRejectedWithoutFetching(string schema)
        {
            Assert.False(JsonSchemaService.Instance.TryParseSchema(schema, out _, out var error));
            Assert.Contains("External schema resolution is disabled", error);
            Assert.False(JsonSchemaService.Instance.Validate(schema, "{}", out _));
        }

        [Theory]
        [InlineData("http://json-schema.org/draft-07/schema#")]
        [InlineData("https://json-schema.org/draft/2019-09/schema")]
        [InlineData("https://json-schema.org/draft/2020-12/schema")]
        public void BuiltInDialects_ValidateDefinitionsAndInstances(string dialect)
        {
            var schema = new JObject
            {
                ["$schema"] = dialect,
                ["type"] = "integer",
                ["minimum"] = 2,
            }.ToString();

            Assert.True(JsonSchemaService.Instance.TryParseSchema(schema, out _, out var error), error);
            Assert.True(JsonSchemaService.Instance.Validate(schema, "2", out error), error);
            Assert.False(JsonSchemaService.Instance.Validate(schema, "1", out _));
        }

        [Fact]
        public void EmbeddedReferences_AreResolvedLocally()
        {
            const string schema = """
                {"$id":"https://example.invalid/local", "$defs":{"positive":{"type":"integer","minimum":1}},
                 "type":"object","properties":{"count":{"$ref":"#/$defs/positive"}}}
                """;

            Assert.True(JsonSchemaService.Instance.Validate(schema, "{\"count\":1}", out var error), error);
            Assert.False(JsonSchemaService.Instance.Validate(schema, "{\"count\":0}", out _));
        }

        [Fact]
        public void ConcurrentValidations_KeepEvaluationStateSeparate()
        {
            Parallel.For(0, 64, index =>
            {
                var schema = $"{{\"type\":\"integer\",\"minimum\":{index}}}";
                Assert.True(JsonSchemaService.Instance.Validate(schema, index.ToString(System.Globalization.CultureInfo.InvariantCulture), out var error), error);
                Assert.False(JsonSchemaService.Instance.Validate(schema, "-1", out _));
            });
        }

        [Fact]
        public void ConcurrentValidations_ReuseOneSchemaWithoutLeakingResults()
        {
            const string schema = "{\"type\":\"object\",\"required\":[\"count\"],\"properties\":{\"count\":{\"type\":\"integer\",\"minimum\":1}}}";
            Parallel.For(0, 128, index =>
            {
                var expected = index % 2 == 0;
                var json = expected ? "{\"count\":1}" : "{\"count\":0}";
                Assert.Equal(expected, JsonSchemaService.Instance.Validate(schema, json, out _));
            });
        }

        [Fact]
        public void CachedDefinition_IsNotModifiedByProviderSchemaMutations()
        {
            const string schemaText = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}},\"required\":[\"name\"]}";
            var svc = JsonSchemaService.Instance;
            Assert.True(svc.TryParseSchema(schemaText, out var first, out var error), error);
            first["properties"]!["name"]!["type"] = "integer";
            ((JArray)first["required"]!).Clear();

            Assert.True(svc.TryParseSchema(schemaText, out var second, out error), error);
            Assert.Equal("string", second["properties"]!["name"]!["type"]!.Value<string>());
            Assert.Single((JArray)second["required"]!);
            Assert.True(svc.Validate(schemaText, "{\"name\":\"Alice\"}", out error), error);
            Assert.False(svc.Validate(schemaText, "{\"name\":1}", out _));
        }

        [Fact]
        public void CacheEviction_PreservesValidationAndDoesNotRetainLargeSchemas()
        {
            var svc = JsonSchemaService.Instance;
            for (var index = 0; index < 160; index++)
            {
                var schema = $"{{\"title\":\"cache-eviction-{index}\",\"type\":\"integer\",\"minimum\":{index}}}";
                Assert.True(svc.Validate(schema, index.ToString(System.Globalization.CultureInfo.InvariantCulture), out var error), error);
                Assert.False(svc.Validate(schema, "-1", out _));
            }

            var largeSchema = new JObject { ["type"] = "integer", ["minimum"] = 1, ["description"] = new string('x', 20000) }.ToString();
            Assert.True(svc.Validate(largeSchema, "1", out var largeError), largeError);
            Assert.False(svc.Validate(largeSchema, "0", out _));

            var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var cacheLock = typeof(JsonSchemaService).GetField("SchemaCacheLock", flags)!.GetValue(null)!;
            lock (cacheLock)
            {
                var cache = (System.Collections.IDictionary)typeof(JsonSchemaService).GetField("SchemaCache", flags)!.GetValue(null)!;
                Assert.InRange(cache.Count, 1, 128);
                Assert.False(cache.Contains(largeSchema));
            }

            Assert.True(svc.Validate("{\"title\":\"cache-eviction-0\",\"type\":\"integer\",\"minimum\":0}", "0", out var firstError), firstError);
        }

        [Theory]
        [InlineData("{\"type\":\"array\"}", "items", "array")]
        [InlineData("{\"type\":\"string\"}", "value", "string")]
        [InlineData("{\"type\":\"number\"}", "value", "number")]
        [InlineData("{\"type\":\"integer\"}", "value", "integer")]
        [InlineData("{\"type\":\"boolean\"}", "value", "boolean")]
        [InlineData("{\"oneOf\":[{\"type\":\"string\"},{\"type\":\"null\"}]}", "data", "unknown")]
        public void DefaultWrapper_PreservesWireShapeAndMetadata(string schemaText, string property, string wrapperType)
        {
            var schema = JObject.Parse(schemaText);
            var before = schema.ToString();
            var (wrapped, info) = JsonSchemaService.Instance.WrapForProvider(schema, "unregistered-schema-test-provider");

            Assert.Equal("object", (string)wrapped["type"]);
            Assert.True(JToken.DeepEquals(schema, wrapped["properties"]?[property]));
            Assert.Equal(new[] { property }, wrapped["required"]!.ToObject<string[]>());
            Assert.False((bool)wrapped["additionalProperties"]!);
            Assert.Equal(before, schema.ToString());
            Assert.True(info.IsWrapped);
            Assert.Equal(property, info.PropertyName);
            Assert.Equal(wrapperType, info.WrapperType);
            Assert.Equal("unregistered-schema-test-provider", info.ProviderName);
        }

        [Fact]
        public void ObjectRoot_IsReturnedWithoutWrapping()
        {
            var schema = JObject.Parse("{\"type\":\"object\"}");
            var (wrapped, info) = JsonSchemaAdapterRegistry.Default.Wrap(schema);
            Assert.Same(schema, wrapped);
            Assert.False(info.IsWrapped);
        }

        [Theory]
        [InlineData("{\"value\":\"hello\\nworld\"}", "\"hello\\nworld\"")]
        [InlineData("{\"value\":true}", "true")]
        [InlineData("{\"value\":null}", "null")]
        [InlineData("{\"value\":5}", "5")]
        public void Unwrap_PreservesValidPrimitiveJson(string content, string expected)
        {
            var info = new SchemaWrapperInfo { IsWrapped = true, PropertyName = "value" };
            Assert.Equal(expected, JsonSchemaService.Instance.Unwrap(content, info));
        }

        [Fact]
        public void Unwrap_PreservesArrayReturnedByProviderRepair()
        {
            var adapter = new ArrayRepairAdapter();
            JsonSchemaAdapterRegistry.Register(adapter);
            var info = new SchemaWrapperInfo { IsWrapped = true, PropertyName = "items", ProviderName = adapter.ProviderName };

            Assert.Equal("[1,2]", JsonSchemaService.Instance.Unwrap("{\"enum\":[1,2]}", info));
        }

        private sealed class ArrayRepairAdapter : IJsonSchemaAdapter
        {
            public string ProviderName => "schema-test-array-repair";

            public (JObject wrapped, SchemaWrapperInfo info) Wrap(JObject schema) => JsonSchemaAdapterRegistry.Default.Wrap(schema);

            public string Unwrap(string content, SchemaWrapperInfo info) => "[1,2]";
        }
    }
}
