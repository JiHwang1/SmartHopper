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

#if NET7_WINDOWS
namespace SmartHopper.Infrastructure.Tests.AICall
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Moq;
    using SmartHopper.Infrastructure.AICall.Core.Base;
    using SmartHopper.Infrastructure.AICall.Core.Interactions;
    using SmartHopper.Infrastructure.AICall.Core.Requests;
    using SmartHopper.Infrastructure.AICall.Core.Returns;
    using SmartHopper.Infrastructure.AICall.Execution;
    using SmartHopper.Infrastructure.AICall.Metrics;
    using SmartHopper.Infrastructure.AICall.Policies;
    using SmartHopper.Infrastructure.AIProviders;
    using SmartHopper.Infrastructure.Diagnostics;
    using Xunit;

    // The cancellation test briefly installs a policy in the process-wide default pipeline.
    [CollectionDefinition("Schema request execution", DisableParallelization = true)]
    public sealed class SchemaRequestExecutionCollection
    {
    }

    [Collection("Schema request execution")]
    public class SchemaRequestExecutionTests
    {
        [Theory]
        [InlineData("{\"type\":\"not-a-type\"}")]
        [InlineData("{\"properties\":{\"items\":{\"minItems\":-1}}}")]
        [InlineData("{\"$ref\":\"https://example.invalid/schema\"}")]
        [InlineData("true")]
        public async Task Exec_InvalidOutputSchema_NeverInvokesProvider(string schema)
        {
            var provider = CreateProvider();
            var request = CreateRequest(provider.Object, schema);
            // Before policies run, provider/model/body validation succeeds. A failure below must
            // therefore come from schema validation, not a missing provider or malformed test request.
            Assert.True(request.IsValid().IsValid);

            var result = await new DefaultProviderExecutor().ExecProviderAsync(request, CancellationToken.None);

            Assert.NotNull(result);
            Assert.False(request.IsValid().IsValid);
            Assert.Contains(request.Messages, m => m.Code == SHMessageCode.BodyInvalid && m.Message.Contains("schema", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result!.Messages, m => m.Severity == SHRuntimeMessageSeverity.Error);
            provider.Verify(p => p.Call(It.IsAny<AIRequestCall>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Fact]
        public async Task Exec_ValidOutputSchema_InvokesProviderExactlyOnce()
        {
            var provider = CreateProvider();
            var request = CreateRequest(provider.Object, "{\"type\":\"object\"}");
            Assert.True(request.IsValid().IsValid);

            var result = await new DefaultProviderExecutor().ExecProviderAsync(request, CancellationToken.None);

            Assert.NotNull(result);
            Assert.DoesNotContain(result!.Messages, m => m.Severity == SHRuntimeMessageSeverity.Error);
            provider.Verify(p => p.Call(request, CancellationToken.None), Times.Once);
        }

        [Fact]
        public async Task Exec_PolicyReceivesCancellationToken_AndCancellationPreventsProviderCall()
        {
            using var cancellation = new CancellationTokenSource();
            var policy = new CancellingPolicy(cancellation);
            var provider = CreateProvider();
            var request = CreateRequest(provider.Object, "{\"type\":\"object\"}");
            PolicyPipeline.Default.RequestPolicies.Add(policy);
            try
            {
                var result = await new DefaultProviderExecutor().ExecProviderAsync(request, cancellation.Token);

                Assert.Equal(cancellation.Token, policy.ReceivedToken);
                Assert.Contains(result!.Messages, m => m.Severity == SHRuntimeMessageSeverity.Error && m.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase));
                provider.Verify(p => p.Call(It.IsAny<AIRequestCall>(), It.IsAny<CancellationToken>()), Times.Never);
            }
            finally
            {
                PolicyPipeline.Default.RequestPolicies.Remove(policy);
            }
        }

        private static Mock<IAIProvider> CreateProvider()
        {
            var provider = new Mock<IAIProvider>(MockBehavior.Strict);
            provider.SetupGet(p => p.Name).Returns("schema-execution-test-provider");
            provider.Setup(p => p.Call(It.IsAny<AIRequestCall>(), It.IsAny<CancellationToken>()))
                .Returns((AIRequestCall request, CancellationToken token) =>
                {
                    var result = new AIReturn { Request = request };
                    result.SetBody(AIBodyBuilder.Create().AddText(AIAgent.Assistant, "{}", new AIMetrics
                    {
                        Provider = request.Provider,
                        Model = request.Model,
                        FinishReason = "stop",
                    }).Build());
                    return Task.FromResult<IAIReturn>(result);
                });
            return provider;
        }

        private static AIRequestCall CreateRequest(IAIProvider provider, string schema)
        {
            return new MockProviderRequest(provider)
            {
                Provider = provider.Name,
                Endpoint = "https://example.invalid/no-network",
                Body = AIBodyBuilder.Create()
                    .WithTurnId(Guid.NewGuid().ToString("N"))
                    .WithToolFilter("-*")
                    .WithContextFilter("-*")
                    .WithJsonOutputSchema(schema)
                    .AddText(AIAgent.User, "Return an object")
                    .Build(),
            };
        }

        private sealed class MockProviderRequest : AIRequestCall
        {
            private readonly IAIProvider provider;

            public MockProviderRequest(IAIProvider provider)
            {
                this.provider = provider;
            }

            public override IAIProvider ProviderInstance => this.provider;

            public override string Model { get; set; } = "schema-test-model";
        }

        private sealed class CancellingPolicy : IRequestPolicy
        {
            private readonly CancellationTokenSource cancellation;

            public CancellingPolicy(CancellationTokenSource cancellation)
            {
                this.cancellation = cancellation;
            }

            public CancellationToken ReceivedToken { get; private set; }

            public Task ApplyAsync(PolicyContext context)
            {
                this.ReceivedToken = context.CancellationToken;
                this.cancellation.Cancel();
                return Task.CompletedTask;
            }
        }
    }
}
#endif
