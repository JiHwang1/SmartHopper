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

namespace SmartHopper.Infrastructure.Tests.AITools
{
    using System;
    using System.Threading;
    using System.Threading.Tasks;
    using Newtonsoft.Json.Linq;
    using SmartHopper.Infrastructure.AITools.External;
    using SmartHopper.Infrastructure.Authentication.Codex;
    using Xunit;

    public class CanvasMutationVerifierTests
    {
        [Fact]
        public void Move_RejectsWrongPositionEvenWhenPayloadReportsUpdated()
        {
            var id = "component-1";
            var result = Verify("gh_move",
                new JObject { ["targets"] = new JObject { [id] = new JObject { ["x"] = 100, ["y"] = 200 } } },
                new JObject { ["updated"] = new JArray(id) },
                Snapshot(id, new JObject { ["x"] = 0, ["y"] = 0 }),
                Snapshot(id, new JObject { ["x"] = 90, ["y"] = 200 }));

            Assert.False(result.IsValid);
            Assert.Equal("Failed", result.Status);
        }

        [Fact]
        public void Move_RejectsStringCoordinatesInSnapshot()
        {
            var result = Verify("gh_move",
                new JObject { ["targets"] = new JObject { ["item"] = new JObject { ["x"] = 10, ["y"] = 20 } } },
                new JObject { ["updated"] = new JArray("item") },
                Snapshot("item", new JObject { ["x"] = 0, ["y"] = 0 }),
                Snapshot("item", new JObject { ["x"] = "10", ["y"] = 20 }));

            Assert.False(result.IsValid);
            Assert.Equal("Failed", result.Status);
        }

        [Fact]
        public void Connect_RejectsWireOnWrongInputPort()
        {
            var args = new JObject
            {
                ["connections"] = new JArray(new JObject
                {
                    ["sourceGuid"] = "source",
                    ["sourceParam"] = "Out",
                    ["targetGuid"] = "target",
                    ["targetParam"] = "B",
                }),
            };
            var before = new JObject
            {
                ["source"] = new JObject { ["outputs"] = new JArray(new JObject { ["guid"] = "out-guid", ["name"] = "Out" }) },
                ["target"] = new JObject { ["inputs"] = new JArray(
                    new JObject { ["guid"] = "input-a", ["name"] = "A", ["sources"] = new JArray() },
                    new JObject { ["guid"] = "input-b", ["name"] = "B", ["sources"] = new JArray() }) },
            };
            var after = (JObject)before.DeepClone();
            ((JArray)after["target"]["inputs"])[0]["sources"] = new JArray("out-guid");

            var result = Verify("gh_connect", args, new JObject { ["successCount"] = 1 }, before, after);

            Assert.False(result.IsValid);
            Assert.Contains("input port", result.Message);
        }

        [Fact]
        public void ScriptEdit_RejectsChangeToUnrelatedObject()
        {
            var before = new JObject
            {
                ["target"] = new JObject { ["serialized"] = "before" },
                ["other"] = new JObject { ["serialized"] = "old" },
            };
            var after = new JObject
            {
                ["target"] = new JObject { ["serialized"] = "before" },
                ["other"] = new JObject { ["serialized"] = "new" },
            };

            var result = Verify("script_parameter_modifier", new JObject { ["instanceGuid"] = "target" }, new JObject(), before, after);

            Assert.False(result.IsValid);
            Assert.Contains("target", result.Message);
        }

        [Fact]
        public void Lock_WhenDesiredStateAlreadyExists_ReturnsAlreadySatisfied()
        {
            var args = new JObject { ["guids"] = new JArray("item"), ["locked"] = true };
            var before = Snapshot("item", new JObject { ["locked"] = true });

            var result = Verify("gh_component_toggle_lock", args, new JObject { ["success"] = true }, before, (JObject)before.DeepClone());

            Assert.True(result.IsValid);
            Assert.Equal("AlreadySatisfied", result.Status);
        }

        [Fact]
        public void Lock_RejectsSnapshotThatDoesNotMatchRequestedState()
        {
            var result = Verify("gh_component_toggle_lock",
                new JObject { ["guids"] = new JArray("item"), ["locked"] = true },
                new JObject { ["success"] = true },
                Snapshot("item", new JObject { ["locked"] = false }),
                Snapshot("item", new JObject { ["locked"] = false }));

            Assert.False(result.IsValid);
            Assert.Equal("Failed", result.Status);
        }

