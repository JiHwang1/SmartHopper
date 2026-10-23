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

using Newtonsoft.Json.Linq;
using SmartHopper.Core.Parsing;
using Xunit;

namespace SmartHopper.Core.Tests.Parsing
{
    public class BooleanToolResultParserTests
    {
        [Fact]
        public void Parse_NullResult_UsesEmptyValueAndFallbackDefault()
        {
            var parsed = BooleanToolResultParser.Parse(null);

            Assert.Equal(string.Empty, parsed.Value);
            Assert.True(parsed.UsedFallback);
            Assert.False(parsed.IsBatchSentinel);
        }

        [Fact]
        public void Parse_NormalResult_UsesToolValues()
        {
            var result = JObject.Parse("{ 'result': true, 'usedFallback': false }");
            var parsed = BooleanToolResultParser.Parse(result);

            Assert.Equal(result["result"].ToString(), parsed.Value);
            Assert.False(parsed.UsedFallback);
            Assert.False(parsed.IsBatchSentinel);
        }

        [Fact]
        public void Parse_MissingOrInvalidFallbackFlag_DefaultsToTrue()
        {
            var missing = BooleanToolResultParser.Parse(JObject.Parse("{ 'result': false }"));
            var invalid = BooleanToolResultParser.Parse(JObject.Parse("{ 'result': false, 'usedFallback': 'unknown' }"));

            Assert.True(missing.UsedFallback);
            Assert.True(invalid.UsedFallback);
        }

        [Fact]
        public void Parse_BatchSentinel_PreservesSentinelForBothComponentOutputs()
        {
            const string sentinel = "##SH_BATCH:item-1##";
            var parsed = BooleanToolResultParser.Parse(new JObject
            {
                ["result"] = sentinel,
                ["usedFallback"] = false,
            });

            Assert.Equal(sentinel, parsed.Value);
            Assert.True(parsed.IsBatchSentinel);
            Assert.False(parsed.UsedFallback);
        }
    }
}
