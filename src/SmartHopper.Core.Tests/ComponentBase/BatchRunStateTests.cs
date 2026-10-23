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

namespace SmartHopper.Core.Tests.ComponentBase
{
    using System;
    using System.Collections.Generic;
    using SmartHopper.Core.ComponentBase.Cores;
    using SmartHopper.Infrastructure.AICall.Batch;
    using SmartHopper.Infrastructure.AICall.Core.Requests;
    using SmartHopper.Infrastructure.AICall.Metrics;
    using Xunit;

    /// <summary>
    /// Exercises the internal state object without loading component or native API types.
    /// Reflection preserves its internal visibility without changing signing or friend assemblies.
    /// </summary>
    public class BatchRunStateTests
    {
        private static readonly Type StateType = typeof(ComponentStateManager).Assembly.GetType(
            "SmartHopper.Core.ComponentBase.Cores.BatchRunState", throwOnError: true);

        [Fact]
        public void ResetForNextRun_PreservesActiveSubmissionAndRestoredTrees()
        {
            var state = CreatePopulatedState();
            var submission = Get<AIBatchSubmission>(state, "Submission");
            var trees = Get<Dictionary<string, object>>(state, "SentinelTrees");

            Invoke(state, "ResetForNextRun");

            Assert.Same(submission, Get<AIBatchSubmission>(state, "Submission"));
            Assert.Same(trees, Get<Dictionary<string, object>>(state, "SentinelTrees"));
            Assert.Equal("saved branch mapping", trees["Result"]);
            AssertScratchCleared(state);
        }

        [Fact]
        public void ClearSentinels_DoesNotResetActiveBatchTimingOrMetrics()
        {
            var state = CreatePopulatedState();
            var submission = Get<AIBatchSubmission>(state, "Submission");
            var metrics = Get<AIMetricsList>(state, "PersistedMetricsList");
            var startTime = Get<DateTime?>(state, "StartTime");

            Invoke(state, "ClearSentinels");

            Assert.Null(Get<object>(state, "SentinelTrees"));
            Assert.Null(Get<object>(state, "SentinelIds"));
            Assert.Null(Get<object>(state, "Queue"));
            Assert.Same(submission, Get<AIBatchSubmission>(state, "Submission"));
            Assert.Same(metrics, Get<AIMetricsList>(state, "PersistedMetricsList"));
            Assert.Equal(startTime, Get<DateTime?>(state, "StartTime"));
            Assert.Equal(12.5, Get<double?>(state, "CompletionTime"));
            Assert.Equal(2, Get<int>(state, "ProgressCompleted"));
            Assert.True(Get<bool>(state, "UnsupportedChecked"));
        }

        [Fact]
        public void ResetForRestoration_DropsSubmissionAndMappingAsWellAsScratch()
        {
            var state = CreatePopulatedState();

            Invoke(state, "ResetForRestoration");

            Assert.Null(Get<object>(state, "Submission"));
            Assert.Null(Get<object>(state, "SentinelTrees"));
            AssertScratchCleared(state);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(12.5)]
        public void ConsumeCompletionTime_StampsEveryProviderAndReturnsDurationOnlyOnce(double duration)
        {
            var state = CreatePopulatedState();
            Set(state, "CompletionTime", duration);
            var metrics = Get<AIMetricsList>(state, "PersistedMetricsList");

            Assert.Equal(duration, (double?)Invoke(state, "ConsumeCompletionTime"));
            Assert.All(metrics.Entries, entry => Assert.Equal(duration, entry.CompletionTime));
            Assert.Null(Get<double?>(state, "CompletionTime"));

            // A later non-batch FinishResults must not stamp an old batch duration.
            var laterMetric = new AIMetrics { Provider = "later", Model = "model" };
            var originalCompletionTime = laterMetric.CompletionTime;
            metrics.Add(laterMetric);
            Assert.Null(Invoke(state, "ConsumeCompletionTime"));
            Assert.Equal(originalCompletionTime, laterMetric.CompletionTime);
            Assert.All(metrics.Entries, entry => Assert.Null(entry.Role));
        }

        [Fact]
        public void ConsumeCompletionTime_WithoutMetricsStillReturnsDurationForTreeStamping()
        {
            var state = Activator.CreateInstance(StateType, nonPublic: true);
            Set(state, "CompletionTime", 0.0);

            Assert.Equal(0.0, (double?)Invoke(state, "ConsumeCompletionTime"));
            Assert.Null(Invoke(state, "ConsumeCompletionTime"));
        }

        [Fact]
        public void ResetForNextRun_PreventsStaleDurationFromReachingNewMetrics()
        {
            var state = CreatePopulatedState();
            Invoke(state, "ResetForNextRun");
            var metrics = new AIMetricsList();
            var entry = new AIMetrics { Provider = "next", Model = "model", CompletionTime = 7.0 };
            metrics.Add(entry, "main");
            Set(state, "PersistedMetricsList", metrics);

            Assert.Null(Invoke(state, "ConsumeCompletionTime"));
            Assert.Equal(7.0, entry.CompletionTime);
            Assert.Equal("main", entry.Role);
        }

        private static object CreatePopulatedState()
        {
            var state = Activator.CreateInstance(StateType, nonPublic: true);
            var metrics = new AIMetricsList();
            metrics.Add(new AIMetrics { Provider = "first", Model = "model" });
            metrics.Add(new AIMetrics { Provider = "fallback", Model = "other-model" });
            Set(state, "Submission", new AIBatchSubmission("batch-id", "first", "{}", "custom-id"));
            Set(state, "SentinelTrees", new Dictionary<string, object> { ["Result"] = "saved branch mapping" });
            Set(state, "Queue", new List<(string CustomId, AIRequestCall Request)> { ("custom-id", null) });
            Set(state, "SentinelIds", new HashSet<string> { "custom-id" });
            Set(state, "ProgressCompleted", 2);
            Set(state, "UnsupportedChecked", true);
            Set(state, "StartTime", new DateTime(2026, 10, 23, 0, 0, 0, DateTimeKind.Utc));
            Set(state, "CompletionTime", 12.5);
            Set(state, "PersistedMetricsList", metrics);
            return state;
        }

        private static void AssertScratchCleared(object state)
        {
            Assert.Null(Get<object>(state, "Queue"));
            Assert.Null(Get<object>(state, "SentinelIds"));
            Assert.Equal(0, Get<int>(state, "ProgressCompleted"));
            Assert.False(Get<bool>(state, "UnsupportedChecked"));
            Assert.Null(Get<DateTime?>(state, "StartTime"));
            Assert.Null(Get<double?>(state, "CompletionTime"));
            Assert.Null(Get<object>(state, "PersistedMetricsList"));
        }

        private static T Get<T>(object state, string name) => (T)StateType.GetProperty(name).GetValue(state);

        private static void Set(object state, string name, object value) => StateType.GetProperty(name).SetValue(state, value);

        private static object Invoke(object state, string name) => StateType.GetMethod(name).Invoke(state, null);
    }
}
