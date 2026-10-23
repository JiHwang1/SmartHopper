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

using System.Collections.Concurrent;

namespace SmartHopper.Infrastructure.Diagnostics
{
    /// <summary>
    /// Suppresses repeated, identical provider trust warnings at the display boundary only.
    /// Validation and error diagnostics remain unchanged. State lasts for the host process.
    /// </summary>
    public static class ProviderTrustWarningDeduplicator
    {
        private static readonly ConcurrentDictionary<string, byte> SurfacedWarnings = new (System.StringComparer.Ordinal);

        public static bool ShouldSurface(SHRuntimeMessage message)
        {
            if (message == null || message.Severity != SHRuntimeMessageSeverity.Warning
                || message.Origin != SHRuntimeMessageOrigin.Validation
                || message.Code != SHMessageCode.ProviderTrustWarning || !message.Surfaceable)
            {
                return true;
            }

            // The complete text includes provider identity and the specific integrity condition.
            return SurfacedWarnings.TryAdd(message.Message, 0);
        }
    }
}
