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
using System.Threading.Tasks;
using Newtonsoft.Json;
using SmartHopper.Infrastructure.AICall.Core.Interactions;

namespace SmartHopper.Core.UI.Chat
{
    internal partial class WebChatDialog
    {
        private async Task QueueInteractionRenderAsync(string? domKey, IAIInteraction interaction, string? followKey = null, bool isStreaming = false)
        {
            // The observer mutates its cumulative aggregate on the UI thread. Freeze text before
            // handing it to the renderer so a delayed render cannot observe a later segment/final.
            if (interaction is AIInteractionText text)
            {
                interaction = new AIInteractionText
                {
                    Agent = text.Agent,
                    TurnId = text.TurnId,
                    Content = text.Content,
                    Reasoning = text.Reasoning,
                    Time = text.Time,
                    Metrics = text.Metrics,
                };
            }

            try
            {
                await this._renderScheduler.EnqueueAsync(domKey, async ticket =>
                {
                    if (!ticket.IsCurrent) return;
                    string html;
                    lock (this._htmlRenderLock)
                    {
                        if (!ticket.IsCurrent) return;
                        html = this._htmlRenderer.RenderInteraction(interaction);
                    }

                    if (!ticket.IsCurrent || string.IsNullOrEmpty(html)) return;
                    var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    this.RunWhenWebViewReady(() =>
                    {
                        try
                        {
                            if (!ticket.IsCurrent || this._isClosing) return;
                            if (domKey != null && this._lastDomHtmlByKey.TryGetValue(domKey, out var previous) &&
                                string.Equals(previous, html, StringComparison.Ordinal)) return;

                            var payload = JsonConvert.SerializeObject(html);
                            var key = JsonConvert.SerializeObject(domKey);
                            var script = domKey == null
                                ? $"addMessage({payload});"
                                : followKey == null
                                    ? $"upsertMessage({key}, {payload});"
                                    : $"upsertMessageAfter({JsonConvert.SerializeObject(followKey)}, {key}, {payload});";

                            // The DOM drain already runs on the Rhino UI thread, outside navigation
                            // events. Acknowledge the actual script delivery before rendering the next message.
                            this._webView.ExecuteScript(script);
                            if (domKey != null) this.UpdateIdempotencyCache(domKey, html);
                        }
                        catch (Exception ex)
                        {
                            DebugLog($"[WebChatDialog] Render delivery error: {ex.Message}");
                        }
                        finally
                        {
                            delivered.TrySetResult(true);
                        }
                    });
                    await delivered.Task.WaitAsync(ticket.CancellationToken).ConfigureAwait(false);
                }, discardIfFull: isStreaming).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Closing or resetting the view invalidates pending renders.
            }
            catch (Exception ex)
            {
                DebugLog($"[WebChatDialog] Render error: {ex.Message}");
            }
        }

        private async Task FinishRenderingAsync(Action finish)
        {
            try
            {
                await this._renderScheduler.EnqueueAsync(null, async ticket =>
                {
                    var delivered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    this.RunWhenWebViewReady(() =>
                    {
                        try
                        {
                            if (ticket.IsCurrent && !this._isClosing) finish();
                        }
                        finally
                        {
                            delivered.TrySetResult(true);
                        }
                    });
                    await delivered.Task.WaitAsync(ticket.CancellationToken).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }
    }
}
