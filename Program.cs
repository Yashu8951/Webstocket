using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using WebApplication1.Data;
using WebApplication1.Models;

var builder = WebApplication.CreateBuilder(args);

/// Railway PORT
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

/// Database connection (Railway)
var connection = Environment.GetEnvironmentVariable("DATABASE_URL");

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(connection));

var app = builder.Build();

app.UseWebSockets();

app.MapGet("/", () => "WebSocket server running");

app.Map("/ws", async (HttpContext context, AppDbContext db) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
        return;

    var socket = await context.WebSockets.AcceptWebSocketAsync();

    Console.WriteLine("Client Connected");

    /// Send all items when client connects
    var items = await db.Kiran.ToListAsync();

    var json = JsonSerializer.Serialize(items);

    await socket.SendAsync(
        Encoding.UTF8.GetBytes(json),
        WebSocketMessageType.Text,
        true,
        CancellationToken.None
    );

    var buffer = new byte[1024];

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

        /// Update device
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

        /// Send updated list
        var updated = await db.Kiran.ToListAsync();

        var updatedJson = JsonSerializer.Serialize(updated);

        await socket.SendAsync(
            Encoding.UTF8.GetBytes(updatedJson),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None
        );
    }

    Console.WriteLine("Client Disconnected");
});

app.Run();