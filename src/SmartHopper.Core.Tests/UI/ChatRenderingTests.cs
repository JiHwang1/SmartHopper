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
using System.Linq.Expressions;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SmartHopper.Core.ComponentBase.Batch;
using SmartHopper.Infrastructure.AICall.Core.Base;
using SmartHopper.Infrastructure.AICall.Core.Interactions;
using SmartHopper.Infrastructure.Diagnostics;
using Xunit;

namespace SmartHopper.Core.Tests.UI
{
    public class ChatRenderingTests
    {
        private static TaskCompletionSource<bool> Signal() => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        [Fact]
        public void InitialHtml_EmbedsSanitizerAndRestrictsWebViewCapabilities()
        {
            var rendererType = typeof(BatchSentinel).Assembly.GetType("SmartHopper.Core.UI.Chat.HtmlChatRenderer", true)!;
            var renderer = Activator.CreateInstance(rendererType)!;
            var html = (string)rendererType.GetMethod("GetInitialHtml")!.Invoke(renderer, null)!;

            Assert.Contains("Content-Security-Policy", html);
            Assert.Contains("default-src 'none'", html);
            Assert.Contains("script-src data: 'unsafe-inline'", html);
            Assert.Contains("style-src 'unsafe-inline'", html);
            Assert.Contains("connect-src 'none'", html);
            var match = Regex.Match(html, "src=\\\"data:application/javascript;base64,([^\\\"]+)\\\"");
            Assert.True(match.Success, "Embedded DOMPurify script URI was not substituted into the chat page.");
            var sanitizer = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(match.Groups[1].Value));
            Assert.Contains("DOMPurify 3.4.17", sanitizer);
            Assert.Contains("function sanitizeChatHtml", html);
            Assert.Contains("DOMPurify.sanitize", html);
            Assert.Equal(5, Regex.Matches(html, @"innerHTML\s*=\s*sanitizeChatHtml\(").Count);
        }

        [Fact]
        public void NavigationPolicy_AllowsOnlyChatBridgeAndInitialBlankDocument()
        {
            var policyType = typeof(BatchSentinel).Assembly.GetType("SmartHopper.Core.UI.Chat.ChatNavigationPolicy", true)!;
            var isAllowed = policyType.GetMethod("IsAllowed", BindingFlags.Static | BindingFlags.NonPublic)!;

            Assert.True((bool)isAllowed.Invoke(null, new object[] { new Uri("about:blank") })!);
            Assert.True((bool)isAllowed.Invoke(null, new object[] { new Uri("sh://event?type=send") })!);
            Assert.True((bool)isAllowed.Invoke(null, new object[] { new Uri("clipboard://copy?text=test") })!);
            Assert.False((bool)isAllowed.Invoke(null, new object[] { new Uri("https://example.com") })!);
            Assert.False((bool)isAllowed.Invoke(null, new object[] { new Uri("javascript:alert(1)") })!);
            Assert.False((bool)isAllowed.Invoke(null, new object[] { new Uri("file:///tmp/chat.html") })!);
        }

        [Fact]
        public async Task Burst_CoalescesBeforeRealMarkdownRender_AndPreservesToolsAndFinal()
        {
            using var scheduler = new Scheduler(8);
            var started = Signal();
            var release = Signal();
            var blocker = scheduler.Enqueue(null, async _ => { started.SetResult(true); await release.Task; });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var rendered = new List<string>();
            var renderer = new Renderer();
            var submissions = new List<Task>();
            int markdownRenders = 0;

            Task Render(string key, IAIInteraction interaction, bool streaming = false) => scheduler.Enqueue(key, _ =>
            {
                markdownRenders++;
                rendered.Add(renderer.Render(interaction));
                return Task.CompletedTask;
            }, streaming);

            for (int i = 0; i < 250; i++)
            {
                submissions.Add(Render("text:1", new AIInteractionText { Agent = AIAgent.Assistant, Content = $"**partial {i}**" }, true));
            }

            Assert.Equal(1, scheduler.PendingCount);
            submissions.Add(Render("text:1", new AIInteractionText { Agent = AIAgent.Assistant, Content = "**before-tool-final**" }));
            submissions.Add(Render("tool:call", new AIInteractionToolCall { Id = "1", Name = "example", Arguments = new JObject() }));
            submissions.Add(Render("tool:result", new AIInteractionToolResult { Id = "1", Name = "example", Result = JObject.Parse("{ 'result': 'tool-result-marker' }") }));
            submissions.Add(Render("text:2", new AIInteractionText { Agent = AIAgent.Assistant, Content = "**answer-final**" }));
            var flush = scheduler.Flush();
            Assert.False(flush.IsCompleted);
            release.SetResult(true);
            await Task.WhenAll(submissions).WaitAsync(TimeSpan.FromSeconds(10));
            await blocker;
            await flush;

            Assert.Equal(4, markdownRenders);
            Assert.Contains("<strong>before-tool-final</strong>", rendered[0]);
            Assert.Contains("example", rendered[1]);
            Assert.Contains("tool-result-marker", rendered[2]);
            Assert.Contains("<strong>answer-final</strong>", rendered[3]);
            Assert.Equal(0, scheduler.TrackedKeyCount);
        }

