using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
#if OPENIDEA_TEST_FRAMEWORK
using UnityEditor.TestTools.TestRunner.Api;
#endif

namespace OpenIDEA.Unity
{
    /// Runs Edit Mode / Play Mode tests through the Test Framework and keeps the results for the
    /// IDE's Unity Tests view. Results live in SessionState: Play Mode tests reload the domain.
    internal static class TestBridge
    {
        const string ResultsKey = "openidea_test_results";
        const string RunningKey = "openidea_tests_running";

        [Serializable]
        class Result
        {
            public string fullName;
            public string name;
            public string status;
            public double duration;
            public string message;
            public string stack;
        }

        [Serializable]
        class ResultList
        {
            public List<Result> items = new List<Result>();
        }

        public static bool Running => SessionState.GetBool(RunningKey, false);

        static ResultList Load() =>
            JsonUtility.FromJson<ResultList>(SessionState.GetString(ResultsKey, "{}")) ?? new ResultList();

        static void Save(ResultList results) => SessionState.SetString(ResultsKey, JsonUtility.ToJson(results));

        public static string Results()
        {
            var json = new Json().Begin()
                .Field("available", Available)
                .Field("running", Running);
            json.BeginArray("results");
            foreach (var r in Load().items)
            {
                json.Begin()
                    .Field("fullName", r.fullName)
                    .Field("name", r.name)
                    .Field("status", r.status)
                    .Field("duration", r.duration)
                    .Field("message", r.message)
                    .Field("stack", r.stack)
                    .End();
            }
            return json.EndArray().End().ToString();
        }

#if OPENIDEA_TEST_FRAMEWORK
        const bool Available = true;

        [InitializeOnLoadMethod]
        static void RegisterCallbacks()
        {
            ScriptableObject.CreateInstance<TestRunnerApi>().RegisterCallbacks(new Callbacks());
        }

        /// `mode`: EditMode or PlayMode; `tests`/`groups`: `;`-separated full names / regexes (empty: all).
        public static string Run(string mode, string tests, string groups)
        {
            var filter = new Filter
            {
                testMode = mode == "PlayMode" ? TestMode.PlayMode : TestMode.EditMode,
                testNames = Split(tests),
                groupNames = Split(groups),
            };
            Save(new ResultList());
            SessionState.SetBool(RunningKey, true);
            ScriptableObject.CreateInstance<TestRunnerApi>().Execute(new ExecutionSettings(filter));
            Notify();
            return "{\"ok\":true}";
        }

        static string[] Split(string list) =>
            string.IsNullOrEmpty(list) ? null : list.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

        static void Notify() =>
            BridgeServer.Broadcast(new Json().Begin().Field("type", "tests").Field("running", Running).End().ToString());

        class Callbacks : ICallbacks
        {
            public void RunStarted(ITestAdaptor testsToRun)
            {
                SessionState.SetBool(RunningKey, true);
                Notify();
            }

            public void RunFinished(ITestResultAdaptor result)
            {
                SessionState.SetBool(RunningKey, false);
                Notify();
            }

            public void TestStarted(ITestAdaptor test) { }

            public void TestFinished(ITestResultAdaptor result)
            {
                if (result.HasChildren) return;
                var results = Load();
                results.items.RemoveAll(r => r.fullName == result.FullName);
                results.items.Add(new Result
                {
                    fullName = result.FullName,
                    name = result.Name,
                    status = result.TestStatus.ToString(),
                    duration = result.Duration,
                    message = result.Message ?? "",
                    stack = result.StackTrace ?? "",
                });
                Save(results);
                Notify();
            }
        }
#else
        const bool Available = false;

        public static string Run(string mode, string tests, string groups) =>
            "{\"error\":\"Install the Test Framework package (com.unity.test-framework) to run tests from OpenIDEA.\"}";
#endif
    }
}
