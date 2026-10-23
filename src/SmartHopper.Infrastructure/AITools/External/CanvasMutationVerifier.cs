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

namespace SmartHopper.Infrastructure.AITools.External
{
    using System;
    using System.Collections.Generic;
    using System.Globalization;
    using System.Linq;
    using Newtonsoft.Json.Linq;

    /// <summary>
    /// Verifies reported canvas tool outcomes using JSON snapshots captured by the caller.
    /// </summary>
    public static class CanvasMutationVerifier
    {
        private const double PositionTolerance = 0.01;

        /// <summary>
        /// Verifies a tool payload against the requested operation and before/after snapshots.
        /// </summary>
        public static (bool IsValid, string Status, string Message) Verify(
            string toolName,
            JObject arguments,
            JObject payload,
            JObject before,
            JObject after)
        {
            if (string.IsNullOrWhiteSpace(toolName) || arguments == null || payload == null || before == null || after == null)
            {
                return Fail("Tool name, arguments, payload, and both snapshots are required.");
            }

            var protectedGuids = ReadStrings(Get(payload, "protectedGuids")).ToList();
            if (protectedGuids.Count > 0)
            {
                return Fail($"Partial failure: protected objects were not mutated ({string.Join(", ", protectedGuids)}).");
            }

            if (Bool(payload, "success") == false || Bool(payload, "putSuccess") == false || Number(payload, "failCount") > 0 || HasItems(Get(payload, "failed")))
            {
                return Fail("The tool payload reports a failed or partial operation.");
            }

            var name = toolName.Trim().ToLowerInvariant();
            if (name == "gh_move") return VerifyMove(arguments, payload, before, after);
            if (name.Contains("toggle_lock") || name.Contains("lock_selected") || name.Contains("unlock_selected")) return VerifyLock(name, arguments, before, after);
            if (name.Contains("toggle_preview") || name.Contains("preview_selected")) return VerifyPreview(name, arguments, before, after);
            if (name == "gh_connect" || name == "gh_disconnect") return VerifyConnections(name == "gh_connect", arguments, before, after);
            if (name == "gh_group" || name == "gh_group_selected") return VerifyGroup(arguments, payload, before, after);
            if (name == "gh_put") return VerifyPut(payload, before, after);
            if (name == "gh_tidy_up" || name == "gh_tidy_up_selected") return VerifyTidy(payload, before, after);
            if (name.Contains("script") || name.Contains("parameter")) return VerifySerializedChange(arguments, before, after);

            return Fail($"No canvas mutation verifier is defined for '{toolName}'.");
        }