        [Fact]
        public void Preview_UsesInverseHiddenState()
        {
            var result = Verify("gh_component_toggle_preview",
                new JObject { ["guids"] = new JArray("item"), ["previewOn"] = false },
                new JObject { ["success"] = true },
                Snapshot("item", new JObject { ["hidden"] = false }),
                Snapshot("item", new JObject { ["hidden"] = true }));

            Assert.True(result.IsValid);
            Assert.Equal("Succeeded", result.Status);
        }

        [Fact]
        public void ProtectedGuids_ReportPartialFailure()
        {
            var result = Verify("gh_component_toggle_lock",
                new JObject { ["guids"] = new JArray("item", "protected"), ["locked"] = true },
                new JObject { ["success"] = true, ["protectedGuids"] = new JArray("protected") },
                new JObject { ["item"] = new JObject { ["locked"] = false }, ["protected"] = new JObject { ["locked"] = false } },
                new JObject { ["item"] = new JObject { ["locked"] = true }, ["protected"] = new JObject { ["locked"] = false } });

            Assert.False(result.IsValid);
            Assert.Equal("Failed", result.Status);
            Assert.Contains("Partial failure", result.Message);
            Assert.Contains("protected", result.Message);
        }

        [Fact]
        public void FailedCount_RejectsPartialOperation()
        {
            var result = Verify("gh_component_toggle_lock",
                new JObject { ["guids"] = new JArray("item"), ["locked"] = true },
                new JObject { ["success"] = true, ["failCount"] = 1 },
                Snapshot("item", new JObject { ["locked"] = false }),
                Snapshot("item", new JObject { ["locked"] = true }));

            Assert.False(result.IsValid);
            Assert.Contains("partial", result.Message);
        }

        [Fact]
        public void TidyUpSelected_ReportsVerifiedMovementAsObservedChange()
        {
            var result = Verify("gh_tidy_up_selected", new JObject(), new JObject { ["moved"] = new JArray("item") },
                Snapshot("item", new JObject { ["x"] = 0, ["y"] = 0 }),
                Snapshot("item", new JObject { ["x"] = 20, ["y"] = 10 }));

            Assert.True(result.IsValid);
            Assert.Equal("ObservedChange", result.Status);
        }

        [Fact]
        public void TidyUp_WithTargetsVerifiesExactLayoutAndIsIdempotent()
        {
            var args = new JObject();
            var payload = new JObject
            {
                ["targets"] = new JObject { ["item"] = new JObject { ["x"] = 20, ["y"] = 10 } },
                ["moved"] = new JArray("item"),
            };
            var moved = Verify("gh_tidy_up", args, payload,
                Snapshot("item", new JObject { ["x"] = 0, ["y"] = 0 }),
                Snapshot("item", new JObject { ["x"] = 20, ["y"] = 10 }));
            var alreadySatisfied = Verify("gh_tidy_up_selected", args,
                new JObject { ["targets"] = payload["targets"].DeepClone(), ["moved"] = new JArray() },
                Snapshot("item", new JObject { ["x"] = 20, ["y"] = 10 }),
                Snapshot("item", new JObject { ["x"] = 20, ["y"] = 10 }));

            Assert.True(moved.IsValid);
            Assert.Equal("Succeeded", moved.Status);
            Assert.True(alreadySatisfied.IsValid);
            Assert.Equal("AlreadySatisfied", alreadySatisfied.Status);
        }

        [Fact]
        public void Put_ReportsExistenceOnlyAsObservedChange()
        {
            var result = Verify("gh_put", new JObject(), new JObject { ["instanceGuids"] = new JArray("placed") },
                new JObject(), Snapshot("placed", new JObject { ["serialized"] = "arbitrary" }));

            Assert.True(result.IsValid);
            Assert.Equal("ObservedChange", result.Status);
            Assert.Contains("content and placement were not verified", result.Message);
        }

        [Fact]
        public void Connect_RejectsNonObjectConnectionEntries()
        {
            var result = Verify("gh_connect", new JObject { ["connections"] = new JArray("invalid") }, new JObject(), new JObject(), new JObject());

            Assert.False(result.IsValid);
            Assert.Equal("Failed", result.Status);
        }

        [Fact]
        public void Put_RejectsPreviouslyExistingGuidFromExistenceOnlyBaseline()
        {
            var snapshot = Snapshot("existing", new JObject());
            var result = Verify("gh_put", new JObject(), new JObject { ["instanceGuids"] = new JArray("EXISTING") },
                snapshot, snapshot);

            Assert.False(result.IsValid);
            Assert.Contains("already existed", result.Message);
        }

