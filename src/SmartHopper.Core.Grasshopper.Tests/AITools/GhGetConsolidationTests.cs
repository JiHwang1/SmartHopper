using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using SmartHopper.Core.Grasshopper.AITools;
using Xunit;

namespace SmartHopper.Core.Grasshopper.Tests.AITools
{
    public class GhGetConsolidationTests
    {
        private static readonly string[] CanonicalNames =
        {
            "gh_get",
            "gh_get_selected",
            "gh_get_by_guid",
            "gh_get_errors",
        };

        private static readonly string[] AliasNames =
        {
            "gh_get_selected_with_data",
            "gh_get_by_guid_with_data",
            "gh_get_errors_with_data",
            "gh_get_locked",
            "gh_get_preview_off",
            "gh_get_preview_on",
            "gh_get_visible",
            "gh_get_start",
            "gh_get_start_with_data",
            "gh_get_end",
            "gh_get_end_with_data",
        };

        [Fact]
        public void GetTools_AdvertisesFourCanonicalToolsAndKeepsAliasesRegistered()
        {
            var tools = new gh_get().GetTools().ToList();

            Assert.Equal(CanonicalNames, tools.Where(tool => tool.Advertise).Select(tool => tool.Name));
            Assert.Equal(AliasNames, tools.Where(tool => !tool.Advertise).Select(tool => tool.Name));
            Assert.All(tools.Where(tool => !tool.Advertise), tool => Assert.True(tool.Enabled));
            Assert.Equal(CanonicalNames.Concat(AliasNames).OrderBy(name => name, StringComparer.Ordinal),
                tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal));
        }

        [Fact]
        public void CanonicalTools_ShareBoundedQuerySurface()
        {
            var tools = new gh_get().GetTools().Where(tool => tool.Advertise).ToDictionary(tool => tool.Name);

            foreach (var name in CanonicalNames)
            {
                var properties = (JObject)JObject.Parse(tools[name].ParametersSchema)["properties"]!;
                Assert.Equal(100, (int?)properties["pageSize"]?["maximum"]);
                Assert.Equal(25, (int?)properties["pageSize"]?["default"]);
                Assert.NotNull(properties["includeRuntimeData"]);
                Assert.NotNull(properties["includeInternalizedData"]);
                Assert.NotNull(properties["guidFilter"]);
            }
        }

        [Fact]
        public void GuidTools_RequireEitherCanonicalOrLegacyGuidParameter()
        {
            var tools = new gh_get().GetTools().ToDictionary(tool => tool.Name);

            foreach (var name in new[] { "gh_get_by_guid", "gh_get_by_guid_with_data" })
            {
                var requirements = (JArray?)JObject.Parse(tools[name].ParametersSchema)["anyOf"];
                Assert.NotNull(requirements);
                Assert.Contains(requirements!, item => (string?)item["required"]?[0] == "instanceGuids");
                Assert.Contains(requirements!, item => (string?)item["required"]?[0] == "guidFilter");
            }
        }
    }
}
