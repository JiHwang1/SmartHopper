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
using System.Reflection;
using GhJSON.Core.SchemaModels;
using Newtonsoft.Json.Linq;
using SmartHopper.Core.Grasshopper.AITools;
using Xunit;

namespace SmartHopper.Core.Grasshopper.Tests.AITools
{
    /// <summary>Exercises the native-free input boundary used before Canvas access.</summary>
    public class GhPutDocumentTests
    {
        [Theory]
        [InlineData("{broken")]
        [InlineData("null")]
        [InlineData("{\"components\":[]}")]
        public void InvalidDocument_IsRejectedBeforeNativeAccess(string json)
        {
            var error = Assert.Throws<TargetInvocationException>(() => Parse(json));
            Assert.NotNull(error.InnerException);
        }

        [Fact]
        public void ExplicitInstanceIdentity_IsRetained_AndMissingIdentityIsAssigned()
        {
            var id = Guid.NewGuid();
            var json = new JObject
            {
                ["components"] = new JArray(
                    new JObject { ["id"] = 1, ["name"] = "Panel", ["instanceGuid"] = id.ToString() },
                    new JObject { ["id"] = 2, ["name"] = "Panel" }),
            };
            var document = Parse(json.ToString());
            Assert.Equal(id, document.Components[0].InstanceGuid);
            Assert.True(document.Components[1].InstanceGuid.HasValue);
            Assert.NotEqual(Guid.Empty, document.Components[1].InstanceGuid.Value);
            Assert.NotEqual(id, document.Components[1].InstanceGuid.Value);
        }

        [Fact]
        public void DuplicateExplicitIdentity_IsRejectedRatherThanRepairedToAnotherObject()
        {
            var id = Guid.NewGuid().ToString();
            var json = new JObject
            {
                ["components"] = new JArray(
                    new JObject { ["id"] = 1, ["name"] = "Panel", ["instanceGuid"] = id },
                    new JObject { ["id"] = 2, ["name"] = "Panel", ["instanceGuid"] = id }),
            };
            var error = Assert.Throws<TargetInvocationException>(() => Parse(json.ToString()));
            Assert.Contains("duplicate instanceGuid", error.InnerException.Message);
        }

        private static GhJsonDocument Parse(string json) => (GhJsonDocument)typeof(gh_put)
            .GetMethod("ParseDocument", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { json })!;
    }
}
