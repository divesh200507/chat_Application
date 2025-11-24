using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace TcpChatServer;

internal class Program
{
    // Basic config
    private const int Port = 5000;
    private const int IdleTimeoutSeconds = 120;
    private const int MetricsIntervalSeconds = 5;

    // Static credentials (username -> password)
    private static readonly ConcurrentDictionary<string, string> Credentials = new()
    {
        ["alice"] = "password1",
        ["bob"]   = "password2",
        ["charlie"] = "password3"
    };

    // Online users (username -> session)
    private static readonly ConcurrentDictionary<string, ClientSession> OnlineUsers = new();

    // All sessions (for idle timeout scanning)
    private static readonly ConcurrentDictionary<Guid, ClientSession> Sessions = new();

    // Metrics
    private static long _messagesDelivered;
    private static long _messagesDeliveredLastInterval;
    private static string _auditLogPath = "audit.log";
    private static readonly object AuditLock = new();

    private static async Task Main(string[] args)
    {
        Console.WriteLine("TCP Chat Server starting...");

        var listener = new TcpListener(IPAddress.Any, Port);
        listener.Start();
        Console.WriteLine($"Listening on port {Port}.");

        var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            Console.WriteLine("Shutdown requested...");
            e.Cancel = true;
            cts.Cancel();
        };

        _auditLogPath = Path.Combine(AppContext.BaseDirectory, "audit.log");
        Console.WriteLine($"Audit log: {_auditLogPath}");

        _ = Task.Run(() => IdleTimeoutLoopAsync(cts.Token));
        _ = Task.Run(() => MetricsLoopAsync(cts.Token));

        try
        {
            while (!cts.IsCancellationRequested)
            {
                if (!listener.Pending())
                {
                    await Task.Delay(50, cts.Token);
                    continue;
                }

                var tcpClient = await listener.AcceptTcpClientAsync(cts.Token);
                tcpClient.NoDelay = true;

                var session = new ClientSession(tcpClient);
                Sessions[session.Id] = session;

                Console.WriteLine($"[INFO] New connection: {session.Id}");

                _ = Task.Run(() => HandleClientAsync(session, cts.Token));
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            listener.Stop();
            Console.WriteLine("Server stopped.");
        }
    }

    private static async Task HandleClientAsync(ClientSession session, CancellationToken serverToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        var ct = linkedCts.Token;

        using var networkStream = session.TcpClient.GetStream();
        using var reader = new StreamReader(networkStream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(networkStream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        // Start sender loop
        var sendTask = Task.Run(() => SendLoopAsync(session, writer, ct), ct);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null)
                {
                    // client closed connection
                    break;
                }

                session.Touch();
                await HandleIncomingJsonAsync(session, line, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore on shutdown
        }
        catch (IOException)
        {
            // network error, treat as disconnect
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Unexpected error in client {session.Id}: {ex}");
        }
        finally
        {
            linkedCts.Cancel();
            await sendTask.ContinueWith(_ => { });

            CloseSession(session);
        }
    }

    private static async Task HandleIncomingJsonAsync(ClientSession session, string jsonLine, CancellationToken ct)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(jsonLine);
        }
        catch (JsonException)
        {
            Console.WriteLine($"[WARN] Malformed JSON from {session.Username ?? session.Id.ToString()}, closing session.");
            CloseSession(session);
            return;
        }

        if (root is null || root["type"] is null)
        {
            Console.WriteLine($"[WARN] Missing type field, closing session.");
            CloseSession(session);
            return;
        }

        string? type = root["type"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(type))
        {
            Console.WriteLine($"[WARN] Empty type field, closing session.");
            CloseSession(session);
            return;
        }

        type = type.ToUpperInvariant();

