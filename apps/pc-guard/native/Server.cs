using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace PCGuard
{
    // Небольшой loopback-сервер: ограниченные заголовки/тело, ключ запуска и строгий список ресурсов.
    internal sealed class Server : IDisposable
    {
        private readonly Store store; private readonly Api api; private readonly string token; private readonly Suite suite;
        private readonly TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        private readonly ManualResetEvent stop = new ManualResetEvent(false);
        private readonly SemaphoreSlim capacity = new SemaphoreSlim(16, 16);
        internal int Port { get; private set; }
        private readonly Dictionary<string, string> assets = new Dictionary<string, string> {
            {"/","index.html"},{"/assets/creative-relab-avatar.png","assets/creative-relab-avatar.png"},{"/assets/zabivki-logo.png","assets/zabivki-logo.png"},{"/assets/partner.css","assets/partner.css"},{"/assets/driver-launch.css","assets/driver-launch.css"},{"/assets/family.js","assets/family.js"},{"/assets/family.css","assets/family.css"},{"/assets/app.js","assets/app.js"},{"/assets/app.css","assets/app.css"},{"/assets/logo.svg","assets/logo.svg"},{"/assets/suite.js","assets/suite.js"},{"/assets/driver-ui.js","assets/driver-ui.js"},{"/assets/camera-policy-ui.js","assets/camera-policy-ui.js"},
            {"/vendor/bootstrap.min.css","vendor/bootstrap.min.css"},{"/vendor/jquery.min.js","vendor/jquery.min.js"},{"/vendor/dataTables.min.js","vendor/dataTables.min.js"},{"/vendor/dataTables.dataTables.min.css","vendor/dataTables.dataTables.min.css"}
        };
        internal Server(Store store, string token) { this.store = store; this.token = token; suite=new Suite(store); api = new Api(store, RequestStop,suite); }
        internal void Start()
        {
            listener.Start(32); Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            ThreadPool.QueueUserWorkItem(delegate {
                while (!stop.WaitOne(0))
                {
                    try
                    {
                        if (!listener.Pending()) { stop.WaitOne(40); continue; }
                        TcpClient client = listener.AcceptTcpClient();
                        if (!capacity.Wait(0)) { client.Close(); continue; }
                        ThreadPool.QueueUserWorkItem(delegate { try { Handle(client); } catch (IOException) { } catch (SocketException) { } catch (InvalidOperationException) { } finally { client.Close(); capacity.Release(); } });
                    }
                    catch (SocketException) { if (!stop.WaitOne(0)) Thread.Sleep(100); }
                    catch (ObjectDisposedException) { break; }
                }
            });
        }
        internal void Wait() { stop.WaitOne(); }
        internal void RequestStop() { if(suite.Busy)throw new InvalidOperationException("Дождись завершения собственной операции. Для обхода файлов сначала можно запросить отмену.");ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(800); stop.Set(); }); }
        private static string Header(NetworkStream stream)
        {
            var bytes = new List<byte>(); int state = 0;
            while (bytes.Count < 16384)
            {
                int next = stream.ReadByte(); if (next < 0) throw new IOException("Неполный HTTP-запрос."); bytes.Add((byte)next);
                state = next == (state == 0 || state == 2 ? 13 : 10) ? state + 1 : next == 13 ? 1 : 0;
                if (state == 4) return Encoding.ASCII.GetString(bytes.ToArray());
            }
            throw new IOException("Заголовки слишком велики.");
        }
        private void Handle(TcpClient client)
        {
            client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
            using (NetworkStream stream = client.GetStream())
            {
                try
                {
                    string[] lines = Header(stream).Split(new[] { "\r\n" }, StringSplitOptions.None);
                    string[] first = lines[0].Split(' '); if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal)) throw new IOException("Некорректный HTTP-запрос.");
                    var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 1; i < lines.Length && lines[i].Length > 0; i++)
                    {
                        int separator = lines[i].IndexOf(':'); if (separator < 1) throw new IOException("Некорректный заголовок.");
                        string name = lines[i].Substring(0, separator).Trim(); if (headers.ContainsKey(name)) throw new IOException("Повторный заголовок."); headers[name] = lines[i].Substring(separator + 1).Trim();
                    }
                    // Даже при отказе авторизации дочитываем ограниченное тело, чтобы закрытие не сбрасывало TCP-ответ.
                    int length = 0;
                    if (headers.ContainsKey("Transfer-Encoding") || (headers.ContainsKey("Content-Length") && (!Int32.TryParse(Value(headers, "Content-Length"), out length) || length < 0 || length > 65536))) { Json(stream, 413, Store.Map("ok", false, "error", "Некорректная длина запроса.")); return; }
                    byte[] body = new byte[length]; int read = 0; while (read < length) { int chunk = stream.Read(body, read, length - read); if (chunk <= 0) throw new IOException("Неполное тело запроса."); read += chunk; }
                    string host = "127.0.0.1:" + Port;
                    if (Value(headers, "Host") != host) { Json(stream, 403, Store.Map("ok", false, "error", "Чужой Host.")); return; }
                    if (first[1] == "/api")
                    {
                        if (!Equal(token, Value(headers, "X-PCGuard-Token"))) { Json(stream, 403, Store.Map("ok", false, "error", "Открой PC Guard через файл запуска.")); return; }
                        if (first[0] != "POST") { Json(stream, 405, Store.Map("ok", false, "error", "Допустим только POST.")); return; }
                        string origin = Value(headers, "Origin"); if (origin.Length > 0 && origin != "http://" + host) { Json(stream, 403, Store.Map("ok", false, "error", "Чужой источник запроса.")); return; }
                        if (length < 2) { Json(stream, 413, Store.Map("ok", false, "error", "Некорректная длина запроса.")); return; }
                        object parsed = Store.Serializer().DeserializeObject(Store.Utf8.GetString(body)); var input = parsed as Dictionary<string, object>; if (input == null) throw new ArgumentException("Нужен JSON-объект.");
                        Json(stream, 200, Store.Map("ok", true, "data", api.Dispatch(input))); return;
                    }
                    if (first[0] != "GET" || !assets.ContainsKey(first[1])) { Json(stream, 404, Store.Map("ok", false, "error", "Не найдено")); return; }
                    string path = Path.Combine(store.Root, "public", assets[first[1]].Replace('/', Path.DirectorySeparatorChar));
                    string extension = Path.GetExtension(path); string type = extension == ".html" ? "text/html; charset=utf-8" : extension == ".js" ? "text/javascript; charset=utf-8" : extension == ".css" ? "text/css; charset=utf-8" : extension == ".png" ? "image/png" : "image/svg+xml";
                    // Ресурсы окна включены в EXE: запуск не зависит от доступа к отдельным HTML/JS-файлам.
                    string resource = "PCGuard." + assets[first[1]].Replace('/', '.');
                    using (Stream embedded = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                    {
                        if (embedded == null || embedded.Length > 2 * 1024 * 1024) throw new IOException("Ресурс интерфейса отсутствует или слишком велик.");
                        using (var memory = new MemoryStream()) { embedded.CopyTo(memory); Respond(stream, 200, type, memory.ToArray()); }
                    }
                }
                catch (Exception error)
                {
                    try { Json(stream, 400, Store.Map("ok", false, "error", error.Message)); } catch (IOException) { } catch (ObjectDisposedException) { }
                }
            }
        }
        private static string Value(Dictionary<string, string> headers, string name) { string value; return headers.TryGetValue(name, out value) ? value : ""; }
        private static bool Equal(string a, string b) { if (a.Length != b.Length) return false; int difference = 0; for (int i = 0; i < a.Length; i++) difference |= a[i] ^ b[i]; return difference == 0; }
        private static void Json(NetworkStream stream, int code, object value) { Respond(stream, code, "application/json; charset=utf-8", Store.Utf8.GetBytes(Store.Json(value))); }
        private static void Respond(NetworkStream stream, int code, string type, byte[] body)
        {
            string status = code == 200 ? "OK" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : code == 405 ? "Method Not Allowed" : code == 413 ? "Payload Too Large" : "Bad Request";
            string header = "HTTP/1.1 " + code + " " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\nReferrer-Policy: no-referrer\r\nContent-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'\r\n\r\n";
            byte[] bytes = Encoding.ASCII.GetBytes(header); stream.Write(bytes, 0, bytes.Length); stream.Write(body, 0, body.Length);
        }
        public void Dispose() { stop.Set(); listener.Stop(); suite.Dispose(); }
    }
}
