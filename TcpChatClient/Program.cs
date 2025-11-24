using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace TcpChatClient;

internal class Program
{
    private const string DefaultHost = "127.0.0.1";
    private const int DefaultPort = 5000;
    private const int HeartbeatIntervalSeconds = 30;

    private static async Task Main(string[] args)
    {
        Console.WriteLine("=== TCP Chat Client ===");

        Console.Write($"Server host [{DefaultHost}]: ");
        var hostInput = Console.ReadLine();
        var host = string.IsNullOrWhiteSpace(hostInput) ? DefaultHost : hostInput.Trim();

        Console.Write($"Server port [{DefaultPort}]: ");
        var portInput = Console.ReadLine();
        var port = DefaultPort;
        if (!string.IsNullOrWhiteSpace(portInput) && int.TryParse(portInput, out var p))
        {
            port = p;
        }

        Console.Write("Username: ");
        var username = Console.ReadLine()?.Trim() ?? "";
        Console.Write("Password: ");
        var password = ReadPassword();

        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
        {
            Console.WriteLine("Username and password are required.");
            return;
        }

        Console.WriteLine($"Connecting to {host}:{port}...");

        using var tcpClient = new TcpClient();
        try
        {
            await tcpClient.ConnectAsync(host, port);
        }
        catch (SocketException ex)
        {
            Console.WriteLine($"Failed to connect to {host}:{port} - {ex.Message}");
            return;
        }

        tcpClient.NoDelay = true;

        using var networkStream = tcpClient.GetStream();
        using var reader = new StreamReader(networkStream, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(networkStream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };

        // 1) LOGIN HANDSHAKE (no extra read loops here)
        var loginReq = new
        {
            type = "LOGIN_REQ",
            username,
            password
        };
        await SendJsonAsync(writer, loginReq);

        Console.WriteLine("Waiting for login response...");

        string? firstLine;
        try
        {
            firstLine = await reader.ReadLineAsync();
        }
        catch
        {
            Console.WriteLine("Disconnected before login response.");
            return;
        }

        if (firstLine == null)
        {
            Console.WriteLine("Server closed connection before login response.");
            return;
        }

        if (!HandleLoginResponse(firstLine, out var loginOk))
        {
            return;
        }

        if (!loginOk)
        {
            return;
        }

        Console.WriteLine("Logged in successfully!");
        PrintHelp();

        // 2) AFTER LOGIN: start background receive + heartbeat
        var cts = new CancellationTokenSource();
        var ct = cts.Token;

        var receiveTask = Task.Run(() => ReceiveLoopAsync(reader, ct), ct);
        var heartbeatTask = Task.Run(() => HeartbeatLoopAsync(writer, ct), ct);

        // 3) REPL loop for user commands
        while (!ct.IsCancellationRequested)
        {
            Console.Write("> ");
            var line = Console.ReadLine();
            if (line == null)
            {
                break;
            }

            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.Equals("/quit", StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            if (line.Equals("/help", StringComparison.OrdinalIgnoreCase))
            {
                PrintHelp();
                continue;
            }

            if (line.StartsWith("/dm ", StringComparison.OrdinalIgnoreCase))
            {
                // /dm bob hello bob
                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    Console.WriteLine("Usage: /dm <user> <message>");
                    continue;
                }

                var to = parts[1];
                var msg = parts[2];
                var payload = new
                {
                    type = "DM",
                    to,
                    msg
                };
                await SendJsonAsync(writer, payload);
                continue;
            }

            if (line.StartsWith("/multi ", StringComparison.OrdinalIgnoreCase))
            {
                // /multi bob,alice hi there
                var parts = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3)
                {
                    Console.WriteLine("Usage: /multi <u1,u2,...> <message>");
                    continue;
                }

                var users = parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var msg = parts[2];

                var payload = new
                {
                    type = "MULTI",
                    to = users,
                    msg
                };
                await SendJsonAsync(writer, payload);
                continue;
            }

            if (line.StartsWith("/broadcast ", StringComparison.OrdinalIgnoreCase))
            {
                // /broadcast hello all
                var msg = line.Substring("/broadcast ".Length);
                var payload = new
                {
                    type = "BROADCAST",
                    msg
                };
                await SendJsonAsync(writer, payload);
                continue;
            }

            Console.WriteLine("Unknown command. Type /help for commands.");
        }

        Console.WriteLine("Exiting client...");
        cts.Cancel();
        await Task.WhenAll(
            receiveTask.ContinueWith(_ => { }),
            heartbeatTask.ContinueWith(_ => { })
        );
    }

