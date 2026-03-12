using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApplication1.Data;
using WebApplication1.Models;
using System.Collections.Concurrent;

var builder = WebApplication.CreateBuilder(args);

/// Railway PORT
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

/// Database connection (Railway PostgreSQL)
var connection = Environment.GetEnvironmentVariable("DATABASE_URL");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connection));

var app = builder.Build();

/// Enable WebSockets
app.UseWebSockets(new WebSocketOptions
{
    KeepAliveInterval = TimeSpan.FromSeconds(30)
});

/// Health check
app.MapGet("/", () => "WebSocket server running");

/// Connected clients
ConcurrentDictionary<Guid, WebSocket> clients = new();

app.Map("/ws", async (HttpContext context, AppDbContext db) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsync("WebSocket only");
        return;
    }

    var socket = await context.WebSockets.AcceptWebSocketAsync();

    var clientId = Guid.NewGuid();
    clients.TryAdd(clientId, socket);

    Console.WriteLine($"Client connected: {clientId}");

    await SendItems(socket, db);

    var buffer = new byte[1024];

    while (socket.State == WebSocketState.Open)
    {
        var result = await socket.ReceiveAsync(buffer, CancellationToken.None);

        if (result.MessageType == WebSocketMessageType.Close)
        {
            clients.TryRemove(clientId, out _);

            await socket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Closed",
                CancellationToken.None);

            Console.WriteLine($"Client disconnected: {clientId}");
            break;
        }

        if (result.Count == 0)
            continue;

        var message = Encoding.UTF8.GetString(buffer, 0, result.Count);

        Console.WriteLine("Received: " + message);

        bool changed = false;

        try
        {
            var doc = JsonDocument.Parse(message);

            /// Handle all_on / all_off
            if (doc.RootElement.TryGetProperty("action", out var action))
            {
                var act = action.GetString();

                var items = await db.Kiran.ToListAsync();

                if (act == "all_on")
                {
                    foreach (var i in items)
                        i.Status = 1;

                    changed = true;
                }

                if (act == "all_off")
                {
                    foreach (var i in items)
                        i.Status = 0;

                    changed = true;
                }

                await db.SaveChangesAsync();
            }
            else
            {
                /// Handle single device update
                var data = JsonSerializer.Deserialize<Kiran>(message);

                if (data != null)
                {
                    var item = await db.Kiran
                        .FirstOrDefaultAsync(x => x.ItemId == data.ItemId);

                    if (item != null)
                    {
                        item.Status = data.Status;
                        await db.SaveChangesAsync();
                        changed = true;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("Error: " + ex.Message);
        }

        if (changed)
            await Broadcast(db);
    }
});

app.Run();


/// Send devices to new client
async Task SendItems(WebSocket socket, AppDbContext db)
{
    var items = await db.Kiran.ToListAsync();

    var json = JsonSerializer.Serialize(items);

    var bytes = Encoding.UTF8.GetBytes(json);

    await socket.SendAsync(
        bytes,
        WebSocketMessageType.Text,
        true,
        CancellationToken.None);
}


/// Broadcast updates to all clients
async Task Broadcast(AppDbContext db)
{
    var items = await db.Kiran.ToListAsync();

    var json = JsonSerializer.Serialize(items);

    var bytes = Encoding.UTF8.GetBytes(json);

    foreach (var client in clients.Values)
    {
        if (client.State == WebSocketState.Open)
        {
            await client.SendAsync(
                bytes,
                WebSocketMessageType.Text,
                true,
                CancellationToken.None);
        }
    }
}