        private static (bool, string, string) VerifyMove(JObject args, JObject payload, JObject before, JObject after)
        {
            var targets = Get(args, "targets") as JObject;
            if (targets == null || !targets.Properties().Any()) return Fail("No move targets were provided.");
            var relative = Bool(args, "relative") ?? false;
            var updated = ReadStrings(Get(payload, "updated")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var changed = false;

            foreach (var target in targets.Properties())
            {
                var id = target.Name;
                var oldState = Find(before, id);
                var newState = Find(after, id);
                if (oldState == null || newState == null || !TryPosition(target.Value as JObject, out var x, out var y) || !TryPosition(newState, out var actualX, out var actualY))
                {
                    return Fail($"Move target '{id}' is missing or has no verifiable coordinates.");
                }

                if (relative)
                {
                    if (!TryPosition(oldState, out var oldX, out var oldY)) return Fail($"Move target '{id}' has no before position.");
                    x += oldX;
                    y += oldY;
                }

                if (!Near(actualX, x) || !Near(actualY, y)) return Fail($"Move target '{id}' did not reach the requested position.");
                var moved = !TryPosition(oldState, out var beforeX, out var beforeY) || !Near(beforeX, actualX) || !Near(beforeY, actualY);
                if (moved && !updated.Contains(id)) return Fail($"Move target '{id}' changed position but is missing from payload.updated.");
                changed |= moved;
            }

            return changed ? Ok("Succeeded", "Every requested object reached its target position and is reported as updated.")
                : Ok("AlreadySatisfied", "Every requested object was already at its target position.");
        }

        private static (bool, string, string) VerifyLock(string name, JObject args, JObject before, JObject after)
        {
            var desired = name.Contains("unlock") ? false : name.Contains("lock_selected") ? true : Bool(args, "locked") ?? false;
            var ids = name.Contains("selected")
                ? SelectedIds(before)
                : ReadStrings(Get(args, "guids")).ToList();
            return VerifyFlag(ids, "locked", desired, before, after, "lock");
        }

        private static (bool, string, string) VerifyPreview(string name, JObject args, JObject before, JObject after)
        {
            var previewOn = name.Contains("hide_preview") ? false
                : name.Contains("show_preview") ? true
                : Bool(args, "previewOn") ?? false;
            var ids = name.Contains("selected")
                ? SelectedIds(before)
                : ReadStrings(Get(args, "guids")).ToList();
            return VerifyFlag(ids, "hidden", !previewOn, before, after, "preview");
        }

        private static (bool, string, string) VerifyFlag(List<string> ids, string field, bool desired, JObject before, JObject after, string label)
        {
            if (ids.Count == 0) return Fail($"No objects were targeted for the {label} change.");
            var already = true;
            foreach (var id in ids)
            {
                var oldState = Find(before, id);
                var newState = Find(after, id);
                if (newState == null || Bool(newState, field) != desired) return Fail($"Object '{id}' does not have the requested {label} state.");
                already &= Bool(oldState, field) == desired;
            }

            return already ? Ok("AlreadySatisfied", $"All targeted objects already had the requested {label} state.")
                : Ok("Succeeded", $"The requested {label} state is present on every targeted object.");
        }

        private static (bool, string, string) VerifyConnections(bool connect, JObject args, JObject before, JObject after)
        {
            var connections = Get(args, "connections") as JArray;
            if (connections == null || connections.Count == 0 || connections.Any(item => !(item is JObject)))
            {
                return Fail("Connections must be a non-empty array of connection objects.");
            }

            var already = true;
            foreach (var item in connections.Cast<JObject>())
            {
                var sourceId = Text(item, "sourceGuid");
                var targetId = Text(item, "targetGuid");
                var sourceBefore = Find(before, sourceId);
                var targetBefore = Find(before, targetId);
                var sourceAfter = Find(after, sourceId);
                var targetAfter = Find(after, targetId);
                var outputBefore = ResolvePort(Get(sourceBefore, "outputs") as JArray, Text(item, "sourceParam"));
                var inputBefore = ResolvePort(Get(targetBefore, "inputs") as JArray, Text(item, "targetParam"));
                var outputAfter = ResolvePort(Get(sourceAfter, "outputs") as JArray, Text(item, "sourceParam"));
                var inputAfter = ResolvePort(Get(targetAfter, "inputs") as JArray, Text(item, "targetParam"));
                if (sourceBefore == null || targetBefore == null || sourceAfter == null || targetAfter == null ||
                    outputBefore == null || inputBefore == null || outputAfter == null || inputAfter == null)
                {
                    return Fail("A requested connection endpoint or parameter could not be resolved in the snapshots.");
                }

                var sourceParam = Text(outputAfter, "guid");
                var sources = ReadStrings(Get(inputAfter, "sources"));
                var connectedAfter = sources.Contains(sourceParam, StringComparer.OrdinalIgnoreCase);
                var connectedBefore = ReadStrings(Get(inputBefore, "sources"))
                    .Contains(Text(outputBefore, "guid"), StringComparer.OrdinalIgnoreCase);
                if (connectedAfter != connect) return Fail("The requested connection state is absent on the specified input port.");
                already &= connectedBefore == connect;
            }

            return already ? Ok("AlreadySatisfied", "Every requested connection state was already satisfied.")
                : Ok("Succeeded", "Every requested connection state is present on the specified input port.");
        }

        private static (bool, string, string) VerifyGroup(JObject args, JObject payload, JObject before, JObject after)
        {
            var groupId = Text(payload, "group") ?? Text(payload, "groupGuid");
            var group = Find(after, groupId);
            if (string.IsNullOrWhiteSpace(groupId) || group == null) return Fail("The reported group does not exist in the after snapshot.");
            if (Find(before, groupId) != null) return Fail("The reported group already existed before group creation.");
            var members = ReadStrings(Get(group, "members"));
            var requested = args["guids"] != null
                ? ReadStrings(Get(args, "guids")).ToList()
                : SelectedIds(before);
            if (requested.Count == 0 || requested.Any(id => !members.Contains(id, StringComparer.OrdinalIgnoreCase)))
            {
                return Fail("The reported group does not contain every requested or selected object.");
            }

            return Ok("Succeeded", "The reported group exists and contains every requested object.");
        }

        private static (bool, string, string) VerifyPut(JObject payload, JObject before, JObject after)
        {
            var ids = ReadStrings(Get(payload, "instanceGuids")).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ids.Count == 0) return Fail("gh_put returned no instanceGuids to verify.");
            var existing = ids.Where(id => Find(before, id) != null).ToList();
            if (existing.Count > 0) return Fail($"Reported gh_put instances already existed before placement: {string.Join(", ", existing)}.");
            var missing = ids.Where(id => Find(after, id) == null).ToList();
            return missing.Count > 0 ? Fail($"Reported gh_put instances are absent from the after snapshot: {string.Join(", ", missing)}.")
                : Ok("ObservedChange", "Every reported gh_put instance exists in the after snapshot; content and placement were not verified.");
        }

