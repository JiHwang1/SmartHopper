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
    using System.Threading.Tasks;
    using SmartHopper.Infrastructure.AICall.Core.Base;
    using SmartHopper.Infrastructure.AICall.Core.Interactions;
    using SmartHopper.Infrastructure.AICall.Core.Requests;
    using SmartHopper.Infrastructure.AICall.Core.Returns;
    using SmartHopper.Infrastructure.AICall.JsonSchemas;
    using SmartHopper.Infrastructure.AICall.Policies;
    using SmartHopper.Infrastructure.AICall.Policies.Request;
    using SmartHopper.Infrastructure.AICall.Policies.Response;
    using SmartHopper.Infrastructure.Diagnostics;
    using Xunit;

    public class SchemaPolicyRegressionTests
    {
        [Fact]
        public async Task Attach_CanonicalizesValidSchema()
        {
            var context = new PolicyContext { Request = CreateRequest("{ \"type\" : \"object\" }") };
            await new SchemaAttachRequestPolicy().ApplyAsync(context);

            Assert.Equal("{\"type\":\"object\"}", context.Request.Body.JsonOutputSchema);
        }

        [Theory]
        [InlineData("{\"minLength\":-1}")]
        [InlineData("{\"$ref\":\"https://example.invalid/schema\"}")]
        public async Task InvalidRequestSchema_ProducesRequestLevelError(string schema)
        {
            var request = CreateRequest(schema);
            var context = new PolicyContext { Request = request };
            await new SchemaAttachRequestPolicy().ApplyAsync(context);
            await new SchemaValidateRequestPolicy().ApplyAsync(context);

            Assert.Contains(context.Diagnostics, m => m.Severity == SHRuntimeMessageSeverity.Error);
            Assert.Contains(request.Messages, m => m.Code == SHMessageCode.BodyInvalid && m.Severity == SHRuntimeMessageSeverity.Error);
            Assert.Equal(schema, request.Body.JsonOutputSchema);
        }

        [Fact]
        public async Task RequestWithoutSchema_ClearsAmbientWrapperMetadata()
        {
            var svc = JsonSchemaService.Instance;
            var previous = svc.GetCurrentWrapperInfo();
            try
            {
                svc.SetCurrentWrapperInfo(new SchemaWrapperInfo { IsWrapped = true, PropertyName = "stale" });
                // ApplyAsync completes synchronously so this assignment occurs in the same execution context.
                var task = new SchemaValidateRequestPolicy().ApplyAsync(new PolicyContext { Request = CreateRequest(string.Empty) });
                Assert.Null(svc.GetCurrentWrapperInfo());
                await task;
            }
            finally
            {
                svc.SetCurrentWrapperInfo(previous);
            }
        }

        [Theory]
        [InlineData("{\"type\":\"integer\",\"minimum\":2}", "1", false)]
        [InlineData("{\"type\":\"integer\",\"minimum\":2}", "2", true)]
        [InlineData("{\"type\":\"integer\",\"minimum\":2}", "{\"value\":2}", true)]
        [InlineData("{\"type\":\"string\"}", "{\"value\":\"hello\"}", true)]
        [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}", "{\"items\":[1,2]}", true)]
        [InlineData("{\"type\":\"array\",\"items\":{\"type\":\"integer\"}}", "{\"items\":[\"wrong\"]}", false)]
        [InlineData("{\"type\":\"object\",\"required\":[\"value\"]}", "{\"value\":\"keep me\"}", true)]
        [InlineData("{\"required\":[\"data\"]}", "{\"data\":{}}", true)]
        public async Task Response_UsesItsRequestSchemaAndAcceptsWrappedOrDecodedJson(string schema, string json, bool expected)
        {
            var svc = JsonSchemaService.Instance;
            var previous = svc.GetCurrentWrapperInfo();
            try
            {
                svc.SetCurrentWrapperInfo(new SchemaWrapperInfo { IsWrapped = true, PropertyName = "value" });
                var request = CreateRequest(schema);
                // This policy receives its request through PolicyContext. Keep the synthetic
                // return independent of provider/model/metrics validation performed elsewhere.
                var response = new AIReturn { SkipRequestValidation = true, SkipMetricsValidation = true };
                response.SetBody(AIBodyBuilder.Create().AddText(AIAgent.Assistant, json).Build());
                Assert.Empty(response.Messages);

                await new SchemaValidateResponsePolicy().ApplyAsync(new PolicyContext { Request = request, Response = response });

                Assert.Equal(!expected, response.Messages.Exists(m => m.Severity == SHRuntimeMessageSeverity.Error));
                Assert.Equal(json, response.Body.GetLastText());
            }
            finally
            {
                svc.SetCurrentWrapperInfo(previous);
            }
        }

        private static AIRequestCall CreateRequest(string schema)
        {
            return new AIRequestCall
            {
                Provider = "schema-regression-provider",
                Model = "test-model",
                Body = AIBodyBuilder.Create().WithJsonOutputSchema(schema).AddText(AIAgent.User, "test").Build(),
            };
        }
    }
}
