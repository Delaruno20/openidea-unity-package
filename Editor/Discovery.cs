using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace OpenIDEA.Unity
{
    /// Finds OpenIDEA installations and starts it on a file.
    internal static class Discovery
    {
        const string BinaryName = "openidea";

        public static bool IsOpenIdea(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            var trimmed = path.TrimEnd('/', '\\');
            var name = Path.GetFileNameWithoutExtension(trimmed);
            return name.IndexOf(BinaryName, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static string[] Installations()
        {
            var found = new List<string>();
            foreach (var candidate in Candidates())
            {
                try
                {
                    if ((File.Exists(candidate) || Directory.Exists(candidate)) && !found.Contains(candidate))
                        found.Add(candidate);
                }
                catch (Exception)
                {
                    // An unreadable location is simply not an installation.
                }
            }
            return found.ToArray();
        }

        static IEnumerable<string> Candidates()
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsEditor:
                    var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                    var programs = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                    yield return Path.Combine(local, "OpenIDEA", "openidea.exe");
                    yield return Path.Combine(local, "Programs", "OpenIDEA", "openidea.exe");
                    yield return Path.Combine(programs, "OpenIDEA", "openidea.exe");
                    break;
                case RuntimePlatform.OSXEditor:
                    yield return "/Applications/OpenIDEA.app";
                    yield return Path.Combine(home, "Applications", "OpenIDEA.app");
                    break;
                default:
                    yield return "/usr/bin/openidea";
                    yield return "/usr/local/bin/openidea";
                    yield return "/opt/OpenIDEA/openidea";
                    yield return Path.Combine(home, ".local", "bin", "openidea");
                    var appImages = Path.Combine(home, "Applications");
                    if (Directory.Exists(appImages))
                        foreach (var image in Directory.GetFiles(appImages, "*.AppImage").Where(IsOpenIdea))
                            yield return image;
                    break;
            }

            var exe = Application.platform == RuntimePlatform.WindowsEditor ? BinaryName + ".exe" : BinaryName;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            foreach (var dir in path.Split(Path.PathSeparator).Where(d => d.Length > 0))
                yield return Path.Combine(dir, exe);
        }

        /// Opens the project in OpenIDEA, with `file` at `line`/`column` (1-based; 0 or less: no position).
        public static bool Open(string installation, string projectRoot, string file, int line, int column)
        {
            if (string.IsNullOrEmpty(installation)) return false;

            var args = new List<string> { projectRoot };
            if (!string.IsNullOrEmpty(file))
            {
                args.Add("--file");
                args.Add(file);
                if (line > 0)
                {
                    args.Add("--line");
                    args.Add(line.ToString());
                    if (column > 0)
                    {
                        args.Add("--column");
                        args.Add(column.ToString());
                    }
                }
            }

            var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = projectRoot };
            if (Application.platform == RuntimePlatform.OSXEditor && installation.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                start.FileName = "open";
                start.Arguments = "-a " + Quote(installation) + " --args " + string.Join(" ", args.Select(Quote));
            }
            else
            {
                start.FileName = installation;
                start.Arguments = string.Join(" ", args.Select(Quote));
            }

            try
            {
                Process.Start(start);
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError($"[OpenIDEA] Could not start {installation}: {e.Message}");
                return false;
            }
        }

        static string Quote(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') sb.Append('\\', backslashes * 2 + 1);
                else sb.Append('\\', backslashes);
                backslashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', backslashes * 2);
            return sb.Append('"').ToString();
        }
    }
}
