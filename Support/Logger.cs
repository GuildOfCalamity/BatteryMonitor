using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Threading;

namespace BatteryMonitor;

internal static class Logger
{
    static readonly string UniqueName = $"{App.GetCurrentAssemblyName()}_{DateTime.Now:yyyy-MM-dd}.log";

    static readonly BlockingCollection<string> LogCollection = new BlockingCollection<string>();

    static readonly TaskCompletionSource<string> LoggerPathCompletionSource = new TaskCompletionSource<string>();

    static readonly Thread BackgroundProcessThread = new Thread(LogProcessThread)
    {
        IsBackground = true,
        Priority = ThreadPriority.BelowNormal
    };

    static Logger()
    {
        BackgroundProcessThread.Start();
    }

    /// <summary>
    /// Core log method (formatting will be added)
    /// </summary>
    /// <param name="ex"><see cref="Exception"/></param>
    /// <param name="AdditionalComment">extra text if needed</param>
    public static void Log(Exception ex, string? AdditionalComment = null, [CallerMemberName] string? MemberName = null, [CallerFilePath] string? SourceFilePath = null, [CallerLineNumber] int SourceLineNumber = 0)
    {
        if (ex == null)
            throw new ArgumentNullException(nameof(ex), "Exception object cannot be null.");

        try
        {
            if (ex is AggregateException Aggreated)
            {
                ex = Aggreated.Flatten().InnerException ?? ex;
            }

            string[] MessageSplit;

            try
            {
                string? ExceptionMessageRaw = ex.Message;

                if (string.IsNullOrWhiteSpace(ExceptionMessageRaw))
                    MessageSplit = Array.Empty<string>();
                else
                    MessageSplit = ExceptionMessageRaw.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select((Line) => $"        {Line.Trim()}").ToArray();
            }
            catch
            {
                MessageSplit = Array.Empty<string>();
            }

            string[] StackTraceSplit;

            try
            {
                string? ExceptionStackTraceRaw = ex.StackTrace;

                if (string.IsNullOrEmpty(ExceptionStackTraceRaw))
                    StackTraceSplit = Array.Empty<string>();
                else
                    StackTraceSplit = ExceptionStackTraceRaw.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).Select((Line) => $"        {Line.Trim()}").ToArray();
            }
            catch
            {
                StackTraceSplit = Array.Empty<string>();
            }

            if (string.IsNullOrEmpty(AdditionalComment))
                AdditionalComment = "None";

            StringBuilder Builder = new StringBuilder()
               .AppendLine("------------------------------------")
               .AppendLine($"EXCEPTION [Comment: {AdditionalComment}]")
               .AppendLine("------------------------------------")
               .AppendLine($"Source: {App.GetCurrentAssemblyName()}")
               .AppendLine()
               .AppendLine($"Exception: {ex}")
               .AppendLine()
               .AppendLine("Message:")
               .AppendLine(MessageSplit.Length == 0 ? "        Unknown" : string.Join(Environment.NewLine, MessageSplit))
               .AppendLine()
               .AppendLine("StackTrace:")
               .AppendLine(StackTraceSplit.Length == 0 ? "        Unknown" : string.Join(Environment.NewLine, StackTraceSplit))
               .AppendLine()
               .AppendLine("Extra info: ")
               .AppendLine($"        CallerFileName: {Path.GetFileName(SourceFilePath)}")
               .AppendLine($"        CallerMemberName: {MemberName}")
               .AppendLine($"        CallerLineNumber: {SourceLineNumber}")
               .AppendLine("------------------------------------");

            LogInternal(Builder.ToString());
        }
        catch (Exception dex)
        {
#if DEBUG
            if (Debugger.IsAttached)
                Debugger.Break();
            else
                Debugger.Launch();
#endif
        }
    }

    /// <summary>
    /// Secondary log method (formatting will be added)
    /// </summary>
    /// <param name="Message">text to write</param>
    public static void Log(string? Message, [CallerMemberName] string? MemberName = null, [CallerFilePath] string? SourceFilePath = null, [CallerLineNumber] int SourceLineNumber = 0)
    {
        if (string.IsNullOrEmpty(Message))
            return;

        try
        {
            string[] MessageSplit;

            try
            {
                if (string.IsNullOrWhiteSpace(Message))
                    MessageSplit = Array.Empty<string>();
                else
                    MessageSplit = Message.Split(new string[] { Environment.NewLine }, StringSplitOptions.RemoveEmptyEntries).Select((Line) => $"        {Line.Trim()}").ToArray();
            }
            catch
            {
                MessageSplit = Array.Empty<string>();
            }

            StringBuilder Builder = new StringBuilder()
               .AppendLine("------------------------------------")
               .AppendLine("EXCEPTION                           ")
               .AppendLine("------------------------------------")
               .AppendLine($"Source: {App.GetCurrentAssemblyName()}")
               .AppendLine()
               .AppendLine("Message:")
               .AppendLine(MessageSplit.Length == 0 ? "        Unknown" : string.Join(Environment.NewLine, MessageSplit))
               .AppendLine()
               .AppendLine("Extra info: ")
               .AppendLine($"        CallerFileName: {Path.GetFileName(SourceFilePath)}")
               .AppendLine($"        CallerMemberName: {MemberName}")
               .AppendLine($"        CallerLineNumber: {SourceLineNumber}")
               .AppendLine("------------------------------------");

            LogInternal(Builder.ToString());
        }
        catch (Exception dex)
        {
#if DEBUG
            if (Debugger.IsAttached)
                Debugger.Break();
            else
                Debugger.Launch();
#endif
        }
    }

    /// <summary>
    /// Simplified logging method with no formatting added
    /// </summary>
    /// <param name="Message">text to write</param>
    public static void Log(string? Message, bool addTime = false)
    {
        if (string.IsNullOrEmpty(Message))
            return;
        
        var msg = addTime ? $"[{DateTime.Now.ToString("hh:mm:ss.fff tt")}] {Message}" : $"{Message}";

        LogInternal($"{msg}");
    }

    public static void SetLoggerFolderPath(string Path)
    {
        try
        {
            if (!Directory.Exists(Path))
                Directory.CreateDirectory(Path);

            if (Directory.Exists(Path))
            {
                LoggerPathCompletionSource.TrySetResult(Path);
            }
        }
        catch (Exception) { /* Console.WriteLine($"Logger path creation failed!"); */ }
    }

    /// <summary>
    /// Internal log method
    /// </summary>
    /// <param name="Message">the text to log</param>
    static void LogInternal(string Message)
    {
        LogCollection.Add(Message + Environment.NewLine);
    }

    static void LogProcessThread()
    {
        try
        {
            using (FileStream LogFileStream = File.Open(Path.Combine(LoggerPathCompletionSource.Task.Result, UniqueName), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
            {
                using (StreamWriter Writer = new StreamWriter(LogFileStream, Encoding.UTF8, 1024, true))
                {
                    LogFileStream.Seek(0, SeekOrigin.End);

                    while (true)
                    {
                        string LogItem = LogCollection.Take(); // blocking call
                        Writer.WriteLine(LogItem);
                        Writer.Flush();
#if DEBUG
                        Debug.WriteLine(LogItem);
#endif
                    }
                }
            }
        }
        catch (Exception)
        {
#if DEBUG
            if (Debugger.IsAttached)
                Debugger.Break();
            else
                Debugger.Launch();
#endif
        }
    }

    public static bool ConfirmLogIsFlushed(int TimeoutMilliseconds)
    {
        return SpinWait.SpinUntil(() => BackgroundProcessThread.ThreadState.HasFlag(System.Threading.ThreadState.WaitSleepJoin), Math.Max(0, TimeoutMilliseconds));
    }

    public static string SanitizeFileNameOrPath(string fileName)
    {
        if (string.IsNullOrEmpty(fileName)) { return string.Empty; }
        return string.Join("_", fileName.Split(System.IO.Path.GetInvalidFileNameChars()));
    }
}