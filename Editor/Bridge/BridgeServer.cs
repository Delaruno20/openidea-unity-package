using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using UnityEditor;
using UnityEngine;

namespace OpenIDEA.Unity
{
    /// A loopback HTTP + WebSocket server the OpenIDEA plugin talks to (Rider uses its own protocol
    /// the same way). It writes Library/OpenIDEA/bridge.json with its port and a per-session token;
    /// every request must carry that token, so only a process that can read the project connects.
    internal static class BridgeServer
    {
        const string TokenHeader = "x-openidea-token";
        const int MainThreadTimeoutMs = 15000;

        static TcpListener listener;
        static Thread acceptThread;
        static string token;
        static readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
        static readonly List<WebSocketClient> sockets = new List<WebSocketClient>();

        public static int Port { get; private set; }

        static string BridgeFile => Path.Combine(OpenIdeaCodeEditor.ProjectRoot, "Library", "OpenIDEA", "bridge.json");

        public static void EnsureStarted()
        {
            if (listener != null) return;

            token = SessionState.GetString("openidea_token", "");
            if (token.Length == 0)
            {
                var bytes = new byte[24];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
                token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
                SessionState.SetString("openidea_token", token);
            }

            // Keep the port across domain reloads so the IDE reconnects to the same address.
            var preferred = SessionState.GetInt("openidea_port", 0);
            try
            {
                listener = new TcpListener(IPAddress.Loopback, preferred);
                listener.Start();
            }
            catch (SocketException)
            {
                listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
            }
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            SessionState.SetInt("openidea_port", Port);
            WriteBridgeFile();

            acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "OpenIDEA bridge" };
            acceptThread.Start();

            EditorApplication.update += Pump;
            AssemblyReloadEvents.beforeAssemblyReload += Stop;
            EditorApplication.quitting += OnQuit;
            UnityLog.Start();
            EditorState.Start();
        }

        static void Stop()
        {
            EditorApplication.update -= Pump;
            Broadcast("{\"type\":\"reloading\"}");
            lock (sockets)
            {
                foreach (var socket in sockets) socket.Close();
                sockets.Clear();
            }
            try { listener?.Stop(); } catch (Exception) { /* already stopped */ }
            listener = null;
        }

        static void OnQuit()
        {
            Stop();
            try { File.Delete(BridgeFile); } catch (Exception) { /* nothing to clean */ }
        }

