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
    using System;
    using System.Collections.Generic;
    using SmartHopper.Infrastructure.AICall.Core.Interactions;
    using Xunit;

    public class AIBodySnapshotTests
    {
        [Fact]
        public void Empty_NewnessCannotLeakBetweenRequests()
        {
            var first = AIBody.Empty;
            first.InteractionsNew.Add(42);

            Assert.Empty(AIBody.Empty.InteractionsNew);
        }

        [Fact]
        public void Build_ContainerCannotBeChangedByAnotherConsumer()
        {
            var interaction = new AIInteractionText { Content = "Original message" };
            var builder = AIBodyBuilder.Create().Add(interaction);
            var snapshot = builder.Build();
            builder.Add(new AIInteractionText { Content = "Later message" });

            Assert.Single(snapshot.Interactions);
            Assert.Throws<NotSupportedException>(() =>
                ((IList<IAIInteraction>)snapshot.Interactions).Clear());
            Assert.Same(interaction, snapshot.Interactions[0]);
        }
    }
}
