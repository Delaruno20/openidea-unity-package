using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace OpenIDEA.Unity
{
    /// The Unity console as the IDE sees it: every message with its stack and source location,
    /// plus the compiler's errors and warnings. Ids grow within a domain; `session` changes on
    /// every domain reload so the IDE knows to start again from 0 (it keeps what it already has).
    internal static class UnityLog
    {
        const int Capacity = 5000;

        struct Entry
        {
            public long Id;
            public string Type;
            public string Message;
            public string Stack;
            public string File;
            public int Line;
            public int Column;
            public string Time;
        }

        static readonly ConcurrentQueue<Entry> incoming = new ConcurrentQueue<Entry>();
        static readonly List<Entry> entries = new List<Entry>();
        static readonly object gate = new object();
        static readonly string session = Guid.NewGuid().ToString("N");
        static long nextId = 1;
        static bool dirty;
        static double lastNotify;

        static readonly Regex StackLocation = new Regex(@"\(at (?<file>.+?):(?<line>\d+)\)\s*$", RegexOptions.Compiled | RegexOptions.Multiline);
        static readonly Regex CompilerLine = new Regex(@"^.+\(\d+,\d+\): (error|warning) [A-Z]+\d+", RegexOptions.Compiled);

        public static void Start()
        {
            Application.logMessageReceivedThreaded += OnLog;
            CompilationPipeline.assemblyCompilationFinished += OnCompiled;
        }

        static void OnLog(string condition, string stackTrace, LogType type)
        {
            // Compiler output comes in through OnCompiled with its exact position.
            if (CompilerLine.IsMatch(condition)) return;
            incoming.Enqueue(new Entry
            {
                Type = type.ToString(),
                Message = condition,
                Stack = stackTrace ?? "",
                Time = DateTime.Now.ToString("HH:mm:ss"),
            });
        }

        static void OnCompiled(string assembly, CompilerMessage[] messages)
        {
            foreach (var m in messages)
            {
                if (m.type != CompilerMessageType.Error && m.type != CompilerMessageType.Warning) continue;
                incoming.Enqueue(new Entry
                {
                    Type = m.type == CompilerMessageType.Error ? "CompilerError" : "CompilerWarning",
                    Message = m.message,
                    Stack = "",
                    File = m.file,
                    Line = m.line,
                    Column = m.column,
                    Time = DateTime.Now.ToString("HH:mm:ss"),
                });
            }
        }

        /// Main thread: resolves locations (Unity maps package paths only here) and tells the IDE, at most 10 times a second.
        public static void Pump()
        {
            var added = false;
            while (incoming.TryDequeue(out var e))
            {
                if (e.File == null)
                {
                    var match = StackLocation.Match(e.Stack);
                    if (match.Success)
                    {
                        e.File = match.Groups["file"].Value;
                        e.Line = int.Parse(match.Groups["line"].Value);
                    }
                }
                if (!string.IsNullOrEmpty(e.File)) e.File = ProjectRelative(e.File);
                lock (gate)
                {
                    e.Id = nextId++;
                    entries.Add(e);
                    if (entries.Count > Capacity) entries.RemoveRange(0, entries.Count - Capacity);
                }
                added = true;
            }
            dirty |= added;

            var now = EditorApplication.timeSinceStartup;
            if (dirty && now - lastNotify > 0.1)
            {
                dirty = false;
                lastNotify = now;
                long last;
                lock (gate) last = nextId - 1;
                BridgeServer.Broadcast(new Json().Begin().Field("type", "logs").Field("session", session).Field("last", last).End().ToString());
            }
        }

        static string ProjectRelative(string file)
        {
            try
            {
                var full = Path.GetFullPath(file.Replace('\\', '/')).Replace('\\', '/');
                var root = OpenIdeaCodeEditor.ProjectRoot.Replace('\\', '/').TrimEnd('/') + "/";
                return full.StartsWith(root, StringComparison.Ordinal) ? full.Substring(root.Length) : full;
            }
            catch (Exception)
            {
                return file;
            }
        }

        public static string Since(long since)
        {
            var json = new Json().Begin().Field("session", session);
            json.BeginArray("entries");
            lock (gate)
            {
                foreach (var e in entries)
                {
                    if (e.Id <= since) continue;
                    json.Begin()
                        .Field("id", e.Id)
                        .Field("type", e.Type)
                        .Field("message", e.Message)
                        .Field("stack", e.Stack)
                        .Field("file", e.File)
                        .Field("line", e.Line)
                        .Field("column", e.Column)
                        .Field("time", e.Time)
                        .End();
                }
            }
            return json.EndArray().End().ToString();
        }

        public static string Clear()
        {
            lock (gate) entries.Clear();
            // UnityEditor.LogEntries is internal; clearing the console is best effort.
            var logEntries = Type.GetType("UnityEditor.LogEntries, UnityEditor");
            logEntries?.GetMethod("Clear", BindingFlags.Static | BindingFlags.Public)?.Invoke(null, null);
            BridgeServer.Broadcast(new Json().Begin().Field("type", "logs-cleared").Field("session", session).End().ToString());
            return "{\"ok\":true}";
        }
    }
}
