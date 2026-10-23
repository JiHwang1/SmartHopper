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
using SmartHopper.Infrastructure.Diagnostics;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.AICall
{
    public class ProviderTrustWarningDeduplicatorTests
    {
        [Fact]
        public void OnlyIdenticalTrustWarningsAreSuppressed_WhileIntegrityErrorsRemainVisible()
        {
            var provider = Guid.NewGuid().ToString("N");
            var text = $"Provider '{provider}' failed SHA-256 integrity verification.";
            var warning = Message(SHRuntimeMessageSeverity.Warning, text);
            Assert.True(ProviderTrustWarningDeduplicator.ShouldSurface(warning));
            Assert.False(ProviderTrustWarningDeduplicator.ShouldSurface(warning));
            Assert.True(ProviderTrustWarningDeduplicator.ShouldSurface(Message(SHRuntimeMessageSeverity.Warning, text + " Hash repository unavailable.")));

            var error = Message(SHRuntimeMessageSeverity.Error, text);
            Assert.True(ProviderTrustWarningDeduplicator.ShouldSurface(error));
            Assert.True(ProviderTrustWarningDeduplicator.ShouldSurface(error));
        }

        [Theory]
        [InlineData(SHMessageCode.UnknownProvider, SHRuntimeMessageOrigin.Validation)]
        [InlineData(SHMessageCode.ProviderTrustWarning, SHRuntimeMessageOrigin.Provider)]
        public void OtherWarningsAreNeverSuppressed(SHMessageCode code, SHRuntimeMessageOrigin origin)
        {
            var message = new SHRuntimeMessage(SHRuntimeMessageSeverity.Warning, origin, code, "same text");
            Assert.True(ProviderTrustWarningDeduplicator.ShouldSurface(message));
            Assert.True(ProviderTrustWarningDeduplicator.ShouldSurface(message));
        }

        private static SHRuntimeMessage Message(SHRuntimeMessageSeverity severity, string text) => new (
            severity, SHRuntimeMessageOrigin.Validation, SHMessageCode.ProviderTrustWarning, text);
    }
}
