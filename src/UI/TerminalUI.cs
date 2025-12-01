using System.Buffers;
using System.Net;
using System.Text;
using System.Threading.Channels;
using FalconNode.Core.FS;
using FalconNode.Core.Messages;
using FalconNode.Core.Network;
using FalconNode.Workers;

namespace FalconNode.UI;

/// <summary>
/// Provides a terminal-based user interface for interacting with the node,
/// supporting commands for connecting to peers, uploading and fetching files,
/// sending store packets, and graceful shutdown.
/// </summary>
/// <remarks>
/// This class handles user input from the console, parses commands, and delegates
/// actions to the core node logic, file ingestion, and retrieval components.
/// </remarks>
public class TerminalUi
{
    /// <summary>
    /// The NodeLogicWorker instance for handling core node logic.
    /// </summary>
    private readonly NodeLogicWorker _logicWorker;

    /// <summary>
    /// The FileIngestor instance for ingesting files.
    /// </summary>
    private readonly FileIngestor _ingestor;

    /// <summary>
    /// The FileRetriever instance for retrieving files.
    /// </summary>
    private readonly FileRetriever _retriever;

    /// <summary>
    /// The channel writer for outgoing messages.
    /// </summary>
    private readonly ChannelWriter<OutgoingMessage> _outWriter;

    /// <summary>
    /// The application lifetime manager for graceful shutdown.
    /// </summary>
    private readonly IHostApplicationLifetime _lifetime;

    public TerminalUi(
        NodeLogicWorker logicWorker,
        FileIngestor ingestor,
        FileRetriever retriever,
        Channel<OutgoingMessage> outChannel,
        IHostApplicationLifetime lifetime
    )
    {
        _logicWorker = logicWorker;
        _ingestor = ingestor;
        _retriever = retriever;
        _outWriter = outChannel.Writer;
        _lifetime = lifetime;
    }

    public async Task RunAsync()
    {
        Console.WriteLine(
            "\n[Terminal UI Active] Type commands (connect, upload, fetch, send-store, exit):"
        );

        while (true)
        {
            Console.Write("> ");
            var input = Console.ReadLine();
            if (string.IsNullOrWhiteSpace(input))
            {
                continue;
            }

            var parts = input.Split(' ');
            var cmd = parts[0].ToLowerInvariant();

            try
            {
                switch (cmd)
                {
                    case "exit":
                        // Request graceful shutdown
                        _lifetime.StopApplication();
                        return;

                    case "connect":
                        await HandleConnect(parts);
                        break;

                    case "upload":
                        await HandleUpload(input);
                        break;

                    case "fetch":
                        await HandleFetch(parts);
                        break;

                    case "send-store":
                        await HandleSendStore(input, parts);
                        break;

                    default:
                        Console.WriteLine(
                            "Unknown command. (exit, connect, upload, fetch, send-store)"
                        );
                        break;
                }
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
            }
        }
    }

    private async Task HandleConnect(string[] parts)
    {
        if (parts.Length < 2)
        {
            Console.WriteLine("Usage: connect <port>");
            return;
        }

        int targetPort = int.Parse(parts[1]);
        var target = new IPEndPoint(IPAddress.Loopback, targetPort);

        var handShake = _logicWorker.CreateHandshakeMessage(target);
        await _outWriter.WriteAsync(handShake);

        Console.WriteLine($"[System] Sent handshake to {targetPort}.");
    }

    private async Task HandleUpload(string input)
    {
        // Extract text after "upload "
        if (input.Length < 7)
        {
            Console.WriteLine("Usage: upload <text>");
            return;
        }
        string text = input.Substring(7);

        using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(text)))
        {
            string hash = await _ingestor.IngestAsync(ms, "test.txt", "text/plain");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"Success: Hash (Manifest): {hash}");
            Console.ResetColor();
        }
    }

    private async Task HandleFetch(string[] parts)
    {
        if (parts.Length < 2)
        {
            Console.WriteLine("Usage: fetch <manifestHash>");
            return;
        }
        string hashToFetch = parts[1];

        using (var destMs = new MemoryStream())
        {
            await _retriever.ReassembleFileAsync(hashToFetch, destMs);

            string content = Encoding.UTF8.GetString(destMs.ToArray());
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"File Content: {content}");
            Console.ResetColor();
        }
    }

    private async Task HandleSendStore(string input, string[] parts)
    {
        if (parts.Length < 3)
        {
            Console.WriteLine("Usage: send-store <port> <text>");
            return;
        }

        int port = int.Parse(parts[1]);
        // Find start of text content
        string data = input.Substring(input.IndexOf(parts[2]));

        byte[] rawData = Encoding.UTF8.GetBytes(data);

        int packetSize = FixedHeader.Size + rawData.Length;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(packetSize);

        // Manually build the STORE packet (0x05)
        new FixedHeader(0x05, 123, (uint)rawData.Length).WriteToSpan(
            buffer.AsSpan(0, FixedHeader.Size)
        );
        rawData.CopyTo(buffer.AsSpan(FixedHeader.Size));

        // Fix: Enveloping the arguments into the OutgoingMessage object
        var outgoingMsg = new OutgoingMessage(
            new IPEndPoint(IPAddress.Loopback, port),
            buffer.AsMemory(0, packetSize),
            buffer // Pass reference to return to pool later
        );

        await _outWriter.WriteAsync(outgoingMsg);

        Console.WriteLine($"STORE Packet sent to port {port}.");
    }
}
