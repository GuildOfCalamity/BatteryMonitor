using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Streams;
using static System.Runtime.InteropServices.JavaScript.JSType;

namespace BatteryMonitor;

public static class SpanHelper
{
    public static Span<byte> AsSpan(this byte[] source)
    {
        if (source == null || source.Length == 0)
            return Span<byte>.Empty;

        return source.AsSpan();
    }

    public static byte[] FromSpan(this Span<byte> span)
    {
        if (span.IsEmpty)
            return Array.Empty<byte>();

        return span.ToArray();
    }

    public static ReadOnlySpan<char> AsSpan(this string text)
    {
        if (string.IsNullOrEmpty(text))
            return ReadOnlySpan<char>.Empty;

        return text.AsSpan();
    }

    public static string FromSpan(this ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
            return string.Empty;

        return span.ToString();
    }

    public static Span<byte> StringToSpan(this string text, Encoding? encoding = null)
    {
        if (string.IsNullOrEmpty(text))
            return Span<byte>.Empty;

        encoding ??= Encoding.UTF8;
        byte[] buffer = encoding.GetBytes(text);
        return buffer.AsSpan();
    }

    public static string SpanToString(this ReadOnlySpan<byte> span, Encoding? encoding = null)
    {
        if (span.IsEmpty)
            return string.Empty;

        encoding ??= Encoding.UTF8;
        return encoding.GetString(span);
    }

    public static byte[] CharSpanToBytes(this ReadOnlySpan<char> span, Encoding? encoding = null)
    {
        if (span.IsEmpty)
            return Array.Empty<byte>();

        encoding ??= Encoding.UTF8;
        return encoding.GetBytes(span.ToArray());
    }

    public static string BytesToString(this byte[] bytes, Encoding? encoding = null)
    {
        if (bytes == null || bytes.Length == 0)
            return string.Empty;

        encoding ??= Encoding.UTF8;
        return encoding.GetString(bytes);
    }

    public static Stream SpanToStream(this ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
            return new MemoryStream(Array.Empty<byte>(), writable: false);

        return new MemoryStream(span.ToArray(), writable: false);
    }

    public static async Task<byte[]> StreamToBytesAsync(this Stream stream)
    {
        if (stream == null || !stream.CanRead)
            throw new ArgumentException($"'{nameof(stream)}' is null or unreadable");

        return await StreamToByteArrayAsync(stream);
    }

    public static async Task<Memory<byte>> StreamToMemoryAsync(this Stream stream)
    {
        if (stream == null || !stream.CanRead)
            throw new ArgumentException($"'{nameof(stream)}' is null or unreadable");

        byte[] buffer = await StreamToByteArrayAsync(stream);
        return new Memory<byte>(buffer);
    }

    public static async Task<byte[]> StreamToByteArrayAsync(this Stream stream, int bufferSize = 4096)
    {
        if (stream == null || !stream.CanRead)
            throw new ArgumentException($"'{nameof(stream)}' is null or unreadable");

        byte[] buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
        try
        {
            using var ms = new MemoryStream();
            int bytesRead;
            while ((bytesRead = await stream.ReadAsync(buffer, 0, bufferSize)) > 0)
            {
                ms.Write(buffer, 0, bytesRead);
            }
            return ms.ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static IBuffer SpanToIBuffer(this ReadOnlySpan<byte> span)
    {
        if (span.IsEmpty)
            return WindowsRuntimeBuffer.Create(0);

        var writer = new DataWriter();
        writer.WriteBytes(span.ToArray()); // span must be copied to byte[]
        return writer.DetachBuffer();
    }

    public static Span<byte> IBufferToSpan(this IBuffer buffer)
    {
        if (buffer == null || buffer.Length == 0)
            return Span<byte>.Empty;

        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return bytes.AsSpan();
    }

    public static Memory<byte> IBufferToMemory(this IBuffer buffer)
    {
        if (buffer == null || buffer.Length == 0)
            return Memory<byte>.Empty;

        var bytes = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(bytes);
        return new Memory<byte>(bytes);
    }
}
