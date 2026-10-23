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
 * You should have received a copy of the GNU Lesser General Public
 * License along with this program. If not, see <https://www.gnu.org/licenses/lgpl-3.0.html>.
 */

namespace SmartHopper.Infrastructure.AICall.Fallback
{
    /// <summary>
    /// A modality conversion step bound to the provider and model resolved for it.
    /// Steps in one chain may use different providers and models.
    /// </summary>
    public sealed class FallbackStep
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="FallbackStep"/> class.
        /// </summary>
        /// <param name="fallback">The conversion implementation.</param>
        /// <param name="provider">The provider that executes the conversion.</param>
        /// <param name="model">The model that executes the conversion.</param>
        public FallbackStep(IModalityFallback fallback, string provider, string model)
        {
            this.Fallback = fallback;
            this.Provider = provider;
            this.Model = model;
        }

        /// <summary>Gets the conversion implementation.</summary>
        public IModalityFallback Fallback { get; }

        /// <summary>Gets the provider that executes this conversion.</summary>
        public string Provider { get; }

        /// <summary>Gets the model that executes this conversion.</summary>
        public string Model { get; }
    }
}
