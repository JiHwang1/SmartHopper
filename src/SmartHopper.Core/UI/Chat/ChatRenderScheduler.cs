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
using System.Threading;
using System.Threading.Tasks;

namespace SmartHopper.Core.UI.Chat
{
    /// <summary>
    /// Serializes rendering and delivery, replacing pending work for the same DOM key.
    /// Capacity includes the active render; lossless submissions await space instead of being dropped.
    /// </summary>
    internal sealed class ChatRenderScheduler : IDisposable
    {
        private readonly object _gate = new object();
        private readonly SemaphoreSlim _capacity;
        private readonly LinkedList<Work> _pending = new LinkedList<Work>();
        private readonly Dictionary<string, Work> _latest = new Dictionary<string, Work>(StringComparer.Ordinal);
        private CancellationTokenSource _lifetime = new CancellationTokenSource();
        private Task _admissionTail = Task.CompletedTask;
        private bool _running;
        private bool _disposed;

        internal ChatRenderScheduler(int capacity = 64)
        {
            this._capacity = new SemaphoreSlim(capacity, capacity);
        }

        internal int PendingCount
        {
            get { lock (this._gate) { return this._pending.Count; } }
        }

        internal int TrackedKeyCount
        {
            get { lock (this._gate) { return this._latest.Count; } }
        }

        internal async Task EnqueueAsync(string? key, Func<Ticket, Task> render, bool discardIfFull = false)
        {
            Work work;
            Task precedingAdmission = Task.CompletedTask;
            TaskCompletionSource<bool>? admitted = null;
            CancellationToken lifetime;
            lock (this._gate)
            {
                if (this._disposed) return;
                lifetime = this._lifetime.Token;
                work = new Work(key, render, lifetime);
                if (!this.TryReplacePending(work))
                {
                    if (discardIfFull)
                    {
                        // Intermediate snapshots never accumulate capacity waiters. Completion
                        // and tool messages use the lossless path, including the final flush.
                        if (!this._admissionTail.IsCompleted || !this._capacity.Wait(0)) return;
                        this.AddPending(work);
                    }
                    else
                    {
                        // Preserve admission order as well as render order. Otherwise a flush
                        // could overtake a final message while both await capacity.
                        precedingAdmission = this._admissionTail;
                        admitted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        this._admissionTail = admitted.Task;
                    }
                }
            }

            if (admitted != null)
            {
                try
                {
                    await precedingAdmission.WaitAsync(lifetime).ConfigureAwait(false);
                    await this._capacity.WaitAsync(lifetime).ConfigureAwait(false);
                    lock (this._gate)
                    {
                        if (this._disposed || lifetime.IsCancellationRequested)
                        {
                            this._capacity.Release();
                            return;
                        }

                        if (this.TryReplacePending(work)) this._capacity.Release();
                        else this.AddPending(work);
                    }
                }
                finally
                {
                    admitted.TrySetResult(true);
                }
            }

            await work.Completed.Task.ConfigureAwait(false);
        }

        // Called under _gate. Only active/pending keys are retained, never the transcript.
        private void AddPending(Work work)
        {
            if (work.Key != null)
            {
                if (this._latest.TryGetValue(work.Key, out var previous)) previous.Ticket.Invalidate();
                this._latest[work.Key] = work;
            }

            work.Node = this._pending.AddLast(work);
            if (!this._running)
            {
                this._running = true;
                _ = Task.Run(this.DrainAsync);
            }
        }

        internal Task FlushAsync() => this.EnqueueAsync(null, _ => Task.CompletedTask);

        internal void Reset()
        {
            lock (this._gate)
            {
                if (this._disposed) return;
                this.CancelPending();
                this._lifetime = new CancellationTokenSource();
            }
        }

        public void Dispose()
        {
            lock (this._gate)
            {
                if (this._disposed) return;
                this._disposed = true;
                this.CancelPending();
            }
        }

        private bool TryReplacePending(Work work)
        {
            if (work.Key == null || !this._latest.TryGetValue(work.Key, out var previous) || previous.Node == null)
            {
                return false;
            }

            previous.Ticket.Invalidate();
            work.Node = previous.Node;
            work.Node.Value = work;
            previous.Node = null;
            this._latest[work.Key] = work;
            previous.Completed.TrySetResult(true);
            return true;
        }

        private void CancelPending()
        {
            this._lifetime.Cancel();
            this._lifetime.Dispose();
            foreach (var work in this._pending)
            {
                work.Ticket.Invalidate();
                work.Completed.TrySetCanceled();
                this._capacity.Release();
            }

            this._pending.Clear();
            this._latest.Clear();
            this._admissionTail = Task.CompletedTask;
        }

        private async Task DrainAsync()
        {
            while (true)
            {
                Work work;
                lock (this._gate)
                {
                    if (this._pending.First == null)
                    {
                        this._running = false;
                        return;
                    }

                    work = this._pending.First.Value;
                    this._pending.RemoveFirst();
                    work.Node = null;
                }

                try
                {
                    if (work.Ticket.IsCurrent)
                    {
                        var rendering = work.Render(work.Ticket);
                        try
                        {
                            await rendering.WaitAsync(work.Ticket.CancellationToken).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException)
                        {
                            // A delayed UI delivery can outlive reset/disposal. Observe any later
                            // failure even though cancellation lets the worker move on immediately.
                            _ = rendering.ContinueWith(
                                failed => { _ = failed.Exception; },
                                CancellationToken.None,
                                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                                TaskScheduler.Default);
                            throw;
                        }
                    }

                    work.Completed.TrySetResult(true);
                }
                catch (OperationCanceledException)
                {
                    work.Completed.TrySetCanceled();
                }
                catch (Exception ex)
                {
                    work.Completed.TrySetException(ex);
                }
                finally
                {
                    lock (this._gate)
                    {
                        if (work.Key != null && this._latest.TryGetValue(work.Key, out var latest) && ReferenceEquals(work, latest))
                        {
                            this._latest.Remove(work.Key);
                        }
                    }

                    this._capacity.Release();
                }
            }
        }

        internal sealed class Ticket
        {
            private int _invalidated;

            internal Ticket(CancellationToken cancellationToken) => this.CancellationToken = cancellationToken;

            internal CancellationToken CancellationToken { get; }

            internal bool IsCurrent => Volatile.Read(ref this._invalidated) == 0 && !this.CancellationToken.IsCancellationRequested;

            internal void Invalidate() => Interlocked.Exchange(ref this._invalidated, 1);
        }

        private sealed class Work
        {
            internal Work(string? key, Func<Ticket, Task> render, CancellationToken lifetime)
            {
                this.Key = key;
                this.Render = render;
                this.Ticket = new Ticket(lifetime);
            }

            internal string? Key { get; }

            internal Func<Ticket, Task> Render { get; }

            internal Ticket Ticket { get; }

            internal LinkedListNode<Work>? Node { get; set; }

            internal TaskCompletionSource<bool> Completed { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }
}
