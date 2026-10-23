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
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;
    using SmartHopper.Infrastructure.AICall.Core.Interactions;
    using SmartHopper.Infrastructure.AICall.Core.Returns;
    using SmartHopper.Infrastructure.AICall.Validation;
    using SmartHopper.Infrastructure.AITools;
    using SmartHopper.Infrastructure.Diagnostics;
    using Xunit;

    [Collection("AIToolManager")]
    public class ToolJsonSchemaRegressionTests
    {
        [Theory]
        [InlineData("{\"type\":\"object\",\"minProperties\":1}", false)]
        [InlineData("{\"type\":\"object\",\"required\":[\"name\"]}", false)]
        [InlineData("{\"type\":\"object\",\"minProperties\":-1}", false)]
        [InlineData("{\"type\":\"object\"}", true)]
        public async Task MissingArguments_AreNormalizedAndFullyValidated(string schema, bool expected)
        {
            var call = new AIInteractionToolCall { Name = "schema_regression", Arguments = null! };
            var result = await ValidateToolAsync(schema, call);

            Assert.NotNull(call.Arguments);
            Assert.Empty(call.Arguments);
            Assert.Equal(expected, result.IsValid);
            if (!expected) Assert.Contains(result.Messages, m => m.Severity == SHRuntimeMessageSeverity.Error);
        }

        [Theory]
        [InlineData("{\"payload\":{}}", false)]
        [InlineData("{\"payload\":{\"count\":\"2\"}}", false)]
        [InlineData("{\"payload\":{\"count\":0}}", false)]
        [InlineData("{\"payload\":{\"count\":2}}", true)]
        public async Task SuppliedArguments_EnforceNestedSchema(string arguments, bool expected)
        {
            const string schema = """
                {"type":"object","required":["payload"],"properties":{"payload":{
                 "type":"object","required":["count"],"properties":{"count":{"type":"integer","minimum":1}}}}}
                """;
            var call = new AIInteractionToolCall { Name = "schema_regression", Arguments = JObject.Parse(arguments) };
            Assert.Equal(expected, (await ValidateToolAsync(schema, call)).IsValid);
        }

        [Fact]
        public async Task Required_UsesSchemaSemanticsForNullableAndEmptyValues()
        {
            const string schema = """
                {"type":"object","required":["name","optionalValue"],"properties":{
                  "name":{"type":"string"},"optionalValue":{"type":["string","null"]}}}
                """;
            var call = new AIInteractionToolCall
            {
                Name = "schema_regression",
                Arguments = JObject.Parse("{\"name\":\"\",\"optionalValue\":null}"),
            };
            Assert.True((await ValidateToolAsync(schema, call)).IsValid);
        }

        private static async Task<ValidationResult> ValidateToolAsync(string schema, AIInteractionToolCall call)
        {
            AIToolManager.ResetTools();
            try
            {
                AIToolManager.RegisterTool(new AITool("schema_regression", "Schema regression test", "test", schema,
                    _ => Task.FromResult(new AIReturn())));
                return await new ToolJsonSchemaValidator().ValidateAsync(call, new ValidationContext(), CancellationToken.None);
            }
            finally
            {
                AIToolManager.ResetTools();
            }
        }
    }
}