        switch (type)
        {
            case "LOGIN_REQ":
                await HandleLoginAsync(session, root, ct);
                break;

            case "HEARTBEAT":
                // just update activity
                session.Touch();
                break;

            case "DM":
            case "MULTI":
            case "BROADCAST":
                if (session.Username == null)
                {
                    await EnqueueErrorAsync(session, "Not logged in", ct);
                    break;
                }
                await HandleChatMessageAsync(session, type, root, ct);
                break;

            default:
                await EnqueueErrorAsync(session, $"Unknown message type: {type}", ct);
                break;
        }
    }

    private static async Task HandleLoginAsync(ClientSession session, JsonNode root, CancellationToken ct)
    {
        var username = root["username"]?.GetValue<string>();
        var password = root["password"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            await EnqueueJsonAsync(session, new
            {
                type = "LOGIN_RESP",
                ok = false,
                reason = "Username or password missing"
            }, "system", username ?? "unknown", ct);
            return;
        }

        // Auth
        if (!Credentials.TryGetValue(username, out var storedPw) || storedPw != password)
        {
            await EnqueueJsonAsync(session, new
            {
                type = "LOGIN_RESP",
                ok = false,
                reason = "Invalid credentials"
            }, "system", username, ct);
            return;
        }

        // Already logged in?
        if (OnlineUsers.ContainsKey(username))
        {
            await EnqueueJsonAsync(session, new
            {
                type = "LOGIN_RESP",
                ok = false,
                reason = "User already logged in"
            }, "system", username, ct);
            return;
        }

        session.Username = username;
        session.Touch();
        OnlineUsers[username] = session;

        Console.WriteLine($"[INFO] User '{username}' logged in. Online: {OnlineUsers.Count}");

        await EnqueueJsonAsync(session, new
        {
            type = "LOGIN_RESP",
            ok = true
        }, "system", username, ct);

        // Notify others
        await BroadcastSystemAsync($"{username} joined the chat.", excludeUsername: username, ct);
    }

    private static async Task HandleChatMessageAsync(ClientSession fromSession, string type, JsonNode root, CancellationToken ct)
    {
        var fromUser = fromSession.Username!;
        switch (type)
        {
            case "DM":
            {
                var toUser = root["to"]?.GetValue<string>();
                var msg = root["msg"]?.GetValue<string>() ?? "";

                if (string.IsNullOrWhiteSpace(toUser))
                {
                    await EnqueueErrorAsync(fromSession, "DM requires 'to' field", ct);
                    return;
                }

                if (!OnlineUsers.TryGetValue(toUser, out var toSession))
                {
                    await EnqueueErrorAsync(fromSession, $"User '{toUser}' not online", ct);
                    return;
                }

                var payload = new
                {
                    type = "DM",
                    from = fromUser,
                    to = toUser,
                    msg
                };

                await EnqueueJsonAsync(toSession, payload, fromUser, toUser, ct);
                break;
            }

            case "MULTI":
            {
                var toArray = root["to"] as JsonArray;
                var msg = root["msg"]?.GetValue<string>() ?? "";

                if (toArray == null || toArray.Count == 0)
                {
                    await EnqueueErrorAsync(fromSession, "MULTI requires 'to' array", ct);
                    return;
                }

                foreach (var node in toArray)
                {
                    var toUser = node?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(toUser)) continue;

                    if (OnlineUsers.TryGetValue(toUser, out var toSession))
                    {
                        var payload = new
                        {
                            type = "MULTI",
                            from = fromUser,
                            to = toUser,
                            msg
                        };

                        await EnqueueJsonAsync(toSession, payload, fromUser, toUser, ct);
                    }
                    else
                    {
                        await EnqueueErrorAsync(fromSession, $"User '{toUser}' not online", ct);
                    }
                }

                break;
            }

            case "BROADCAST":
            {
                var msg = root["msg"]?.GetValue<string>() ?? "";
                var payload = new
                {
                    type = "BROADCAST",
                    from = fromUser,
                    msg
                };

                foreach (var kvp in OnlineUsers)
                {
                    var toUser = kvp.Key;
                    var toSession = kvp.Value;
                    if (ReferenceEquals(toSession, fromSession)) continue;

                    await EnqueueJsonAsync(toSession, payload, fromUser, toUser, ct);
                }

                break;
            }
        }
    }

    private static async Task SendLoopAsync(ClientSession session, StreamWriter writer, CancellationToken ct)
    {
        try
        {
            await foreach (var msg in session.SendQueue.Reader.ReadAllAsync(ct))
            {
                var json = msg.Json;
                var bytesCount = Encoding.UTF8.GetByteCount(json) + 1; // + '\n'

                await writer.WriteLineAsync(json);

                var now = DateTime.UtcNow;
                var latency = now - msg.EnqueuedUtc;

                Interlocked.Increment(ref _messagesDelivered);
                Interlocked.Increment(ref _messagesDeliveredLastInterval);

                WriteAudit(now, msg.From, msg.To, msg.Type, bytesCount, latency);
            }
        }
        catch (OperationCanceledException)
        {
            // normal on shutdown
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] SendLoop error for {session.Username ?? session.Id.ToString()}: {ex}");
        }
    }

    private static void WriteAudit(DateTime timestampUtc, string from, string to, string type, int bytes, TimeSpan latency)
    {
        var line =
            $"{timestampUtc:O}|from={from}|to={to}|type={type}|bytes={bytes}|latency_ms={(long)latency.TotalMilliseconds}{Environment.NewLine}";
        lock (AuditLock)
        {
            File.AppendAllText(_auditLogPath, line);
        }
    }

    private static async Task EnqueueJsonAsync(
        ClientSession session,
        object payload,
        string from,
        string to,
        CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload);
        var msg = new OutgoingMessage(json, DateTime.UtcNow, from, to, (payload as JsonObject)?["type"]?.ToString() ?? GetTypeFromJson(json));
        await session.SendQueue.Writer.WriteAsync(msg, ct);
    }

    private static string GetTypeFromJson(string json)
    {
        try
        {
            var node = JsonNode.Parse(json);
            return node?["type"]?.GetValue<string>() ?? "UNKNOWN";
        }
        catch
        {
            return "UNKNOWN";
        }
    }

    private static async Task EnqueueErrorAsync(ClientSession session, string reason, CancellationToken ct)
    {
        var payload = new
        {
            type = "ERROR",
            msg = reason
        };
        await EnqueueJsonAsync(session, payload, "system", session.Username ?? "unknown", ct);
    }

    private static async Task BroadcastSystemAsync(string message, string? excludeUsername, CancellationToken ct)
    {
        var payload = new
        {
            type = "SYSTEM",
            msg = message
        };

        foreach (var kvp in OnlineUsers)
        {
            var user = kvp.Key;
            if (excludeUsername != null && user.Equals(excludeUsername, StringComparison.OrdinalIgnoreCase))
                continue;

            var session = kvp.Value;
            await EnqueueJsonAsync(session, payload, "system", user, ct);
        }
    }

    private static void CloseSession(ClientSession session)
    {
        if (session.IsClosed) return;
        session.IsClosed = true;

        if (session.Username != null)
        {
            OnlineUsers.TryRemove(session.Username, out _);
            Console.WriteLine($"[INFO] User '{session.Username}' disconnected. Online: {OnlineUsers.Count}");
        }
        else
        {
            Console.WriteLine($"[INFO] Unauthenticated session {session.Id} disconnected.");
        }

        Sessions.TryRemove(session.Id, out _);

        try { session.SendQueue.Writer.TryComplete(); } catch { }

        try { session.TcpClient.Close(); } catch { }
    }

    private static async Task IdleTimeoutLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var kvp in Sessions)
                {
                    var session = kvp.Value;
                    if (session.IsClosed) continue;

                    var idleSeconds = (now - session.LastActivityUtc).TotalSeconds;
                    if (idleSeconds > IdleTimeoutSeconds)
                    {
                        Console.WriteLine($"[INFO] Session {session.Username ?? session.Id.ToString()} idle for {idleSeconds:F0}s, closing.");
                        CloseSession(session);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] IdleTimeoutLoop: {ex}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static async Task MetricsLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(MetricsIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var intervalMsgs = Interlocked.Exchange(ref _messagesDeliveredLastInterval, 0);
            var total = Interlocked.Read(ref _messagesDelivered);
            var online = OnlineUsers.Count;
            var mps = intervalMsgs / (double)MetricsIntervalSeconds;

            Console.WriteLine($"[METRICS] Online={online}, totalMsgs={total}, msgsPerSec≈{mps:F2}");
        }
    }

    private sealed class ClientSession
    {
        public Guid Id { get; } = Guid.NewGuid();
        public TcpClient TcpClient { get; }
        public string? Username { get; set; }
        public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;
        public Channel<OutgoingMessage> SendQueue { get; }
        public bool IsClosed { get; set; }

        public ClientSession(TcpClient client)
        {
            TcpClient = client;
            SendQueue = Channel.CreateUnbounded<OutgoingMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
        }

        public void Touch()
        {
            LastActivityUtc = DateTime.UtcNow;
        }
    }

    private sealed record OutgoingMessage(
        string Json,
        DateTime EnqueuedUtc,
        string From,
        string To,
        string Type);
}
