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
using System.Net.Http;

namespace SmartHopper.Infrastructure.Utils
{
    /// <summary>
    /// Reuses provider connections while keeping headers and timeouts local to each client.
    /// </summary>
    internal static class ProviderHttpClient
    {
        // The handler lives for the process; connections are periodically renewed for DNS changes.
        private static readonly SocketsHttpHandler SharedHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
        };

        /// <summary>
        /// Creates a client that the caller must dispose without disposing the shared pool.
        /// </summary>
        /// <returns>A new client with independent headers, timeout, and cancellation state.</returns>
        internal static HttpClient Create()
        {
            return new HttpClient(SharedHandler, disposeHandler: false);
        }
    }
}
