using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Windows.Storage.Streams;

namespace BatteryMonitor;

/// <summary>
/// Production ready for serial, socket, or file streams.
/// IBuffer buffer = await StreamIOHelper.ReadBufferAsync(inputStream, 512, timeoutMs: 3000);
/// string result = await StreamIOHelper.ReadStringAsync(stream, 1024, timeoutMs: 3000);
/// bool success = await StreamIOHelper.WriteStringAsync(stream, "PING", timeoutMs: 2000);
/// </summary>
public static class StreamIOHelper
{
    public static InputStreamOptions StreamOptions { get; set; } = InputStreamOptions.Partial;

    #region [String Methods]
    public static async Task<string> ReadStringAsync(this IInputStream inputStream, uint bufferLength = 1024)
    {
        using var reader = new DataReader(inputStream)
        {
            InputStreamOptions = StreamOptions
        };

        uint bytesLoaded = await reader.LoadAsync(bufferLength);
        return bytesLoaded > 0 ? reader.ReadString(bytesLoaded) : string.Empty;
    }

    public static async Task<string> ReadStringAsync(this IInputStream inputStream, uint bufferLength = 1024, int timeoutMs = 4000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var reader = new DataReader(inputStream)
        {
            InputStreamOptions = StreamOptions
        };

        try
        {
            uint bytesLoaded = await reader.LoadAsync(bufferLength).AsTask(cts.Token);
            return bytesLoaded > 0 ? reader.ReadString(bytesLoaded) : string.Empty;
        }
        catch (TaskCanceledException)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name}: Operation was canceled or timed out.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name}: {ex.Message}");
        }
        return string.Empty;
    }

    public static async Task<bool> WriteStringAsync(this IOutputStream outputStream, string text, bool autoFlush = true)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        using var writer = new DataWriter(outputStream);
        writer.WriteString(text);
        await writer.StoreAsync();
        if (autoFlush)
            await writer.FlushAsync();

        return true;
    }

    public static async Task<bool> WriteStringAsync(this IOutputStream outputStream, string text, bool autoFlush = true, int timeoutMs = 4000)
    {
        if (string.IsNullOrEmpty(text)) return false;

        using var cts = new CancellationTokenSource(timeoutMs);
        using var writer = new DataWriter(outputStream);
        writer.WriteString(text);

        try
        {
            await writer.StoreAsync().AsTask(cts.Token);
            if (autoFlush)
                await writer.FlushAsync().AsTask(cts.Token);
            return true;
        }
        catch (TaskCanceledException) 
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name}: Operation was canceled or timed out.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name}: {ex.Message}");
        }
        return false;

    }
    #endregion

    #region [Byte Methods]
    public static async Task<byte[]> ReadBytesAsync(this IInputStream inputStream, uint bufferLength = 1024)
    {
        using var reader = new DataReader(inputStream)
        {
            InputStreamOptions = StreamOptions
        };

        uint bytesLoaded = await reader.LoadAsync(bufferLength);
        if (bytesLoaded == 0)
            return Array.Empty<byte>();

        byte[] buffer = new byte[bytesLoaded];
        reader.ReadBytes(buffer);
        return buffer;
    }

    public static async Task<byte[]> ReadBytesAsync(this IInputStream inputStream, uint bufferLength = 1024, int timeoutMs = 4000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var reader = new DataReader(inputStream)
        {
            InputStreamOptions = StreamOptions
        };

        try
        {
            uint bytesLoaded = await reader.LoadAsync(bufferLength).AsTask(cts.Token);
            if (bytesLoaded == 0) return Array.Empty<byte>();

            byte[] buffer = new byte[bytesLoaded];
            reader.ReadBytes(buffer);
            return buffer;
        }
        catch (TaskCanceledException)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name}: Operation was canceled or timed out.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name}: {ex.Message}");
        }
        return Array.Empty<byte>();

    }

    public static async Task<bool> WriteBytesAsync(this IOutputStream outputStream, byte[] data, bool autoFlush = true)
    {
        if (data is null || data.Length == 0)
            return false;

        using var writer = new DataWriter(outputStream);
        writer.WriteBytes(data);
        await writer.StoreAsync();
        if (autoFlush)
            await writer.FlushAsync();

        return true;
    }

    public static async Task<bool> WriteBytesAsync(this IOutputStream outputStream, byte[] data, bool autoFlush = true, int timeoutMs = 4000)
    {
        if (data is null || data.Length == 0) 
            return false;

        using var cts = new CancellationTokenSource(timeoutMs);
        using var writer = new DataWriter(outputStream);
        writer.WriteBytes(data);
        try
        {
            await writer.StoreAsync().AsTask(cts.Token);
            if (autoFlush)
                await writer.FlushAsync().AsTask(cts.Token);
            return true;
        }
        catch (TaskCanceledException)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name}: Operation was canceled or timed out.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name}: {ex.Message}");
        }
        return false;
    }
    #endregion

    #region [IBuffer Methods]
    public static async Task<IBuffer> ReadBufferAsync(this IInputStream inputStream, uint bufferLength = 1024)
    {
        using var reader = new DataReader(inputStream)
        {
            InputStreamOptions = StreamOptions
        };

        uint bytesLoaded = await reader.LoadAsync(bufferLength);

        if (bytesLoaded == 0)
            return WindowsRuntimeBuffer.Create(0);

        var buffer = reader.ReadBuffer(bytesLoaded);
        if (buffer is not null)
            return buffer;

        return WindowsRuntimeBuffer.Create(0); // Return empty buffer
    }

    public static async Task<IBuffer> ReadBufferAsync(this IInputStream inputStream, uint bufferLength = 1024, int timeoutMs = 4000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var reader = new DataReader(inputStream)
        {
            InputStreamOptions = StreamOptions
        };

        try
        {
            uint bytesLoaded = await reader.LoadAsync(bufferLength).AsTask(cts.Token);
            return bytesLoaded > 0 ? reader.ReadBuffer(bytesLoaded) : WindowsRuntimeBuffer.Create(0);
        }
        catch (TaskCanceledException)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name}: Operation was canceled or timed out.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name}: {ex.Message}");
        }
        return WindowsRuntimeBuffer.Create(0); // Return empty buffer

    }

    public static async Task<bool> WriteBufferAsync(this IOutputStream outputStream, IBuffer buffer, bool autoFlush = true)
    {
        if (buffer is null || buffer.Length == 0) 
            return false;

        using var writer = new DataWriter(outputStream);
        writer.WriteBuffer(buffer);
        await writer.StoreAsync();
        if (autoFlush)
            await writer.FlushAsync();

        return true;
    }

    public static void CopyByteArrayToBuffer(this byte[] sourceArray, IBuffer destinationBuffer)
    {
        if (sourceArray == null)
            throw new ArgumentNullException(nameof(sourceArray));
        if (destinationBuffer == null)
            throw new ArgumentNullException(nameof(destinationBuffer));
        if (sourceArray.Length > destinationBuffer.Capacity)
            throw new ArgumentException("Source array is larger than the destination buffer's capacity.");

        try
        {
            // Get the IBuffer's underlying byte array (unsafe)
            using (var buffer = destinationBuffer.AsStream())
            {
                buffer.Write(sourceArray, 0, sourceArray.Length);
            }

            // Set the buffer's length to the copied data's length
            destinationBuffer.Length = (uint)sourceArray.Length;
        }
        catch (Exception ex)
        {
            // Handle any exceptions that might occur during the copy operation
            App.DebugLog($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name}: {ex.Message}");
            throw; // Re-throw the exception to signal the caller
        }
    }

    public static void CopyBufferToByteArray(this IBuffer sourceBuffer, byte[] destinationArray)
    {
        if (sourceBuffer == null)
            throw new ArgumentNullException(nameof(sourceBuffer));
        if (destinationArray == null)
            throw new ArgumentNullException(nameof(destinationArray));
        if (destinationArray.Length < sourceBuffer.Length)
            throw new ArgumentException("Destination array is smaller than the source buffer's length.");

        try
        {
            // Create a DataReader from the source buffer
            using (var reader = DataReader.FromBuffer(sourceBuffer))
            {
                // Read the bytes from the buffer into the destination array
                reader.ReadBytes(destinationArray);
            }
        }
        catch (Exception ex)
        {
            // Handle any exceptions that might occur during the copy operation
            App.DebugLog($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name}: {ex.Message}");
            throw; // Re-throw the exception to signal the caller
        }
    }

    #endregion

    #region [IRandomAccessStream Methods]
    public static async Task<InMemoryRandomAccessStream> CopyInputStreamToRandomAccessStream(this Stream stream)
    {
        var randomAccessStream = new InMemoryRandomAccessStream();
        await RandomAccessStream.CopyAsync(stream.AsInputStream(), randomAccessStream);
        randomAccessStream.Seek(0); // reset to beginning
        return randomAccessStream;
    }

    public static async Task<IBuffer> RandomAccessStreamToBufferAsync(this IRandomAccessStream stream, int timeoutMs = 4000)
    {
        if (stream == null || stream.Size == 0)
            return WindowsRuntimeBuffer.Create(0);

        stream.Seek(0); // Ensure start of stream
        return await ReadBufferAsync(stream, (uint)stream.Size, timeoutMs);
    }

    public static async Task<IRandomAccessStream> BufferToRandomAccessStreamAsync(this IBuffer buffer)
    {
        var stream = new InMemoryRandomAccessStream();
        using var writer = new DataWriter(stream);
        writer.WriteBuffer(buffer);
        await writer.StoreAsync();
        await writer.FlushAsync();
        stream.Seek(0); // Ensure start of stream
        return stream;
    }

    public static async Task<string> ReadStringAsync(this IRandomAccessStream stream, Encoding? encoding = null, uint bufferLength = 1024, int timeoutMs = 4000)
    {
        encoding ??= Encoding.UTF8;
        using var cts = new CancellationTokenSource(timeoutMs);
        using var reader = new DataReader(stream.GetInputStreamAt(0))
        {
            InputStreamOptions = StreamOptions
        };

        try
        {
            uint bytesLoaded = await reader.LoadAsync(bufferLength).AsTask(cts.Token);
            var buffer = new byte[bytesLoaded];
            reader.ReadBytes(buffer);
            return encoding.GetString(buffer);
        }
        catch (TaskCanceledException)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name} Operation was canceled or timed out.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name} {ex.Message}");
        }
        return string.Empty;
    }

    public static async Task<byte[]> ReadBytesAsync(this IRandomAccessStream stream, uint bufferLength = 1024, int timeoutMs = 4000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var reader = new DataReader(stream.GetInputStreamAt(0))
        {
            InputStreamOptions = StreamOptions
        };

        try
        {
            uint bytesLoaded = await reader.LoadAsync(bufferLength).AsTask(cts.Token);
            var buffer = new byte[bytesLoaded];
            reader.ReadBytes(buffer);
            return buffer;
        }
        catch (TaskCanceledException)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name} Operation was canceled or timed out.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name} {ex.Message}");
        }
        return Array.Empty<byte>();
    }

    public static async Task<bool> WriteStringAsync(this IRandomAccessStream stream, string text, Encoding? encoding = null, int timeoutMs = 4000)
    {
        if (string.IsNullOrEmpty(text)) return false;
        encoding ??= Encoding.UTF8;

        var bytes = encoding.GetBytes(text);
        return await WriteBytesAsync(stream, bytes, timeoutMs);
    }

    public static async Task<bool> WriteBytesAsync(this IRandomAccessStream stream, byte[] data, int timeoutMs = 4000)
    {
        if (data == null || data.Length == 0) return false;

        using var cts = new CancellationTokenSource(timeoutMs);
        using var writer = new DataWriter(stream.GetOutputStreamAt(0));
        writer.WriteBytes(data);

        try
        {
            await writer.StoreAsync().AsTask(cts.Token);
            await writer.FlushAsync().AsTask(cts.Token);
            return true;
        }
        catch (TaskCanceledException)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name}: Operation was canceled or timed out.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name} {ex.Message}");
        }
        return false;
    }

    public static async Task<IBuffer> ReadBufferAsync(this IRandomAccessStream stream, uint bufferLength = 1024, int timeoutMs = 4000)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var reader = new DataReader(stream.GetInputStreamAt(0))
        {
            InputStreamOptions = StreamOptions
        };

        try
        {
            uint bytesLoaded = await reader.LoadAsync(bufferLength).AsTask(cts.Token);
            return bytesLoaded > 0 ? reader.ReadBuffer(bytesLoaded) : WindowsRuntimeBuffer.Create(0);
        }
        catch (TaskCanceledException)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name} Operation was canceled or timed out.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"{System.Reflection.MethodBase.GetCurrentMethod()?.Name} {ex.Message}");
        }
        return WindowsRuntimeBuffer.Create(0);
    }

    public static InMemoryRandomAccessStream CreateRandomAccessStreamFromBytes(this byte[] data)
    {
        var stream = new InMemoryRandomAccessStream();
        _ = WriteBytesAsync(stream, data).Result;
        return stream;
    }

    public static InMemoryRandomAccessStream CreateRandomAccessStreamFromString(this string text, Encoding? encoding = null)
    {
        encoding ??= Encoding.UTF8;
        var stream = new InMemoryRandomAccessStream();
        _ = WriteStringAsync(stream, text, encoding).Result;
        return stream;
    }

    public static async Task<byte[]> ReadAllBytesAsync(this InMemoryRandomAccessStream stream, int timeoutMs = 4000)
    {
        return await ReadBytesAsync(stream, (uint)stream.Size, timeoutMs);
    }

    public static async Task<string> ReadAllTextAsync(this InMemoryRandomAccessStream stream, Encoding? encoding = null, int timeoutMs = 4000)
    {
        return await ReadStringAsync(stream, encoding, (uint)stream.Size, timeoutMs);
    }

    public static async Task<bool> AppendBytesAsync(this InMemoryRandomAccessStream stream, byte[] data, int timeoutMs = 4000)
    {
        stream.Seek(stream.Size); // Move to end
        return await WriteBytesAsync(stream, data, timeoutMs);
    }

    public static async Task<bool> AppendStringAsync(this InMemoryRandomAccessStream stream, string text, Encoding? encoding = null, int timeoutMs = 4000)
    {
        stream.Seek(stream.Size); // Move to end
        return await WriteStringAsync(stream, text, encoding, timeoutMs);
    }
    #endregion

    #region [Miscellaneous]
    /// <summary>
    /// byte[] rawBytes = StreamIOHelper.BufferToBytes(buffer);
    /// </summary>
    public static byte[] IBufferToBytes(this IBuffer buffer)
    {
        if (buffer == null || buffer.Length == 0)
            return Array.Empty<byte>();

        var data = new byte[buffer.Length];
        DataReader.FromBuffer(buffer).ReadBytes(data);
        return data;
    }

    /// <summary>
    /// IBuffer bufferFromBytes = StreamIOHelper.BytesToBuffer(new byte[] { 0x01, 0x02, 0x03 });
    /// </summary>
    public static IBuffer BytesToIBuffer(this byte[] data)
    {
        if (data == null || data.Length == 0)
            return WindowsRuntimeBuffer.Create(0);

        var writer = new DataWriter();
        writer.WriteBytes(data);
        return writer.DetachBuffer();
    }

    /// <summary>
    /// IBuffer buff = StreamIOHelper.BytesToBuffer(new byte[] { 1, 2, 3 });
    /// </summary>
    public static IBuffer BytesToIBuffer(this ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
            return WindowsRuntimeBuffer.Create(0);

        var writer = new DataWriter();
        writer.WriteBytes(data.ToArray()); // UWP requires byte[]
        return writer.DetachBuffer();
    }

    /// <summary>
    /// IBuffer buff = StreamIOHelper.StringToBuffer("HELLO".AsSpan());
    /// </summary>
    public static IBuffer StringToIBuffer(this ReadOnlySpan<char> text, Encoding? encoding = null)
    {
        if (text.IsEmpty)
            return WindowsRuntimeBuffer.Create(0);

        encoding ??= Encoding.UTF8;

        Span<byte> buffer = stackalloc byte[encoding.GetByteCount(text)];
        encoding.GetBytes(text, buffer);

        return BytesToIBuffer(buffer);
    }

    /// <summary>
    /// string text = StreamIOHelper.BufferToString(buffer);
    /// </summary>
    public static string IBufferToString(this IBuffer buffer, Encoding? encoding = null)
    {
        if (buffer == null || buffer.Length == 0)
            return string.Empty;

        encoding ??= Encoding.UTF8; // default to UTF-8
        var bytes = IBufferToBytes(buffer);
        return encoding.GetString(bytes);
    }

    /// <summary>
    /// IBuffer bufferFromString = StreamIOHelper.StringToBuffer("HELLO");
    /// </summary>
    public static IBuffer StringToIBuffer(this string text, Encoding? encoding = null)
    {
        if (string.IsNullOrEmpty(text))
            return WindowsRuntimeBuffer.Create(0); // Return empty buffer

        encoding ??= Encoding.UTF8;
        byte[] bytes = encoding.GetBytes(text);
        return BytesToIBuffer(bytes);
    }
    #endregion
}