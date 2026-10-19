using System.Buffers;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Overglow.Server.Rpc;

/// <summary>Serialization of protocol messages.</summary>
public static class RpcJson
{
    // Messages only go to our own clients over JSON transports, so non-ASCII text can stay readable.
    private static readonly JsonWriterOptions s_writerOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static byte[] ToUtf8(JsonNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer, s_writerOptions))
        {
            node.WriteTo(writer);
        }

        return buffer.WrittenSpan.ToArray();
    }
}
