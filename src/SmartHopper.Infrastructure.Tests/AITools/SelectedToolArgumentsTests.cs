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

using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using SmartHopper.Infrastructure.AITools;
using Xunit;

namespace SmartHopper.Infrastructure.Tests.AITools
{
    public class SelectedToolArgumentsTests
    {
        [Fact]
        public void GetGuids_PrefersFrozenGuidsWithoutReadingLiveSelection()
        {
            var liveSelectionRead = false;

            var guids = SelectedToolArguments.GetGuids(
                JObject.Parse("{\"guids\":[\"frozen-guid\"]}"),
                () =>
                {
                    liveSelectionRead = true;
                    return new List<string> { "current-guid" };
                });

            Assert.Equal(new[] { "frozen-guid" }, guids);
            Assert.False(liveSelectionRead);
        }

        [Fact]
        public void GetGuids_HonorsExplicitEmptyFrozenSelection()
        {
            var liveSelectionRead = false;

            var guids = SelectedToolArguments.GetGuids(
                JObject.Parse("{\"guids\":[]}"),
                () =>
                {
                    liveSelectionRead = true;
                    return new List<string> { "current-guid" };
                });

            Assert.Empty(guids);
            Assert.False(liveSelectionRead);
        }

        [Fact]
        public void GetGuids_ReadsLiveSelectionWhenNoFrozenArgumentExists()
        {
            var guids = SelectedToolArguments.GetGuids(
                new JObject(),
                () => new List<string> { "current-guid" });

            Assert.Equal(new[] { "current-guid" }, guids);
        }
    }
}
