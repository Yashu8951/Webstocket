
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApplication1.Data;
using WebApplication1.Models;

var builder = WebApplication.CreateBuilder(args);

/// Railway dynamic port
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

/// PostgreSQL URL from Railway
var databaseUrl =
    Environment.GetEnvironmentVariable("DATABASE_URL") ??
    Environment.GetEnvironmentVariable("DATABASE_PUBLIC_URL") ??
    Environment.GetEnvironmentVariable("DATABASE_PRIVATE_URL");

if (string.IsNullOrEmpty(databaseUrl))
{
    throw new Exception("DATABASE_URL not found");
}

/// Convert Railway URL → Npgsql format
databaseUrl = databaseUrl.Replace("postgresql://", "postgres://");

var uri = new Uri(databaseUrl);
var userInfo = uri.UserInfo.Split(':');

var connectionString =
    $"Host={uri.Host};" +
    $"Port={uri.Port};" +
    $"Database={uri.AbsolutePath.TrimStart('/')};" +
    $"Username={userInfo[0]};" +
    $"Password={userInfo[1]};" +
    $"SSL Mode=Require;Trust Server Certificate=true";

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

var app = builder.Build();

/// WebSocket clients list
var clients = new List<WebSocket>();

app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(120)
});

app.MapGet("/", () => "WebSocket server running");

app.Map("/ws", async (HttpContext context, IServiceScopeFactory scopeFactory) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();

    clients.Add(socket);

    Console.WriteLine("Client connected");

    var buffer = new byte[4096];

    using var scope = scopeFactory.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    try
    {
        /// Send initial device list
        var items = await db.Kiran.ToListAsync();
        var json = JsonSerializer.Serialize(items);

        await socket.SendAsync(
            Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );

        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "Closed",
                    CancellationToken.None
                );
                break;
            }

            var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

            Console.WriteLine("Received: " + message);

            try
            {
                var doc = JsonDocument.Parse(message);

                /// CONNECT ALL / DISCONNECT ALL
                if (doc.RootElement.TryGetProperty("action", out var actionProp))
                {
                    var action = actionProp.GetString();

                    if (action == "all_on")
                    {
                        var devices = await db.Kiran.ToListAsync();

                        foreach (var d in devices)
                        {
                            d.Status = 1;
                        }

                        await db.SaveChangesAsync();
                    }

                    else if (action == "all_off")
                    {
                        var devices = await db.Kiran.ToListAsync();

                        foreach (var d in devices)
                        {
                            d.Status = 0;
                        }

                        await db.SaveChangesAsync();
                    }
                }

                /// SINGLE DEVICE TOGGLE
                else
                {
                    var data = JsonSerializer.Deserialize<Kiran>(message);

                    if (data != null)
                    {
                        var item = await db.Kiran
                            .FirstOrDefaultAsync(x => x.ItemId == data.ItemId);

                        if (item != null)
                        {
                            item.Status = data.Status;
                            await db.SaveChangesAsync();
                        }
                    }
                }

                /// GET UPDATED DATA
                var updated = await db.Kiran.ToListAsync();
                var updatedJson = JsonSerializer.Serialize(updated);
                var bytes = Encoding.UTF8.GetBytes(updatedJson);

                /// BROADCAST TO ALL CLIENTS
                foreach (var client in clients.ToList())
                {
                    if (client.State == WebSocketState.Open)
                    {
                        await client.SendAsync(
                            bytes,
                            WebSocketMessageType.Text,
                            true,
                            CancellationToken.None
                        );
                    }
                }
            }
            catch (Exception e)
            {
                Console.WriteLine("JSON Error: " + e.Message);
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine("WebSocket Error: " + ex.Message);
    }

    Console.WriteLine("Client disconnected");

    clients.Remove(socket);
});

app.Run();