        [Fact]
        public async Task Saturation_SkipsOnlyIntermediateSnapshots_AndDeliversFinalBeforeFlush()
        {
            using var scheduler = new Scheduler(2);
            var started = Signal();
            var release = Signal();
            var blocker = scheduler.Enqueue(null, async _ => { started.SetResult(true); await release.Task; });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var delivered = new List<string>();
            Task Add(string key, string value, bool streaming = false) => scheduler.Enqueue(key, _ =>
            {
                delivered.Add(value);
                return Task.CompletedTask;
            }, streaming);

            var tool = Add("tool", "tool");
            await Add("answer", "intermediate", true);
            var final = Add("answer", "final");
            var flush = scheduler.Flush();
            Assert.Equal(1, scheduler.PendingCount);
            Assert.False(final.IsCompleted);
            Assert.False(flush.IsCompleted);
            release.SetResult(true);
            await Task.WhenAll(blocker, tool, final, flush).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "tool", "final" }, delivered);
            Assert.Equal(0, scheduler.TrackedKeyCount);
        }

        [Theory]
        [InlineData(false, false)]
        [InlineData(false, true)]
        [InlineData(true, false)]
        [InlineData(true, true)]
        public async Task Saturation_ThenCancelOrError_FlushesLatestAggregateBeforeMarkerAndFinish(bool cancel, bool previouslyVisible)
        {
            using var scheduler = new Scheduler(2);
            var started = Signal();
            var release = Signal();
            var renderer = new Renderer();
            var delivered = new List<string>();
            const string key = "turn:cancelled:assistant:1";
            var dom = new Dictionary<string, string>();
            if (previouslyVisible) dom[key] = "earlier content";
            var active = scheduler.Enqueue(null, async _ => { started.SetResult(true); await release.Task; });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var tool = scheduler.Enqueue("tool", _ => { delivered.Add("tool"); return Task.CompletedTask; });
            var latest = new AIInteractionText
            {
                Agent = AIAgent.Assistant,
                Content = "**latest cumulative content**",
                Reasoning = "latest reasoning",
            };

            // This attempt is already recorded by the observer's dirty check, but capacity rejects
            // its delivery. The production boundary flush must resubmit this very same snapshot.
            await scheduler.Enqueue(key, _ => throw new InvalidOperationException("Saturated delta must be skipped."), true);
            var submissions = new List<Task>();
            FlushStreams(new[] { new KeyValuePair<string, IAIInteraction>(key, latest) }, (streamKey, text) =>
            {
                submissions.Add(scheduler.Enqueue(streamKey, _ =>
                {
                    dom[streamKey] = renderer.Render(text);
                    delivered.Add("latest");
                    return Task.CompletedTask;
                }));
            });
            string marker = cancel ? "Cancelled." : "Provider failed.";
            var error = scheduler.Enqueue("error", _ =>
            {
                Assert.Contains("<strong>latest cumulative content</strong>", dom[key]);
                Assert.Contains(marker, renderer.Render(new AIInteractionRuntimeMessage { Severity = SHRuntimeMessageSeverity.Error, Content = marker }));
                delivered.Add(marker);
                return Task.CompletedTask;
            });
            var finish = scheduler.Enqueue(null, _ => { delivered.Add("finished"); return Task.CompletedTask; });
            Assert.False(finish.IsCompleted);
            release.SetResult(true);
            await Task.WhenAll(submissions).WaitAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(active, tool, error, finish).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(new[] { "tool", "latest", marker, "finished" }, delivered);
            Assert.Contains("latest reasoning", dom[key]);
        }

        private static void FlushStreams(IEnumerable<KeyValuePair<string, IAIInteraction>> streams, Action<string, AIInteractionText> submit)
        {
            var type = typeof(BatchSentinel).Assembly.GetType("SmartHopper.Core.UI.Chat.ChatStreamFlush", true)!;
            type.GetMethod("Flush", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object?[] { streams, submit, null });
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ResetOrDispose_InvalidatesDelayedUiDelivery_WithoutResurrectingReusedKey(bool dispose)
        {
            using var scheduler = new Scheduler(2);
            var started = Signal();
            var release = Signal();
            var oldDelivered = Signal();
            bool resurrected = false;
            var active = scheduler.Enqueue("same-key", async isCurrent =>
            {
                started.SetResult(true);
                await release.Task;
                resurrected = isCurrent();
                oldDelivered.SetResult(true);
                throw new InvalidOperationException("Delayed delivery failure must be observed after cancellation.");
            });
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var pending = scheduler.Enqueue("pending", _ => throw new InvalidOperationException("Must not render after reset."));
            if (dispose) scheduler.Dispose(); else scheduler.Reset();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => active.WaitAsync(TimeSpan.FromSeconds(5)));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            if (!dispose)
            {
                await scheduler.Enqueue("same-key", isCurrent =>
                {
                    Assert.True(isCurrent());
                    return Task.CompletedTask;
                }).WaitAsync(TimeSpan.FromSeconds(5));
            }

            release.SetResult(true);
            await oldDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(resurrected);
            Assert.Equal(0, scheduler.TrackedKeyCount);
        }

        [Fact]
        public async Task RenderFailure_DoesNotStopLaterMessagesOrLeakKeys()
        {
            using var scheduler = new Scheduler(2);
            await Assert.ThrowsAsync<InvalidOperationException>(() => scheduler.Enqueue("bad", _ => throw new InvalidOperationException("render failed")));
            bool delivered = false;
            await scheduler.Enqueue("good", _ => { delivered = true; return Task.CompletedTask; });
            await scheduler.Flush();
            Assert.True(delivered);
            Assert.Equal(0, scheduler.TrackedKeyCount);
        }

        [Theory]
        [InlineData(SHRuntimeMessageSeverity.Debug, true, false)]
        [InlineData(SHRuntimeMessageSeverity.Debug, false, false)]
        [InlineData(SHRuntimeMessageSeverity.Info, true, true)]
        [InlineData(SHRuntimeMessageSeverity.Warning, true, true)]
        [InlineData(SHRuntimeMessageSeverity.Error, true, true)]
        [InlineData(SHRuntimeMessageSeverity.Info, false, false)]
        [InlineData(SHRuntimeMessageSeverity.Warning, false, false)]
        [InlineData(SHRuntimeMessageSeverity.Error, false, false)]
        public void Renderer_FiltersOnlyHiddenDiagnosticsAtSharedBoundary(SHRuntimeMessageSeverity severity, bool surfaceable, bool visible)
        {
            var html = new Renderer().Render(new AIInteractionRuntimeMessage
            {
                Severity = severity,
                Surfaceable = surfaceable,
                Content = "diagnostic-marker",
            });
            Assert.Equal(visible, html.Contains("diagnostic-marker", StringComparison.Ordinal));
            if (!visible) Assert.Equal(string.Empty, html);
        }

        // Exercise the production internal classes without changing their public API, signing,
        // or loading the Rhino/Eto dialog. Reflection is confined to this test adapter.
        private sealed class Scheduler : IDisposable
        {
            private readonly Type _type = typeof(BatchSentinel).Assembly.GetType("SmartHopper.Core.UI.Chat.ChatRenderScheduler", true)!;
            private readonly object _instance;

            internal Scheduler(int capacity) => this._instance = Activator.CreateInstance(this._type, BindingFlags.Instance | BindingFlags.NonPublic, null, new object[] { capacity }, null)!;

            internal int PendingCount => (int)this._type.GetProperty("PendingCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this._instance)!;

            internal int TrackedKeyCount => (int)this._type.GetProperty("TrackedKeyCount", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(this._instance)!;

            internal Task Enqueue(string? key, Func<Func<bool>, Task> render, bool streaming = false)
            {
                var method = this._type.GetMethod("EnqueueAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
                var callbackType = method.GetParameters()[1].ParameterType;
                var ticketType = callbackType.GenericTypeArguments[0];
                var current = ticketType.GetProperty("IsCurrent", BindingFlags.Instance | BindingFlags.NonPublic)!;
                Func<object, Task> adapter = ticket => render(() => (bool)current.GetValue(ticket)!);
                var parameter = Expression.Parameter(ticketType);
                var callback = Expression.Lambda(callbackType, Expression.Invoke(Expression.Constant(adapter), Expression.Convert(parameter, typeof(object))), parameter).Compile();
                return (Task)method.Invoke(this._instance, new object?[] { key, callback, streaming })!;
            }

            internal Task Flush() => (Task)this._type.GetMethod("FlushAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this._instance, null)!;

            internal void Reset() => this._type.GetMethod("Reset", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(this._instance, null);

            public void Dispose() => ((IDisposable)this._instance).Dispose();
        }

        private sealed class Renderer
        {
            private readonly Type _type = typeof(BatchSentinel).Assembly.GetType("SmartHopper.Core.UI.Chat.HtmlChatRenderer", true)!;
            private readonly object _instance;

            internal Renderer() => this._instance = Activator.CreateInstance(this._type)!;

            internal string Render(IAIInteraction interaction) => (string)this._type.GetMethod("RenderInteraction")!.Invoke(this._instance, new object[] { interaction })!;
        }
    }
}