        private static (bool, string, string) VerifyTidy(JObject payload, JObject before, JObject after)
        {
            var targets = Get(payload, "targets") as JObject;
            if (targets != null)
            {
                var moveArguments = new JObject
                {
                    ["targets"] = (JObject)targets.DeepClone(),
                    ["relative"] = false,
                };
                var movePayload = new JObject
                {
                    ["updated"] = (Get(payload, "moved") ?? Get(payload, "affectedGuids") ?? new JArray()).DeepClone(),
                };
                return VerifyMove(moveArguments, movePayload, before, after);
            }

            var ids = ReadStrings(Get(payload, "moved")).ToList();
            if (ids.Count == 0) ids = ReadStrings(Get(payload, "affectedGuids")).ToList();
            if (ids.Count == 0) return Fail("gh_tidy_up reported no moved objects to verify.");
            var moved = ids.Where(id =>
            {
                var oldState = Find(before, id);
                var newState = Find(after, id);
                return oldState != null && newState != null && TryPosition(oldState, out var ox, out var oy) &&
                    TryPosition(newState, out var nx, out var ny) && (!Near(ox, nx) || !Near(oy, ny));
            }).ToList();
            return moved.Count == ids.Count ? Ok("ObservedChange", $"Verified position changes for reported objects: {string.Join(", ", moved)}.")
                : Fail("gh_tidy_up reported no object with a verified position change.");
        }

        private static (bool, string, string) VerifySerializedChange(JObject args, JObject before, JObject after)
        {
            var ids = new[] { "scriptGuid", "componentGuid", "instanceGuid", "guid" }
                .Select(key => Text(args, key)).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (ids.Count == 0) ids = ReadStrings(Get(args, "guids")).ToList();
            if (ids.Count == 0) return Fail("No target object GUID was provided for serialized-content verification.");
            foreach (var id in ids)
            {
                var oldValue = Text(Find(before, id), "serialized");
                var newValue = Text(Find(after, id), "serialized");
                if (string.IsNullOrEmpty(oldValue) || string.IsNullOrEmpty(newValue) || string.Equals(oldValue, newValue, StringComparison.Ordinal))
                {
                    return Fail($"Serialized content for target '{id}' is missing or unchanged.");
                }
            }

            return Ok("ObservedChange", "Serialized content changed for every targeted object.");
        }

        private static List<string> SelectedIds(JObject snapshot) => snapshot.Properties()
            .Where(property => Bool(property.Value as JObject, "selected") == true)
            .Select(property => property.Name).ToList();

        private static JObject Find(JObject snapshot, string id) => string.IsNullOrWhiteSpace(id)
            ? null
            : snapshot.Properties().FirstOrDefault(property => string.Equals(property.Name, id, StringComparison.OrdinalIgnoreCase))?.Value as JObject;

        private static JObject ResolvePort(JArray ports, string name)
        {
            if (ports == null || ports.Count == 0) return null;
            if (string.IsNullOrWhiteSpace(name)) return ports[0] as JObject;
            return ports.OfType<JObject>().FirstOrDefault(port => string.Equals(Text(port, "name"), name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Text(port, "nickname"), name, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryPosition(JObject state, out double x, out double y)
        {
            var xToken = Get(state, "x");
            var yToken = Get(state, "y");
            x = 0;
            y = 0;
            return IsFiniteNumber(xToken, out x) && IsFiniteNumber(yToken, out y);
        }

        private static bool IsFiniteNumber(JToken token, out double value)
        {
            value = 0;
            return token != null && (token.Type == JTokenType.Integer || token.Type == JTokenType.Float) &&
                double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
                !double.IsNaN(value) && !double.IsInfinity(value);
        }

        private static bool Near(double left, double right) => Math.Abs(left - right) <= PositionTolerance;

        private static JToken Get(JObject value, string key) => value?.GetValue(key, StringComparison.OrdinalIgnoreCase);

        private static string Text(JObject value, string key) => Get(value, key)?.Type == JTokenType.Null ? null : Get(value, key)?.ToString();

        private static bool? Bool(JObject value, string key)
        {
            var token = Get(value, key);
            return token == null || token.Type == JTokenType.Null ? (bool?)null : token.Value<bool>();
        }

        private static double Number(JObject value, string key)
        {
            var token = Get(value, key);
            return token != null && double.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : 0;
        }

        private static IEnumerable<string> ReadStrings(JToken token) => token is JArray array
            ? array.Where(item => item != null && item.Type != JTokenType.Null).Select(item => item.ToString())
            : Enumerable.Empty<string>();

        private static bool HasItems(JToken token) => token is JArray array && array.Count > 0 ||
            token is JObject obj && obj.Properties().Any() || token != null && token.Type == JTokenType.Boolean && token.Value<bool>();

        private static (bool, string, string) Fail(string message) => (false, "Failed", message);

        private static (bool, string, string) Ok(string status, string message) => (true, status, message);
    }
}
