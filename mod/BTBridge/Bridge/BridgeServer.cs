using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace BTBridge.Bridge
{
    public sealed class BridgeRequest
    {
        public string Method;
        public string Path;
        public Dictionary<string, string> Query = new Dictionary<string, string>();
        public string Body;

        public string QueryOr(string key, string fallback) =>
            Query.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;
    }

    /// <summary>A failure the client should see as a specific HTTP status, not a 500.</summary>
    public sealed class BridgeException : Exception
    {
        public readonly int Status;

        public BridgeException(int status, string message) : base(message)
        {
            Status = status;
        }
    }

    public sealed class Route
    {
        public string Method;
        public string Path;
        public bool OnMainThread = true;
        public Func<BridgeRequest, object> Handler;
    }

    /// <summary>
    /// Localhost-only HTTP/JSON server on a background thread. Every response is an
    /// envelope: {"ok": true, "data": ...} or {"ok": false, "error": "..."}.
    /// </summary>
    public sealed class BridgeServer
    {
        public static readonly JsonSerializerSettings Json = new JsonSerializerSettings
        {
            Converters = { new StringEnumConverter() },
            NullValueHandling = NullValueHandling.Include,
            Formatting = Formatting.None,
        };

        private readonly int port;
        private readonly Dictionary<string, Route> routes = new Dictionary<string, Route>();
        private HttpListener listener;
        private Thread thread;

        public BridgeServer(int port, IEnumerable<Route> routeList)
        {
            this.port = port;
            foreach (var r in routeList)
            {
                routes[Key(r.Method, r.Path)] = r;
            }
        }

        public void Start()
        {
            listener = new HttpListener();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            thread = new Thread(Loop) { IsBackground = true, Name = "BTBridge.Http" };
            thread.Start();
        }

        private static string Key(string method, string path) => method.ToUpperInvariant() + " " + path.TrimEnd('/').ToLowerInvariant();

        private void Loop()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = listener.GetContext();
                }
                catch (Exception e)
                {
                    Log.Error("listener stopped", e);
                    return;
                }
                // One request at a time is plenty for a single agent, and it keeps
                // ordering of writes trivially sequential.
                Handle(ctx);
            }
        }

        private void Handle(HttpListenerContext ctx)
        {
            int status = 200;
            object envelope;
            try
            {
                var req = ReadRequest(ctx.Request);
                if (!routes.TryGetValue(Key(req.Method, req.Path), out var route))
                {
                    throw new BridgeException(404, $"no route {req.Method} {req.Path}");
                }
                object data = route.OnMainThread
                    ? MainThread.Run(() => route.Handler(req), Main.Settings.MainThreadTimeoutMs)
                    : route.Handler(req);
                envelope = new { ok = true, data };
            }
            catch (BridgeException e)
            {
                status = e.Status;
                envelope = new { ok = false, error = e.Message };
            }
            catch (TimeoutException e)
            {
                status = 503;
                envelope = new { ok = false, error = e.Message };
            }
            catch (Exception e)
            {
                status = 500;
                Log.Error($"{ctx.Request.HttpMethod} {ctx.Request.Url.AbsolutePath}", e);
                envelope = new { ok = false, error = e.GetType().Name + ": " + e.Message };
            }
            Write(ctx.Response, status, envelope);
        }

        private static BridgeRequest ReadRequest(HttpListenerRequest r)
        {
            var req = new BridgeRequest { Method = r.HttpMethod, Path = r.Url.AbsolutePath };
            foreach (string k in r.QueryString.AllKeys)
            {
                if (k != null)
                {
                    req.Query[k] = r.QueryString[k];
                }
            }
            if (r.HasEntityBody)
            {
                using (var reader = new StreamReader(r.InputStream, Encoding.UTF8))
                {
                    req.Body = reader.ReadToEnd();
                }
            }
            return req;
        }

        private static void Write(HttpListenerResponse resp, int status, object envelope)
        {
            try
            {
                string json;
                try
                {
                    json = JsonConvert.SerializeObject(envelope, Json);
                }
                catch (Exception e)
                {
                    status = 500;
                    json = JsonConvert.SerializeObject(new { ok = false, error = "serialization failed: " + e.Message });
                }
                var bytes = Encoding.UTF8.GetBytes(json);
                resp.StatusCode = status;
                resp.ContentType = "application/json; charset=utf-8";
                resp.ContentLength64 = bytes.Length;
                resp.OutputStream.Write(bytes, 0, bytes.Length);
                resp.OutputStream.Close();
            }
            catch (Exception e)
            {
                Log.Warn("failed to write response: " + e.Message);
            }
        }
    }
}