        [Fact]
        public void Put_AcceptsOnlyReportedNewIdsInPostSnapshot()
        {
            var result = Verify("gh_put", new JObject(), new JObject { ["instanceGuids"] = new JArray("placed") },
                Snapshot("existing", new JObject()), Snapshot("placed", new JObject()));

            Assert.True(result.IsValid);
            Assert.Equal("ObservedChange", result.Status);
        }

        [Fact]
        public void Group_VerifiesNewGroupMembershipWithExistenceOnlyBaseline()
        {
            var args = new JObject { ["guids"] = new JArray("member") };
            var after = Snapshot("new-group", new JObject { ["members"] = new JArray("member") });
            var payload = new JObject { ["group"] = "new-group" };
            var created = Verify("gh_group_selected", args, payload, Snapshot("member", new JObject()), after);
            var reused = Verify("gh_group", args, payload, Snapshot("new-group", new JObject()), after);

            Assert.True(created.IsValid);
            Assert.False(reused.IsValid);
            Assert.Contains("already existed", reused.Message);
        }

        [Fact]
        public void GroupSelected_RejectsMissingFrozenMemberEvenIfOtherMembersExist()
        {
            var result = Verify("gh_group_selected", new JObject { ["guids"] = new JArray("approved", "missing") },
                new JObject { ["group"] = "new-group" },
                new JObject { ["approved"] = new JObject(), ["missing"] = new JObject(), ["other"] = new JObject() },
                Snapshot("new-group", new JObject { ["members"] = new JArray("approved", "other") }));

            Assert.False(result.IsValid);
            Assert.Contains("every requested", result.Message);
        }

        [Fact]
        public void SelectedLock_VerifiesOnlySelectedStateAndRequestedFlag()
        {
            var before = Snapshot("target", new JObject { ["selected"] = true, ["locked"] = false });
            var after = Snapshot("target", new JObject { ["locked"] = true });
            var result = Verify("gh_component_lock_selected", new JObject(), new JObject(), before, after);

            Assert.True(result.IsValid);
            Assert.Equal("Succeeded", result.Status);
        }

        [Fact]
        public void Disconnect_VerifiesMinimalEndpointSnapshots()
        {
            var args = new JObject { ["connections"] = new JArray(new JObject { ["sourceGuid"] = "source", ["targetGuid"] = "target" }) };
            var before = new JObject
            {
                ["source"] = new JObject { ["outputs"] = new JArray(new JObject { ["guid"] = "output" }) },
                ["target"] = new JObject { ["inputs"] = new JArray(new JObject { ["guid"] = "input", ["sources"] = new JArray("output") }) },
            };
            var after = (JObject)before.DeepClone();
            after["target"]["inputs"][0]["sources"] = new JArray();

            Assert.True(Verify("gh_disconnect", args, new JObject(), before, after).IsValid);
        }

        [Fact]
        public async Task ExecutionState_CancellationRetainsGateUntilDispatchedWorkFinishes()
        {
            using var state = new CodexToolExecutionState();
            using var first = state.CreateLinkedCancellation(CancellationToken.None);
            await state.WaitAsync(first.Token);
            state.CancelPending();
            using var second = state.CreateLinkedCancellation(CancellationToken.None);
            var waiting = state.WaitAsync(second.Token);

            Assert.True(first.IsCancellationRequested);
            Assert.False(second.IsCancellationRequested);
            Assert.False(waiting.IsCompleted);
            state.Release();
            await waiting.WaitAsync(TimeSpan.FromSeconds(2));
            state.Release();
        }

        [Fact]
        public async Task ExecutionState_DisposeCancelsQueuedWorkButAllowsActiveOwnerToRelease()
        {
            var state = new CodexToolExecutionState();
            using var token = state.CreateLinkedCancellation(CancellationToken.None);
            await state.WaitAsync(token.Token);
            var waiting = state.WaitAsync(token.Token);
            state.Dispose();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            state.Release();
            state.CancelPending();
            state.Dispose();
            Assert.Throws<ObjectDisposedException>(() => state.CreateLinkedCancellation(CancellationToken.None));
        }

        private static (bool IsValid, string Status, string Message) Verify(string name, JObject args, JObject payload, JObject before, JObject after)
        {
            return CanvasMutationVerifier.Verify(name, args, payload, before, after);
        }

        private static JObject Snapshot(string id, JObject state) => new JObject { [id] = state };
    }
}