    // ---------- Networking helpers ----------

    private static async Task SendJsonAsync(StreamWriter writer, object payload, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(payload);
        if (ct.IsCancellationRequested) return;
        await writer.WriteLineAsync(json);
    }

    private static async Task ReceiveLoopAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null)
                {
                    Console.WriteLine("\n[INFO] Disconnected from server.");
                    break;
                }

                HandleIncoming(line);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (IOException)
        {
            Console.WriteLine("\n[INFO] Connection closed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[ERROR] ReceiveLoop: {ex.Message}");
        }
    }

    private static void HandleIncoming(string jsonLine)
    {
        try
        {
            var node = JsonNode.Parse(jsonLine);
            if (node == null) return;

            var type = node["type"]?.GetValue<string>() ?? "UNKNOWN";
            var now = DateTime.Now.ToString("HH:mm:ss");

            switch (type)
            {
                case "DM":
                {
                    var from = node["from"]?.GetValue<string>() ?? "?";
                    var msg = node["msg"]?.GetValue<string>() ?? "";
                    Console.WriteLine($"\n[{now}] [DM] {from}: {msg}");
                    break;
                }

                case "MULTI":
                {
                    var from = node["from"]?.GetValue<string>() ?? "?";
                    var to = node["to"]?.GetValue<string>() ?? "?";
                    var msg = node["msg"]?.GetValue<string>() ?? "";
                    Console.WriteLine($"\n[{now}] [MULTI] {from} -> {to}: {msg}");
                    break;
                }

                case "BROADCAST":
                {
                    var from = node["from"]?.GetValue<string>() ?? "?";
                    var msg = node["msg"]?.GetValue<string>() ?? "";
                    Console.WriteLine($"\n[{now}] [BROADCAST] {from}: {msg}");
                    break;
                }

                case "SYSTEM":
                {
                    var msg = node["msg"]?.GetValue<string>() ?? "";
                    Console.WriteLine($"\n[{now}] [SYSTEM] {msg}");
                    break;
                }

                case "ERROR":
                {
                    var msg = node["msg"]?.GetValue<string>() ?? "";
                    Console.WriteLine($"\n[{now}] [ERROR] {msg}");
                    break;
                }

                case "LOGIN_RESP":
                    // Should not normally appear here (we handle first LOGIN_RESP in Main).
                    break;

                default:
                    Console.WriteLine($"\n[{now}] [RAW] {jsonLine}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n[ERROR] Failed to parse incoming message: {ex.Message}");
        }

        Console.Write("> ");
    }

    private static bool HandleLoginResponse(string jsonLine, out bool ok)
    {
        ok = false;
        try
        {
            var node = JsonNode.Parse(jsonLine);
            if (node == null)
            {
                Console.WriteLine("Invalid LOGIN_RESP from server.");
                return false;
            }

            var type = node["type"]?.GetValue<string>();
            if (!string.Equals(type, "LOGIN_RESP", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"Unexpected first message from server: {jsonLine}");
                return false;
            }

            ok = node["ok"]?.GetValue<bool>() ?? false;
            if (!ok)
            {
                var reason = node["reason"]?.GetValue<string>() ?? "Unknown reason";
                Console.WriteLine($"Login failed: {reason}");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error parsing LOGIN_RESP: {ex.Message}");
            return false;
        }
    }

    private static async Task HeartbeatLoopAsync(StreamWriter writer, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(HeartbeatIntervalSeconds), ct);
                var payload = new
                {
                    type = "HEARTBEAT"
                };
                await SendJsonAsync(writer, payload, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] HeartbeatLoop: {ex.Message}");
        }
    }

    // ---------- UI helpers ----------

    private static void PrintHelp()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  /dm <user> <message>          - Send direct message");
        Console.WriteLine("  /multi <u1,u2,...> <message>  - Send message to multiple users");
        Console.WriteLine("  /broadcast <message>          - Send broadcast message");
        Console.WriteLine("  /help                         - Show this help");
        Console.WriteLine("  /quit                         - Quit the client");
    }

    private static string ReadPassword()
    {
        var sb = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (sb.Length > 0)
                {
                    sb.Length--;
                    Console.Write("\b \b");
                }
            }
            else
            {
                sb.Append(key.KeyChar);
                Console.Write("*");
            }
        }

        return sb.ToString();
    }
}