        static void WriteBridgeFile()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BridgeFile));
            var json = new Json().Begin()
                .Field("port", Port)
                .Field("token", token)
                .Field("pid", System.Diagnostics.Process.GetCurrentProcess().Id)
                .Field("unityVersion", Application.unityVersion)
                .Field("projectPath", OpenIdeaCodeEditor.ProjectRoot.Replace('\\', '/'))
                .End().ToString();
            File.WriteAllText(BridgeFile, json);
        }

        /// Runs queued work on Unity's main thread (the only place the Editor API may be used).
        static void Pump()
        {
            var budget = 64;
            while (budget-- > 0 && mainThread.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception e) { Debug.LogException(e); }
            }
            UnityLog.Pump();
            EditorState.Pump();
        }

        static T OnMainThread<T>(Func<T> work)
        {
            var done = new ManualResetEventSlim();
            var result = default(T);
            Exception error = null;
            mainThread.Enqueue(() =>
            {
                try { result = work(); }
                catch (Exception e) { error = e; }
                finally { done.Set(); }
            });
            if (!done.Wait(MainThreadTimeoutMs)) throw new TimeoutException("Unity is busy");
            if (error != null) throw error;
            return result;
        }

        public static void Broadcast(string json)
        {
            lock (sockets)
            {
                sockets.RemoveAll(s => !s.Send(json));
            }
        }

        static void AcceptLoop()
        {
            var current = listener;
            while (current != null)
            {
                TcpClient client;
                try { client = current.AcceptTcpClient(); }
                catch (Exception) { return; }
                ThreadPool.QueueUserWorkItem(_ => Serve(client));
            }
        }

        static void Serve(TcpClient client)
        {
            var keepOpen = false;
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                stream.ReadTimeout = 10000;
                var request = HttpRequest.Read(stream);
                if (request == null) return;

                if (!request.Headers.TryGetValue(TokenHeader, out var given) || given != token)
                {
                    Respond(stream, 401, "{\"error\":\"bad token\"}");
                    return;
                }

                if (request.Path == "/ws")
                {
                    var socket = WebSocketClient.Accept(client, stream, request);
                    if (socket == null) return;
                    keepOpen = true;
                    lock (sockets) sockets.Add(socket);
                    socket.Send(EditorState.Last ?? "{\"type\":\"hello\"}");
                    return;
                }

                string body;
                int status = 200;
                try
                {
                    body = Route(request);
                    if (body == null)
                    {
                        status = 404;
                        body = "{\"error\":\"unknown endpoint\"}";
                    }
                }
                catch (Exception e)
                {
                    status = 500;
                    body = new Json().Begin().Field("error", e.Message).End().ToString();
                }
                Respond(stream, status, body);
            }
            catch (Exception)
            {
                // The IDE went away mid-request.
            }
            finally
            {
                if (!keepOpen) client.Close();
            }
        }

        static string Route(HttpRequest r)
        {
            switch (r.Path)
            {
                case "/status": return OnMainThread(() => EditorState.Snapshot("state"));
                case "/play": return OnMainThread(() => EditorState.Play(r.Query("state")));
                case "/pause": return OnMainThread(() => EditorState.Pause(r.Query("state")));
                case "/step": return OnMainThread(EditorState.Step);
                case "/refresh": return OnMainThread(() => EditorState.Refresh(r.Query("force") == "1", r.Query("auto") == "1"));
                case "/sync": return OnMainThread(() => { ProjectGeneration.Sync(true); return "{\"ok\":true}"; });
                case "/menu": return OnMainThread(() => EditorState.Menu(r.Query("path")));
                case "/select": return OnMainThread(() => EditorState.Select(r.Query("path"), r.Query("fileId")));
                case "/logs": return UnityLog.Since(long.TryParse(r.Query("since"), out var since) ? since : 0);
                case "/clear-logs": return OnMainThread(UnityLog.Clear);
                case "/tests": return OnMainThread(TestBridge.Results);
                case "/tests/run": return OnMainThread(() => TestBridge.Run(r.Query("mode"), r.Query("tests"), r.Query("groups")));
                default: return null;
            }
        }

        static void Respond(Stream stream, int status, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            var reason = status == 200 ? "OK" : status == 401 ? "Unauthorized" : status == 404 ? "Not Found" : "Error";
            var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
            var headBytes = Encoding.ASCII.GetBytes(head);
            stream.Write(headBytes, 0, headBytes.Length);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }
    }

    internal sealed class HttpRequest
    {
        public string Method;
        public string Path;
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>();
        readonly Dictionary<string, string> query = new Dictionary<string, string>();

        public string Query(string name) => query.TryGetValue(name, out var v) ? v : null;

        public static HttpRequest Read(Stream stream)
        {
            var requestLine = ReadLine(stream);
            if (string.IsNullOrEmpty(requestLine)) return null;
            var parts = requestLine.Split(' ');
            if (parts.Length < 2) return null;

            var request = new HttpRequest { Method = parts[0] };
            var target = parts[1];
            var q = target.IndexOf('?');
            request.Path = q < 0 ? target : target.Substring(0, q);
            if (q >= 0)
            {
                foreach (var pair in target.Substring(q + 1).Split('&'))
                {
                    if (pair.Length == 0) continue;
                    var eq = pair.IndexOf('=');
                    var key = Uri.UnescapeDataString(eq < 0 ? pair : pair.Substring(0, eq));
                    var value = eq < 0 ? "" : Uri.UnescapeDataString(pair.Substring(eq + 1).Replace('+', ' '));
                    request.query[key] = value;
                }
            }

            string line;
            while (!string.IsNullOrEmpty(line = ReadLine(stream)))
            {
                var colon = line.IndexOf(':');
                if (colon > 0) request.Headers[line.Substring(0, colon).Trim().ToLowerInvariant()] = line.Substring(colon + 1).Trim();
            }

            // Bodies are not used; drain one so the client isn't reset mid-send.
            if (request.Headers.TryGetValue("content-length", out var lengthText) && int.TryParse(lengthText, out var length))
            {
                var buffer = new byte[Math.Min(length, 1 << 16)];
                var left = Math.Min(length, buffer.Length);
                while (left > 0)
                {
                    var n = stream.Read(buffer, 0, left);
                    if (n <= 0) break;
                    left -= n;
                }
            }
            return request;
        }

        static string ReadLine(Stream stream)
        {
            var bytes = new List<byte>();
            while (bytes.Count < 16384)
            {
                var b = stream.ReadByte();
                if (b < 0) return bytes.Count == 0 ? null : Encoding.UTF8.GetString(bytes.ToArray());
                if (b == '\n') break;
                if (b != '\r') bytes.Add((byte)b);
            }
            return Encoding.UTF8.GetString(bytes.ToArray());
        }
    }

    /// Server side of RFC 6455, text frames out, close/ping handled in.
    internal sealed class WebSocketClient
    {
        readonly TcpClient client;
        readonly Stream stream;
        readonly object writeLock = new object();
        volatile bool closed;

        WebSocketClient(TcpClient client, Stream stream)
        {
            this.client = client;
            this.stream = stream;
        }

        public static WebSocketClient Accept(TcpClient client, Stream stream, HttpRequest request)
        {
            if (!request.Headers.TryGetValue("sec-websocket-key", out var key)) return null;
            string accept;
            using (var sha1 = SHA1.Create())
                accept = Convert.ToBase64String(sha1.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            var head = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
            var bytes = Encoding.ASCII.GetBytes(head);
            stream.Write(bytes, 0, bytes.Length);
            stream.ReadTimeout = Timeout.Infinite;

            var socket = new WebSocketClient(client, stream);
            new Thread(socket.ReadLoop) { IsBackground = true, Name = "OpenIDEA bridge socket" }.Start();
            return socket;
        }

        public bool Send(string text) => SendFrame(0x1, Encoding.UTF8.GetBytes(text));

        bool SendFrame(byte opcode, byte[] payload)
        {
            if (closed) return false;
            try
            {
                lock (writeLock)
                {
                    var header = new List<byte> { (byte)(0x80 | opcode) };
                    if (payload.Length < 126) header.Add((byte)payload.Length);
                    else if (payload.Length <= ushort.MaxValue)
                    {
                        header.Add(126);
                        header.Add((byte)(payload.Length >> 8));
                        header.Add((byte)payload.Length);
                    }
                    else
                    {
                        header.Add(127);
                        for (var i = 7; i >= 0; i--) header.Add((byte)((long)payload.Length >> (8 * i)));
                    }
                    stream.Write(header.ToArray(), 0, header.Count);
                    stream.Write(payload, 0, payload.Length);
                    stream.Flush();
                }
                return true;
            }
            catch (Exception)
            {
                Close();
                return false;
            }
        }

        void ReadLoop()
        {
            try
            {
                while (!closed)
                {
                    var b0 = stream.ReadByte();
                    var b1 = stream.ReadByte();
                    if (b0 < 0 || b1 < 0) break;
                    var opcode = b0 & 0x0f;
                    long length = b1 & 0x7f;
                    if (length == 126) length = (ReadByte() << 8) | ReadByte();
                    else if (length == 127)
                    {
                        length = 0;
                        for (var i = 0; i < 8; i++) length = (length << 8) | (long)ReadByte();
                    }
                    var mask = (b1 & 0x80) != 0 ? new[] { ReadByte(), ReadByte(), ReadByte(), ReadByte() } : null;
                    if (length > 1 << 20) break;
                    var payload = new byte[length];
                    var read = 0;
                    while (read < length)
                    {
                        var n = stream.Read(payload, read, (int)length - read);
                        if (n <= 0) throw new EndOfStreamException();
                        read += n;
                    }
                    if (mask != null)
                        for (var i = 0; i < payload.Length; i++) payload[i] ^= (byte)mask[i % 4];

                    if (opcode == 0x8) break;
                    if (opcode == 0x9) SendFrame(0xA, payload);
                }
            }
            catch (Exception)
            {
                // Connection dropped.
            }
            Close();
        }

        int ReadByte()
        {
            var b = stream.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            return b;
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            try
            {
                lock (writeLock)
                {
                    stream.Write(new byte[] { 0x88, 0x00 }, 0, 2);
                }
            }
            catch (Exception) { /* already gone */ }
            try { client.Close(); } catch (Exception) { /* already gone */ }
        }
    }
}
