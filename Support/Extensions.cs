using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Contracts;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Numerics;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.VisualBasic;
using Windows.ApplicationModel;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace BatteryMonitor;

public static class Extensions
{
    public static bool CheckIfValidPtr(this IntPtr Ptr)
    {
        return Ptr != IntPtr.Zero && Ptr.ToInt64() != -1;
    }

    public static string FormatMilliwatts(int? milliwatts)
    {
        if (milliwatts == null)
            return "0 Watts";

        if (milliwatts < 0)
            milliwatts = Math.Abs((int)milliwatts);

        if (milliwatts >= 1000000)
            return $"{milliwatts / 1000000.0:F2} KW";
        else if (milliwatts >= 1000)
            return $"{milliwatts / 1000.0:F2} W";
        else
            return $"{milliwatts:F2} mW";
    }

    public static string MilliwattHoursToMinutes(int? milliwattHours, int? powerConsumptionInMilliwatts)
    {
        if (milliwattHours == null || powerConsumptionInMilliwatts == null || powerConsumptionInMilliwatts == 0)
            return "Invalid power consumption value.";
        else if (powerConsumptionInMilliwatts < 0)
            powerConsumptionInMilliwatts = Math.Abs((int)powerConsumptionInMilliwatts);

        double totalMinutes = ((int)milliwattHours * 60.0) / (int)powerConsumptionInMilliwatts;
        int minutesRemaining = (int)Math.Floor(totalMinutes);
        int hoursRemaining = minutesRemaining / 60;
        minutesRemaining %= 60;

        return $"{hoursRemaining} hours & {minutesRemaining} minutes ";
    }

    public static bool ContainsUnicode(this string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        return text.Any(c => (int)c > 127);
    }

    /// <summary>
    /// Home-brew parallel invoke that will block while actions run.
    /// </summary>
    /// <param name="actions">array of <see cref="Action"/>s</param>
    public static void ParallelInvoke(params Action[] actions)
    {
        if (actions.Length < 10)
        {
            IEnumerable<Task>? tasks = from action in actions select Task.Run(action);
            Task.WaitAll(tasks.ToArray());
        }
        else // The overhead for Task can add up once you have 100s of actions.
        {
            Parallel.ForEach(actions, (action) => { action.Invoke(); });
        }
    }

    /// <summary>
    /// Home-brew parallel invoke that will not block while actions run.
    /// </summary>
    /// <param name="actions">array of <see cref="Action"/>s</param>
    public static void ParallelInvokeAndForget(params Action[] action)
    {
        action.ForEach(a =>
        {
            try
            {
                ThreadPool.QueueUserWorkItem((obj) => { a.Invoke(); });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] ParallelInvokeAndForget: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// An unoptimized, home-brew parallel ForEach implementation.
    /// </summary>
    public static void ParallelForEach<T>(IEnumerable<T> source, Action<T> action)
    {
        IEnumerable<Task>? tasks = from item in source select Task.Run(() => action(item));
        Task.WaitAll(tasks.ToArray());
    }

    /// <summary>
    /// An optimized, home-brew parallel ForEach implementation.
    /// Creates branched execution based on available processors.
    /// </summary>
    public static void ParallelForEachUsingEnumerator<T>(IEnumerable<T> source, Action<T> action, Action<Exception>? onError)
    {
        using IEnumerator<T> e = source.GetEnumerator();
        IEnumerable<Task>? tasks = from i in Enumerable.Range(0, Environment.ProcessorCount)
            select Task.Run(() =>
            {
                while (true)
                {
                    T item;
                    lock (e)
                    {
                        if (!e.MoveNext()) { return; }
                        item = e.Current;
                    }
                    #region [Must stay outside locking scope, or defeats the purpose of parallelism]
                    try
                    {
                        action(item); 
                    }
                    catch (Exception ex)
                    {
                        onError?.Invoke(ex);
                    }
                    #endregion
                }
            });
        Task.WaitAll(tasks.ToArray());
    }

    /// <summary>
    /// An optimized, home-brew parallel ForEach implementation.
    /// Creates branched execution based on available processors.
    /// </summary>
    public static void ParallelForEachUsingPartitioner<T>(IEnumerable<T> source, Action<T> action, Action<Exception>? onError, EnumerablePartitionerOptions options = EnumerablePartitionerOptions.NoBuffering)
    {
        //IList<IEnumerator<T>> partitions = Partitioner.Create(source, options).GetPartitions(Environment.ProcessorCount);
        IEnumerable<Task>? tasks = 
            from partition 
            in Partitioner.Create(source, options).GetPartitions(Environment.ProcessorCount)
            select Task.Run(() =>
            {
                using (partition) // partitions are disposable!
                {
                    while (partition.MoveNext())
                    {
                        try
                        {
                            action(partition.Current);
                        }
                        catch (Exception ex)
                        {
                            onError?.Invoke(ex);
                        }
                    }
                }
            });
        Task.WaitAll(tasks.ToArray());
    }

    /// <summary>
    /// An optimized, home-brew parallel ForEach implementation.
    /// </summary>
    public static void ParallelForEachUsingPartitioner<T>(IList<T> list, Action<T> action, Action<Exception>? onError, EnumerablePartitionerOptions options = EnumerablePartitionerOptions.NoBuffering)
    {
        //IList<IEnumerator<T>> partitions = Partitioner.Create(list, options).GetPartitions(Environment.ProcessorCount);
        IEnumerable<Task>? tasks =
            from partition
            in Partitioner.Create(list, options).GetPartitions(Environment.ProcessorCount)
            select Task.Run(() =>
            {
                using (partition) // partitions are disposable!
                {
                    while (partition.MoveNext())
                    {
                        try
                        {
                            action(partition.Current);
                        }
                        catch (Exception ex)
                        {
                            onError?.Invoke(ex);
                        }
                    }
                }
            });
        Task.WaitAll(tasks.ToArray());
    }


    /// <summary>
    /// If not using a console app, set <paramref name="consoleApp"/> to false.
    /// </summary>
    /// <param name="task"><see cref="Task"/></param>
    /// <param name="onSuccess"><see cref="Action"/> to perform if <see cref="TaskContinuationOptions.OnlyOnRanToCompletion"/></param>
    /// <param name="onCanceled"><see cref="Action"/> to perform if <see cref="TaskContinuationOptions.OnlyOnCanceled"/></param>
    /// <param name="onFaulted"><see cref="Action"/> to perform if <see cref="TaskContinuationOptions.OnlyOnFaulted"/></param>
    public static void ContinueTaskWithActions(this Task task, Action? onSuccess, Action? onCanceled, Action? onFaulted, bool consoleApp = false)
    {
        #region [ContinueWith-Success]
        task.ContinueWith(task => { onSuccess?.Invoke(); },
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnRanToCompletion,
        consoleApp ? TaskScheduler.Current : TaskScheduler.FromCurrentSynchronizationContext());
        #endregion

        #region [ContinueWith-Canceled]
        task.ContinueWith(task => { onCanceled?.Invoke(); },
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnCanceled,
        consoleApp ? TaskScheduler.Current : TaskScheduler.FromCurrentSynchronizationContext());
        #endregion

        #region [ContinueWith-Faulted]
        task.ContinueWith(task => { onFaulted?.Invoke(); },
        CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted,
        consoleApp ? TaskScheduler.Current : TaskScheduler.FromCurrentSynchronizationContext());
        #endregion
    }

    /// <summary>
    /// Func<string, int> getUserId = (id) => { ... };
    /// int userId = getUserId.Retry(3)("Email");
    /// </summary>
    public static Func<TArg, TResult> Retry<TArg, TResult>(this Func<TArg, TResult> func, int maxRetry, int retryDelay = 2000)
    {
        return (arg) => 
        {
            int tryCount = 0;
            while (true)
            {
                try
                {
                    return func(arg);
                }
                catch (Exception ex)
                {
                    if (++tryCount > maxRetry)
                    {
                        throw new Exception($"Retry attempts exhausted: {ex.Message}", ex);
                    }
                    else
                    {
                        Debug.WriteLine($"[WARNING] Failed: {ex.Message}");
                        Debug.WriteLine($"[INFO] Attempts left: {maxRetry - tryCount}");
                        Thread.Sleep(retryDelay);
                    }
                }
            }
        };
    }

    /// <summary>
    ///   Generic retry mechanism with exponential back-off
    /// <example><code>
    ///   Retry(() => MethodThatHasNoReturnValue());
    /// </code></example>
    /// </summary>
    public static void Retry(this Action action, int maxRetry = 3, int retryDelay = 1000)
    {
        int retries = 0;
        while (true)
        {
            try
            {
                action();
                break;
            }
            catch (Exception ex)
            {
                retries++;
                if (retries > maxRetry)
                {
                    throw new TimeoutException($"Operation failed after {maxRetry} retries: {ex.Message}", ex);
                }
                Debug.WriteLine($"[WARNING] Retry {retries}/{maxRetry} after failure: {ex.Message}. Retrying in {retryDelay} ms...");
                Thread.Sleep(retryDelay);
                retryDelay *= 2; // Double the delay after each attempt.
            }
        }
    }

    /// <summary>
    ///   Modified retry mechanism for return value with exponential back-off.
    /// <example><code>
    ///   int result = Retry(() => MethodThatReturnsAnInteger());
    /// </code></example>
    /// </summary>
    public static T Retry<T>(this Func<T> func, int maxRetry = 3, int retryDelay = 1000)
    {
        int retries = 0;
        while (true)
        {
            try
            {
                return func();
            }
            catch (Exception ex)
            {
                retries++;
                if (retries > maxRetry)
                {
                    throw new TimeoutException($"Operation failed after {maxRetry} retries: {ex.Message}", ex);
                }
                Debug.WriteLine($"[WARNING] Retry {retries}/{maxRetry} after failure: {ex.Message}. Retrying in {retryDelay} ms...");
                Thread.Sleep(retryDelay);
                retryDelay *= 2; // Double the delay after each attempt.
            }
        }
    }

    /// <summary>
    ///   Generic retry mechanism with exponential back-off
    /// <example><code>
    ///   await RetryAsync(() => AsyncMethodThatHasNoReturnValue());
    /// </code></example>
    /// </summary>
    public static async Task RetryAsync(this Func<Task> action, int maxRetry = 3, int retryDelay = 1000)
    {
        int retries = 0;
        while (true)
        {
            try
            {
                await action();
                break;
            }
            catch (Exception ex)
            {
                retries++;
                if (retries > maxRetry)
                {
                    throw new InvalidOperationException($"Operation failed after {maxRetry} retries: {ex.Message}", ex);
                }
                Debug.WriteLine($"[WARNING] Retry {retries}/{maxRetry} after failure: {ex.Message}. Retrying in {retryDelay} ms...");
                await Task.Delay(retryDelay);
                retryDelay *= 2; // Double the delay after each attempt.
            }
        }
    }

    /// <summary>
    ///   Modified retry mechanism for return value with exponential back-off.
    /// <example><code>
    ///   int result = await RetryAsync(() => AsyncMethodThatReturnsAnInteger());
    /// </code></example>
    /// </summary>
    public static async Task<T> RetryAsync<T>(this Func<Task<T>> func, int maxRetry = 3, int retryDelay = 1000)
    {
        int retries = 0;
        while (true)
        {
            try
            {
                return await func();
            }
            catch (Exception ex)
            {
                retries++;
                if (retries > maxRetry)
                {
                    throw new InvalidOperationException($"Operation failed after {maxRetry} retries: {ex.Message}", ex);
                }
                Debug.WriteLine($"[WARNING] Retry {retries}/{maxRetry} after failure: {ex.Message}. Retrying in {retryDelay} ms...");
                await Task.Delay(retryDelay);
                retryDelay *= 2; // Double the delay after each attempt.
            }
        }
    }

    /// <summary>
    /// Fetch all derived types from a super class.
    /// </summary>
    /// <returns><see cref="List{T}"/></returns>
    public static List<Type> GetDerivedClasses<T>(T objectClass) where T : class
    {
        List<Type> derivedClasses = new List<Type>();
        // Get the assembly containing the base class
        Assembly assembly = typeof(T).Assembly;
        // Iterate through all types in the assembly
        foreach (Type type in assembly.GetTypes())
        {
            // Check if the type is a subclass of T
            if (type.IsSubclassOf(typeof(T)))
            {
                Debug.WriteLine($"[INFO] Adding subtype '{type.Name}'");
                derivedClasses.Add(type);
            }
        }
        Debug.WriteLine($"[INFO] Total subclasses: {derivedClasses.Count}");
        return derivedClasses;
    }

    #region [Easing Functions]

    // Quadratic Easing (t²): EaseInQuadratic → Starts slow, speeds up.  EaseOutQuadratic → Starts fast, slows down.  EaseInOutQuadratic → Symmetric acceleration-deceleration.
    public static double EaseInQuadratic(double t) => t * t;
    public static double EaseOutQuadratic(double t) => 1.0 - (1.0 - t) * (1.0 - t);
    public static double EaseInOutQuadratic(double t) => t < 0.5 ? 2.0 * t * t : 1.0 - Math.Pow(-2.0 * t + 2.0, 2.0) / 2.0;
    
    // Cubic Easing (t³): EaseInCubic → Stronger acceleration.  EaseOutCubic → Slower deceleration.  EaseInOutCubic → Balanced smooth curve.
    public static double EaseInCubic(double t) => Math.Pow(t, 3.0);
    public static double EaseOutCubic(double t) => 1.0 - Math.Pow(1.0 - t, 3.0);
    public static double EaseInOutCubic(double t) => t < 0.5 ? 4.0 * Math.Pow(t, 3.0) : 1.0 - Math.Pow(-2.0 * t + 2.0, 3.0) / 2.0;
    
    // Quartic Easing (t⁴): Sharper transition than cubic easing.
    public static double EaseInQuartic(double t) => Math.Pow(t, 4.0);
    public static double EaseOutQuartic(double t) => 1.0 - Math.Pow(1.0 - t, 4.0);
    public static double EaseInOutQuartic(double t) => t < 0.5 ? 8.0 * Math.Pow(t, 4.0) : 1.0 - Math.Pow(-2.0 * t + 2.0, 4.0) / 2.0;
    
    // Quintic Easing (t⁵): Even steeper curve for dramatic transitions.
    public static double EaseInQuintic(double t) => Math.Pow(t, 5.0);
    public static double EaseOutQuintic(double t) => 1.0 - Math.Pow(1.0 - t, 5.0);
    public static double EaseInOutQuintic(double t) => t < 0.5 ? 16.0 * Math.Pow(t, 5.0) : 1.0 - Math.Pow(-2.0 * t + 2.0, 5.0) / 2.0;
    
    // Elastic Easing (Bouncing Effect)
    public static double EaseInElastic(double t) => t == 0 ? 0 : t == 1 ? 1 : -Math.Pow(2.0, 10.0 * t - 10.0) * Math.Sin((t * 10.0 - 10.75) * (2.0 * Math.PI) / 3.0);
    public static double EaseOutElastic(double t) => t == 0 ? 0 : t == 1 ? 1 : Math.Pow(2.0, -10.0 * t) * Math.Sin((t * 10.0 - 0.75) * (2.0 * Math.PI) / 3.0) + 1.0;
    public static double EaseInOutElastic(double t) => t == 0 ? 0 : t == 1 ? 1 : t < 0.5 ? -(Math.Pow(2.0, 20.0 * t - 10.0) * Math.Sin((20.0 * t - 11.125) * (2.0 * Math.PI) / 4.5)) / 2.0 : (Math.Pow(2.0, -20.0 * t + 10.0) * Math.Sin((20.0 * t - 11.125) * (2.0 * Math.PI) / 4.5)) / 2.0 + 1.0;
    
    //Bounce Easing(Ball Bouncing Effect)
    public static double EaseInBounce(double t) => 1.0 - EaseOutBounce(1.0 - t);
    public static double EaseOutBounce(double t)
    {
        double n1 = 7.5625, d1 = 2.75;
        if (t < 1.0 / d1)
            return n1 * t * t;
        else if (t < 2.0 / d1)
            return n1 * (t -= 1.5 / d1) * t + 0.75;
        else if (t < 2.5 / d1)
            return n1 * (t -= 2.25 / d1) * t + 0.9375;
        else
            return n1 * (t -= 2.625 / d1) * t + 0.984375;
    }
    public static double EaseInOutBounce(double t) => t < 0.5 ? (1.0 - EaseOutBounce(1.0 - 2.0 * t)) / 2.0 : (1.0 + EaseOutBounce(2.0 * t - 1.0)) / 2.0;
    
    // Exponential Easing(Fast Growth/Decay)
    public static double EaseInExpo(double t) => t == 0 ? 0 : Math.Pow(2.0, 10.0 * t - 10.0);
    public static double EaseOutExpo(double t) => t == 1 ? 1 : 1.0 - Math.Pow(2.0, -10.0 * t);
    public static double EaseInOutExpo(double t) => t == 0 ? 0 : t == 1 ? 1 : t < 0.5 ? Math.Pow(2.0, 20.0 * t - 10.0) / 2.0 : (2.0 - Math.Pow(2.0, -20.0 * t + 10.0)) / 2.0;
    
    // Circular Easing(Smooth Circular Motion)
    public static double EaseInCircular(double t) => 1.0 - Math.Sqrt(1.0 - Math.Pow(t, 2.0));
    public static double EaseOutCircular(double t) => Math.Sqrt(1.0 - Math.Pow(t - 1.0, 2.0));
    public static double EaseInOutCircular(double t) => t < 0.5 ? (1.0 - Math.Sqrt(1.0 - Math.Pow(2.0 * t, 2.0))) / 2.0 : (Math.Sqrt(1.0 - Math.Pow(-2.0 * t + 2.0, 2.0)) + 1.0) / 2.0;
    
    // Back Easing(Overshoots Before Settling)
    public static double EaseInBack(double t) => 2.70158 * t * t * t - 1.70158 * t * t;
    public static double EaseOutBack(double t) => 1.0 + 2.70158 * Math.Pow(t - 1.0, 3.0) + 1.70158 * Math.Pow(t - 1.0, 2.0);
    public static double EaseInOutBack(double t) => t < 0.5 ? (Math.Pow(2.0 * t, 2.0) * ((2.59491 + 1.0) * 2.0 * t - 2.59491)) / 2.0 : (Math.Pow(2.0 * t - 2.0, 2.0) * ((2.59491 + 1.0) * (t * 2.0 - 2.0) + 2.59491) + 2.0) / 2.0;

    #endregion

    /// <summary>
    /// Can be passed a list of repeats and will tally and sort a result of their frequency.
    /// </summary>
    public static List<KeyValuePair<string, int>> CountAndSortCategories(this List<string> categories)
    {
        try
        {
            var categoryCounts = categories
                .GroupBy(category => category)
                .Select(group => new KeyValuePair<string, int>(group.Key, group.Count()))
                .OrderByDescending(pair => pair.Value)
                .ToList();

            return categoryCounts;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WARNING] CountAndSortCategories: {ex.Message}");
            return new List<KeyValuePair<string, int>>();
        }
    }

    public static string SanitizeFileNameOrPath(this string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            return string.Empty;

        return string.Join("_", fileName.Split(Path.GetInvalidFileNameChars()));
    }

    /// <summary>
    /// Can be helpful with XML payloads that contain too many namespaces.
    /// </summary>
    /// <param name="xmlDocument"></param>
    /// <param name="disableFormatting"></param>
    /// <returns>sanitized XML</returns>
    public static string RemoveAllNamespaces(string xmlDocument, bool disableFormatting = true)
    {
        try
        {
            XElement xmlDocumentWithoutNs = RemoveAllNamespaces(XElement.Parse(xmlDocument));
            if (disableFormatting)
                return xmlDocumentWithoutNs.ToString(SaveOptions.DisableFormatting);
            else
                return xmlDocumentWithoutNs.ToString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] RemoveAllNamespaces: {ex.Message}");
            return xmlDocument;
        }


        XElement RemoveAllNamespaces(XElement? e)
        {
            return new XElement(e?.Name.LocalName ?? "",
                (from n in e?.Nodes()
                 select ((n is XElement) ? RemoveAllNamespaces(n as XElement) : n)),
                (e != null && e.HasAttributes) ?
                (from a in e?.Attributes()
                 where (!a.IsNamespaceDeclaration)
                 select new XAttribute(a.Name.LocalName, a.Value)) : null);
        }
    }

    /// <summary>
    /// Can be helpful with XML payloads that contain too many namespaces.
    /// </summary>
    /// <param name="xmlDocument"></param>
    /// <param name="disableFormatting"></param>
    /// <returns>sanitized XML</returns>
    public static string RemoveAllNamespacesLINQ(string xmlDocument, bool disableFormatting = true)
    {
        try
        {
            XElement xmlDocumentWithoutNs = RemoveAllNamespacesLINQ(XElement.Parse(xmlDocument));
            if (disableFormatting)
                return xmlDocumentWithoutNs.ToString(SaveOptions.DisableFormatting);
            else
                return xmlDocumentWithoutNs.ToString();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] RemoveAllNamespacesLINQ: {ex.Message}");
            return xmlDocument;
        }

        ///<summary>
        /// Here's the rewritten method using method syntax without using LINQ query syntax.
        /// This version utilizes method chaining and lambda expressions in place of LINQ 
        /// query syntax while maintaining the same functionality as the original method.
        ///</summary>
        /// <returns><see cref="XElement"/></returns>
        XElement RemoveAllNamespacesLINQ(XElement? e)
        {
            return new XElement(e?.Name.LocalName ?? "",
                e?.Nodes().Select(n => n is XElement ? RemoveAllNamespacesLINQ(n as XElement) : n),
                e?.Attributes().Where(a => !a.IsNamespaceDeclaration)
                               .Select(a => new XAttribute(a.Name.LocalName, a.Value)));
        }
    }

    /// <summary>
    /// Creates a dictionary using the element name as the key and the node's contents as the values.
    /// </summary>
    /// <param name="xml">The XML string to parse.</param>
    /// <param name="dump">If true, the contents will be output to the console.</param>
    /// <returns><see cref="Dictionary{string, List{string}}"/></returns>
    public static Dictionary<string, List<string>> ConvertXmlIntoDictionary(this string xml, bool dump = false)
    {
        Dictionary<string, List<string>> dict = new Dictionary<string, List<string>>();

        try
        {
            XElement root = XElement.Parse(xml);

            foreach (XElement element in root.DescendantsAndSelf())
            {
                if (!dict.ContainsKey(element.Name.LocalName))
                    dict[element.Name.LocalName] = new List<string>();

                if (!string.IsNullOrEmpty(element.Value.Trim()))
                    dict[element.Name.LocalName].Add(element.Value.Trim());

                foreach (XAttribute attribute in element.Attributes())
                {
                    if (!dict.ContainsKey(attribute.Name.LocalName))
                        dict[attribute.Name.LocalName] = new List<string>();

                    dict[attribute.Name.LocalName].Add(attribute.Value);
                }
            }

            if (dump)
            {
                foreach (var pair in dict)
                {
                    Console.WriteLine($"Key ⇨ {pair.Key}");
                    Console.WriteLine($" • {string.Join(", ", pair.Value)}");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] ConvertXmlIntoDictionary: {ex.Message}");
        }

        return dict;
    }


    /// <summary>
    /// Minimum password length is 8 characters.
    /// </summary>
    /// <param name="pswd">the string to evaluate</param>
    /// <returns><c>true</c> if password meets basic strength requirements, otherwise <c>false</c></returns>
    public static bool IsStrongPasswordRegex(string pswd)
    {
        return Regex.IsMatch(pswd ?? "", "^(?=.*[a-z])(?=.*[A-Z])(?=.*[0-9])(?=.*[\\-`\\]~\\[!@#$%^\\&*()\\\\_+={}:;<,>.?/|'\\\"])(?=.{8,})");
    }

    /// <summary>
    /// Determine if the application has been launched as an administrator.
    /// </summary>
    public static bool IsAppRunAsAdmin()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null));
    }

    /// <summary>
    ///	Checks if the taskbar is set to auto-hide.
    /// </summary>
    public static bool IsAutoHideTaskbarEnabled()
    {
        const string registryKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StuckRects3";
        const string valueName = "Settings";

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(registryKey);
            var value = key?.GetValue(valueName) as byte[];
            // The least significant bit of the 9th byte controls the auto-hide setting																		
            return value != null && ((value[8] & 0x01) == 1);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] IsAutoHideTaskbarEnabled: {ex.Message}");
        }
        
        return false;
    }

    /// <summary>
    /// Get OS version by way of <see cref="Windows.System.Profile.AnalyticsInfo"/>.
    /// </summary>
    /// <returns><see cref="Version"/></returns>
    public static Version GetWindowsVersionUsingAnalyticsInfo()
    {
        try
        {
            ulong version = ulong.Parse(Windows.System.Profile.AnalyticsInfo.VersionInfo.DeviceFamilyVersion);
            var Major = (ushort)((version & 0xFFFF000000000000L) >> 48);
            var Minor = (ushort)((version & 0x0000FFFF00000000L) >> 32);
            var Build = (ushort)((version & 0x00000000FFFF0000L) >> 16);
            var Revision = (ushort)(version & 0x000000000000FFFFL);

            return new Version(Major, Minor, Build, Revision);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] GetWindowsVersionUsingAnalyticsInfo: {ex.Message}", $"{nameof(Extensions)}");
            return new Version(); // 0.0
        }
    }

    /// <summary>
    /// Copies one <see cref="List{T}"/> to another <see cref="List{T}"/> by value (deep copy).
    /// </summary>
    /// <returns><see cref="List{T}"/></returns>
    /// <remarks>
    /// If your model does not inherit from <see cref="ICloneable"/>
    /// then a manual DTO copying technique could be used instead.
    /// </remarks>
    public static List<T> DeepCopy<T>(this List<T> source) where T : ICloneable
    {
        if (source == null)
            throw new ArgumentNullException($"{nameof(source)} list cannot be null.");

        List<T> destination = new List<T>(source.Count);
        foreach (T item in source)
        {
            if (item is ICloneable cloneable)
                destination.Add((T)cloneable.Clone());
            else
                throw new InvalidOperationException($"Type {typeof(T).FullName} does not implement ICloneable.");
        }

        return destination;
    }

    /// <summary>
    /// An updated string truncation helper.
    /// </summary>
    /// <remarks>
    /// This can be helpful when the CharacterEllipsis TextTrimming Property is not available.
    /// </remarks>
    public static string Truncate(this string text, int maxLength, string mesial = "…")
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        if (maxLength > 0 && text.Length > maxLength)
        {
            var limit = maxLength / 2;
            if (limit > 1)
            {
                return String.Format("{0}{1}{2}", text.Substring(0, limit).Trim(), mesial, text.Substring(text.Length - limit).Trim());
            }
            else
            {
                var tmp = text.Length <= maxLength ? text : text.Substring(0, maxLength).Trim();
                return String.Format("{0}{1}", tmp, mesial);
            }
        }
        return text;
    }

    public static string HumanReadableSize(this long length)
    {
        const int unit = 1024;
        var mu = new List<string> { "B", "KB", "MB", "GB", "PT" };
        while (length > unit)
        {
            mu.RemoveAt(0);
            length /= unit;
        }
        return $"{length}{mu[0]}";
    }

    public static uint[] HexStringToUIntArray(this string hexString, int groupSize = 2)
    {
        if (string.IsNullOrEmpty(hexString))
            return new uint[0];

        if (hexString.Length % 2 != 0 || groupSize % 2 != 0)
            throw new Exception($"HexStringToUIntArray: The input string or group size cannot be odd.");

        uint[] data = new uint[hexString.Length / groupSize];
        for (int index = 0; index < data.Length; index++)
        {
            try
            {
                string byteValue = hexString.Substring(index * groupSize, groupSize);
                data[index] = uint.Parse(byteValue, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception) { data[index] = 0; }
        }

        return data;
    }

    public static string UIntArrayToHexString(this uint[] array, int groupSize = 2)
    {
        if (array.Length == 0)
            return string.Empty;

        var hex = new StringBuilder(array.Length * groupSize);

        foreach (uint i in array)
            hex.AppendFormat("{0:X" + groupSize + "}", i);

        return hex.ToString();
    }

    public static byte[] HexStringToByteArray(this string hexString)
    {
        if (string.IsNullOrEmpty(hexString) || hexString.Length % 2 != 0)
            throw new Exception($"HexStringToByteArray: The input string cannot empty or an odd amount.");

        byte[] data = new byte[hexString.Length / 2];
        for (int index = 0; index < data.Length; index++)
        {
            try
            {
                string byteValue = hexString.Substring(index * 2, 2);
                data[index] = byte.Parse(byteValue, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception) { data[index] = 0; }
        }

        return data;
    }

    public static byte[] HexStringToByteArrayEnumerable(this string hexString)
    {
        if (string.IsNullOrEmpty(hexString))
            return new byte[0];

        return Enumerable.Range(0, hexString.Length)
                         .Where(x => x % 2 == 0)
                         .Select(x => Convert.ToByte(hexString.Substring(x, 2), 16))
                         .ToArray();
    }

    public static string ByteArrayToHexString(this byte[] array)
    {
        if (array.Length == 0)
            return string.Empty;
        
        var hex = new StringBuilder(array.Length * 2);

        foreach (byte b in array)
            hex.AppendFormat("{0:X2}", b);

        return hex.ToString();
    }

    public static UInt16 ReverseBytes(this UInt16 value)
    {
        return (UInt16)((value & 0xFFU) << 8 | (value & 0xFF00U) >> 8);
    }

    public static UInt32 ReverseBytes(this UInt32 value)
    {
        return (value & 0x000000FFU) << 24 | (value & 0x0000FF00U) << 8 |
               (value & 0x00FF0000U) >> 8 | (value & 0xFF000000U) >> 24;
    }

    public static UInt64 ReverseBytes(this UInt64 value)
    {
        return (value & 0x00000000000000FFUL) << 56 | (value & 0x000000000000FF00UL) << 40 |
               (value & 0x0000000000FF0000UL) << 24 | (value & 0x00000000FF000000UL) << 8 |
               (value & 0x000000FF00000000UL) >> 8 | (value & 0x0000FF0000000000UL) >> 24 |
               (value & 0x00FF000000000000UL) >> 40 | (value & 0xFF00000000000000UL) >> 56;
    }

    /// <summary>
    /// Shifting by 63 gives the sign bit, either 64 1's or 64 0's.
    /// </summary>
    /// <returns>+1 if positive, -1 if negative</returns>
    public static long GetSign(this long value) => (value >> 63) == 0 ? (long)1 : (long)-1;

    /// <summary>
    /// Shifting by 31 gives the sign bit, either 32 1's or 32 0's.
    /// </summary>
    /// <returns>+1 if positive, -1 if negative</returns>
    public static int GetSign(this int value) => (value >> 31) == 0 ? (int)1 : (int)-1;

    /// <summary>
    /// Shifting by 15 gives the sign bit, either 16 1's or 16 0's.
    /// </summary>
    /// <returns>+1 if positive, -1 if negative</returns>
    public static short GetSign(this short value) => (value >> 15) == 0 ? (short)1 : (short)-1;


#pragma warning disable 8714 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'notnull' constraint.
    /// <summary>
    /// Helper for <see cref="System.Collections.Generic.SortedList{TKey, TValue}"/>
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="sortedList"></param>
    /// <returns><see cref="Dictionary{TKey, TValue}"/></returns>
    public static Dictionary<TKey, TValue> ConvertToDictionary<TKey, TValue>(this System.Collections.Generic.SortedList<TKey, TValue> sortedList)
    {
        Dictionary<TKey, TValue> dictionary = new Dictionary<TKey, TValue>();
        foreach (KeyValuePair<TKey, TValue> pair in sortedList)
        {
            dictionary.Add(pair.Key, pair.Value);
        }
        return dictionary;
    }

    /// <summary>
    /// Helper for <see cref="System.Collections.SortedList"/>
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="sortedList"></param>
    /// <returns><see cref="Dictionary{TKey, TValue}"/></returns>
    public static Dictionary<TKey, TValue> ConvertToDictionary<TKey, TValue>(this System.Collections.SortedList sortedList)
    {
        Dictionary<TKey, TValue> dictionary = new Dictionary<TKey, TValue>();
        foreach (DictionaryEntry pair in sortedList)
        {
            dictionary.Add((TKey)pair.Key, (TValue)pair.Value);
        }
        return dictionary;
    }

    /// <summary>
    /// Helper for <see cref="System.Collections.Hashtable"/>
    /// </summary>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TValue"></typeparam>
    /// <param name="hashList"></param>
    /// <returns><see cref="Dictionary{TKey, TValue}"/></returns>
    public static Dictionary<TKey, TValue> ConvertToDictionary<TKey, TValue>(this System.Collections.Hashtable hashList)
    {
        Dictionary<TKey, TValue> dictionary = new Dictionary<TKey, TValue>();
        foreach (DictionaryEntry pair in hashList)
        {
            dictionary.Add((TKey)pair.Key, (TValue)pair.Value);
        }
        return dictionary;
    }
    #pragma warning restore 8714 // The type cannot be used as type parameter in the generic type or method. Nullability of type argument doesn't match 'notnull' constraint.


    #region [Duplicate Helpers]
    /// <summary>
    /// Returns a <see cref="Tuple{T1, T2}"/> representing the <paramref name="list"/>
    /// where <b>Item1</b> is the clean set and <b>Item2</b> is the duplicate set.
    /// </summary>
    public static (List<T>, List<T>) RemoveDuplicates<T>(this List<T> list)
    {
        HashSet<T> seen = new HashSet<T>();
        List<T> dupes = new List<T>();
        List<T> clean = new List<T>();
        foreach (T item in list)
        {
            if (seen.Contains(item))
                dupes.Add(item);
            else
            {
                seen.Add(item);
                clean.Add(item);
            }
        }
        return (clean, dupes);
    }

    /// <summary>
    /// Returns a <see cref="Tuple{T1, T2}"/> representing the <paramref name="enumerable"/>
    /// where <b>Item1</b> is the clean set and <b>Item2</b> is the duplicate set.
    /// </summary>
    public static (List<T>, List<T>) RemoveDuplicates<T>(this IEnumerable<T> enumerable)
    {
        HashSet<T> seen = new HashSet<T>();
        List<T> dupes = new List<T>();
        List<T> clean = new List<T>();
        foreach (T item in enumerable)
        {
            if (seen.Contains(item))
                dupes.Add(item);
            else
            {
                seen.Add(item);
                clean.Add(item);
            }
        }
        return (clean, dupes);
    }

    /// <summary>
    /// Returns a <see cref="Tuple{T1, T2}"/> representing the <paramref name="array"/>
    /// where <b>Item1</b> is the clean set and <b>Item2</b> is the duplicate set.
    /// </summary>
    public static (T[], T[]) RemoveDuplicates<T>(this T[] array)
    {
        HashSet<T> seen = new HashSet<T>();
        List<T> dupes = new List<T>();
        List<T> clean = new List<T>();
        foreach (T item in array)
        {
            if (seen.Contains(item))
                dupes.Add(item);
            else
            {
                seen.Add(item);
                clean.Add(item);
            }
        }
        return (clean.ToArray(), dupes.ToArray());
    }

    /// <summary>
    /// Returns a <see cref="IEnumerable{T}"/> representing the <paramref name="input"/> with duplicates removed.
    /// </summary>
    public static IEnumerable<T> DedupeUsingHashSet<T>(this IEnumerable<T> input)
    {
        if (input == null)
            yield return (T)Enumerable.Empty<T>();

        var values = new HashSet<T>();
        foreach (T item in input)
        {
            // The add function returns false if the item already exists.
            if (values.Add(item))
                yield return item;
        }
    }

    /// <summary>
    /// Returns a <see cref="List{T}"/> representing the <paramref name="input"/> with duplicates removed.
    /// </summary>
    public static List<T> DedupeUsingLINQ<T>(this List<T> input)
    {
        if (input == null)
            return new List<T>();

        return input.Distinct().ToList();
    }

    /// <summary>
    /// Returns a <see cref="List{T}"/> representing the <paramref name="input"/> with duplicates removed.
    /// </summary>
    public static List<T> DedupeUsingHashSet<T>(this List<T> input)
    {
        if (input == null)
            return new List<T>();

        return (new HashSet<T>(input)).ToList();
    }

    /// <summary>
    /// Returns a <see cref="List{T}"/> representing the <paramref name="input"/> with duplicates removed.
    /// </summary>
    public static List<T> DedupeUsingDictionary<T>(this List<T> input)
    {
        if (input == null)
            return new List<T>();

        Dictionary<T, bool> seen = new Dictionary<T, bool>();
        List<T> result = new List<T>();

        foreach (T item in input)
        {
            if (!seen.ContainsKey(item))
            {
                seen[item] = true;
                result.Add(item);
            }
        }

        return result;
    }

    /// <summary>
    /// Returns true if the <paramref name="input"/> contains duplicates, false otherwise.
    /// </summary>
    public static bool HasDuplicates<T>(this IEnumerable<T> input)
    {
        var knownKeys = new HashSet<T>();
        return input.Any(item => !knownKeys.Add(item));
    }

    /// <summary>
    /// Returns true if the <paramref name="input"/> contains duplicates, false otherwise.
    /// </summary>
    public static bool HasDuplicates<T>(this List<T> input)
    {
        var knownKeys = new HashSet<T>();
        return input.Any(item => !knownKeys.Add(item));
    }
    #endregion

    public static List<string> ExtractUrls(this string text)
    {
        List<string> urls = new List<string>();
        Regex urlRx = new Regex(@"((https?|ftp|file)\://|www\.)[A-Za-z0-9\.\-]+(/[A-Za-z0-9\?\&\=;\+!'\\(\)\*\-\._~%]*)*", RegexOptions.IgnoreCase);
        MatchCollection matches = urlRx.Matches(text);
        foreach (Match match in matches) { urls.Add(match.Value); }
        return urls;
    }

    public static string DumpContent<T>(this List<T> list)
    {
        StringBuilder sb = new StringBuilder();
        sb.Append("[ ");
        foreach (T item in list)
        {
            sb.Append(item);
            sb.Append(", ");
        }
        sb.Append(']');
        return sb.ToString();
    }

    public static int Remove<T>(this ObservableCollection<T> collection, Func<T, bool> predicate)
    {
        var itemsToRemove = collection.Where(predicate).ToList();
        foreach (T item in itemsToRemove)
        {
            collection.Remove(item);
        }
        return itemsToRemove.Count;
    }

    /// <summary>
    /// Helper method that takes a string as input and returns a DateTime object.
    /// This method can handle date formats such as "04/30", "0430", "04/2030", 
    /// "042030", "42030", "4/2030" and uses the current year as the year value
    /// for the returned DateTime object.
    /// </summary>
    /// <param name="dateString">the month and year string to parse</param>
    /// <returns><see cref="DateTime"/></returns>
    /// <example>
    /// CardData.CreditData.ExpirationDate = response.ExpiryDate.ExtractExpiration();
    /// </example>
    public static DateTime ExtractExpiration(this string dateString)
    {
        if (string.IsNullOrEmpty(dateString))
            return DateTime.Now;

        try
        {
            string yearPrefix = DateTime.Now.Year.ToString().Substring(0, 2);
            string yearSuffix = "00";

            if (dateString.Contains(@"\"))
                dateString = dateString.Replace(@"\", "/");

            if (dateString.Length == 5 && !dateString.Contains("/"))      // Myyyy
            {
                yearSuffix = dateString.Substring(dateString.Length - 2, 2);
                dateString = dateString.PadLeft(6, '0');
            }
            else if (dateString.Length == 4 && !dateString.Contains("/")) // MMyy
            {
                yearSuffix = dateString.Substring(dateString.Length - 2, 2);
                dateString = dateString.PadLeft(4, '0');
            }
            else if (dateString.Length == 3 && !dateString.Contains("/")) // Myy
            {
                yearSuffix = dateString.Substring(dateString.Length - 2, 2);
                dateString = dateString.PadLeft(4, '0');
            }
            else if (dateString.Length > 4)  // MM/yy
                yearSuffix = dateString.Substring(dateString.Length - 2, 2);
            else if (dateString.Length > 3)  // MMyy
                yearSuffix = dateString.Substring(dateString.Length - 2, 2);
            else if (dateString.Length > 2)  // Myy
                yearSuffix = dateString.Substring(dateString.Length - 2, 2);
            else if (dateString.Length > 1)  // should not happen
                yearSuffix = dateString;

            if (!int.TryParse($"{yearPrefix}{yearSuffix}", out int yearBase))
                yearBase = DateTime.Now.Year;

            DateTime result;
            if (DateTime.TryParseExact(dateString, "MM/yy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result))
                return new DateTime(yearBase, result.Month, DateTime.DaysInMonth(yearBase, result.Month));
            else if (DateTime.TryParseExact(dateString, "MMyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result))
                return new DateTime(yearBase, result.Month, DateTime.DaysInMonth(yearBase, result.Month));
            else if (DateTime.TryParseExact(dateString, "M/yy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result))
                return new DateTime(yearBase, result.Month, DateTime.DaysInMonth(yearBase, result.Month));
            else if (DateTime.TryParseExact(dateString, "Myy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result))
                return new DateTime(yearBase, result.Month, DateTime.DaysInMonth(yearBase, result.Month));
            else if (DateTime.TryParseExact(dateString, "MM/yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result))
                return new DateTime(result.Year, result.Month, DateTime.DaysInMonth(DateTime.Now.Year, result.Month));
            else if (DateTime.TryParseExact(dateString, "MMyyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result))
                return new DateTime(result.Year, result.Month, DateTime.DaysInMonth(DateTime.Now.Year, result.Month));
            else if (DateTime.TryParseExact(dateString, "M/yyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result))
                return new DateTime(result.Year, result.Month, DateTime.DaysInMonth(DateTime.Now.Year, result.Month));
            else if (DateTime.TryParseExact(dateString, "Myyyy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result))
                return new DateTime(result.Year, result.Month, DateTime.DaysInMonth(DateTime.Now.Year, result.Month));
            else if (DateTime.TryParseExact(dateString, "yy", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out result))
                return new DateTime(yearBase, 12, DateTime.DaysInMonth(yearBase, 12));
            else
                System.Diagnostics.Debug.WriteLine("[WARNING] ExtractExpiration: Invalid date format.");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ERROR] ExtractExpiration: {ex.Message}");
        }

        return DateTime.Now;
    }

    /// <summary>
    /// Compares two currency amounts formatted as <see cref="string"/>s.
    /// </summary>
    public static bool AreAmountsSimilar(string? amount1, string amount2)
    {
        if (string.IsNullOrEmpty(amount1))
            return false;

        if (TryParseDollarAmount(amount1, out decimal value1) && TryParseDollarAmount(amount2, out decimal value2))
            return value1 == value2;

        // If either parsing fails, consider the amounts not equal.
        return false;
    }

    public static bool TryParseDollarAmount(string amount, out decimal value)
    {
        if (string.IsNullOrEmpty(amount))
        {
            value = 0;
            return false;
        }

        // Remove the dollar sign if present
        string cleanedAmount = amount.Replace(CultureInfo.CurrentCulture.NumberFormat.CurrencySymbol, "").Trim();

        // Attempt to parse the cleaned amount
        return decimal.TryParse(cleanedAmount, System.Globalization.NumberStyles.Float | System.Globalization.NumberStyles.AllowThousands | System.Globalization.NumberStyles.AllowCurrencySymbol, System.Globalization.CultureInfo.CurrentCulture, out value);
        //return decimal.TryParse(cleanedAmount, NumberStyles.Currency, CultureInfo.InvariantCulture, out value);
    }


    /// <summary>
    /// A more accurate averaging method by removing the outliers. 
    /// <c>var median = CalculateMedianAdjustable(ListOfAmounts);</c>
    /// </summary>
    /// <param name="values"><see cref="List{T}"/></param>
    /// <returns>the average of the <param name="values"</returns>
    public static double CalculateMedian(List<double> values)
    {
        if (values == null || values.Count == 0)
            return 0d;

        values.Sort();

        // Find the middle index
        int count = values.Count;
        double medianAverage;

        if (count % 2 == 0)
        {   // Even number of elements: average the two middle elements
            int mid1 = count / 2 - 1;
            int mid2 = count / 2;
            medianAverage = (values[mid1] + values[mid2]) / 2.0;
        }
        else
        {   // Odd number of elements: take the middle element
            int mid = count / 2;
            medianAverage = values[mid];
        }

        return medianAverage;
    }

    /// <summary>
    /// A more accurate averaging method by removing the outliers. 
    /// <c>var median = CalculateMedianAdjustable(ListOfAmounts, ListOfAmounts.Count / 2);</c>
    /// </summary>
    /// <param name="values"><see cref="List{T}"/></param>
    /// <param name="sampleCount">how many of the middle values to sample</param>
    /// <returns>the average of the <param name="values"</returns>
    public static double CalculateMedianAdjustable(List<double> values, int sampleCount)
    {
        if (values == null || values.Count == 0)
            return 0d;

        if (sampleCount <= 0)
            sampleCount = values.Count / 2;

        values.Sort();

        int count = values.Count;

        if (sampleCount >= count && count > 2)
            sampleCount = count - 2;
        else if (sampleCount >= count && count <= 2)
            sampleCount = count - 1;

        // Calculate the starting index of the middle elements
        int startIndex = Math.Abs((count - sampleCount) / 2);

        // Get the middle elements
        var middleElements = values.Skip(startIndex).Take(sampleCount);

        // Calculate the average of the middle elements
        double middleAverage = middleElements.Average();

        return middleAverage;
    }

    /// <summary>
    /// Checks to see if a date is between <paramref name="begin"/> and <paramref name="end"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> if <paramref name="dt"/> is between <paramref name="begin"/> and <paramref name="end"/>, otherwise <c>false</c>
    /// </returns>
    public static bool IsBetween(this DateTime dt, DateTime begin, DateTime end) => dt.Ticks >= begin.Ticks && dt.Ticks <= end.Ticks;

    /// <summary>
    /// Determine if the current time is between two <see cref="TimeSpan"/>s.
    /// </summary>
    /// <param name="ts">DateTime.Now.TimeOfDay</param>
    /// <param name="start">TimeSpan.Parse("23:00:00")</param>
    /// <param name="end">TimeSpan.Parse("02:30:00")</param>
    /// <returns><c>true</c> if between start and end, <c>false</c> otherwise</returns>
    public static bool IsBetween(this TimeSpan ts, TimeSpan start, TimeSpan end)
    {
        // Are we in the same day.
        if (start <= end)
            return ts >= start && ts <= end;

        // Are we on different days.
        return ts >= start || ts <= end;
    }

    /// <summary>
    /// Compares the current <see cref="DateTime.Now.TimeOfDay"/> to the given <paramref name="start"/> and <paramref name="end"/> times.
    /// </summary>
    /// <returns><c>true</c> if between start and end, <c>false</c> otherwise</returns>
    public static bool IsNowBetween(string start = "10:00:00", string end = "14:00:00")
    {
        try
        {
            var tsNow = DateTime.Now.TimeOfDay;
            var tsStart = TimeSpan.Parse(start);
            var tsEnd = TimeSpan.Parse(end);
            if (tsStart <= tsEnd)
                return tsNow >= tsStart && tsNow <= tsEnd;

            return tsNow >= tsStart || tsNow <= tsEnd;
        }
        catch (Exception ex) { Debug.WriteLine($"[ERROR] IsNowBetween: {ex.Message}"); }
        return false;
    }

    /// <summary>
    /// Compares two <see cref="DateTime"/>s ignoring the hours, minutes and seconds.
    /// </summary>
    public static bool AreDatesSimilar(this DateTime? date1, DateTime? date2)
    {
        if (date1 is null && date2 is null)
            return true;

        if (date1 is null || date2 is null)
            return false;

        return date1.Value.Year == date2.Value.Year &&
               date1.Value.Month == date2.Value.Month &&
               date1.Value.Day == date2.Value.Day;
    }

    /// <summary>
    /// Calculates the remaining months in the given <see cref="DateTime"/>.
    /// </summary>
    /// <param name="date"><see cref="DateTime"/></param>
    public static int MonthsTilEndOfInYear(this DateTime? date)
    {
        if (date is null)
            date = DateTime.Now;

        return 12 - date.Value.Month;
    }

    /// <summary>
    /// Calculates the remaining days in the given <see cref="DateTime"/>.
    /// </summary>
    /// <param name="date"><see cref="DateTime"/></param>
    public static int DaysTilEndOfMonth(this DateTime? date)
    {
        if (date is null)
            date = DateTime.Now;

        int daysInMonth = DateTime.DaysInMonth(date.Value.Year, date.Value.Month);
        return daysInMonth - date.Value.Day;
    }

    /// <summary>
    /// Returns a range of <see cref="DateTime"/> objects matching the criteria provided.
    /// </summary>
    /// <example>
    /// IEnumerable<DateTime> dateRange = DateTime.Now.GetDateRangeTo(DateTime.Now.AddDays(80));
    /// </example>
    /// <param name="self"><see cref="DateTime"/></param>
    /// <param name="toDate"><see cref="DateTime"/></param>
    /// <returns><see cref="IEnumerable{DateTime}"/></returns>
    public static IEnumerable<DateTime> GetDateRangeTo(this DateTime self, DateTime toDate)
    {
        // Query Syntax:
        //IEnumerable<int> range = Enumerable.Range(0, new TimeSpan(toDate.Ticks - self.Ticks).Days);
        //IEnumerable<DateTime> dates = from p in range select self.Date.AddDays(p);

        // Method Syntax:
        IEnumerable<DateTime> dates = Enumerable.Range(0, new TimeSpan(toDate.Ticks - self.Ticks).Days).Select(p => self.Date.AddDays(p));

        return dates;
    }

    /// <summary>
    /// Returns an <see cref="Int32"/> amount of days between two <see cref="DateTime"/> objects.
    /// </summary>
    /// <param name="self"><see cref="DateTime"/></param>
    /// <param name="toDate"><see cref="DateTime"/></param>
    public static int GetDaysBetween(this DateTime self, DateTime toDate)
    {
        return new TimeSpan(toDate.Ticks - self.Ticks).Days;
    }

    /// <summary>
    /// Returns a <see cref="TimeSpan"/> amount between two <see cref="DateTime"/> objects.
    /// </summary>
    /// <param name="self"><see cref="DateTime"/></param>
    /// <param name="toDate"><see cref="DateTime"/></param>
    public static TimeSpan GetTimeSpanBetween(this DateTime self, DateTime toDate)
    {
        return new TimeSpan(toDate.Ticks - self.Ticks);
    }

    /// <summary>
    /// Figure out how old something is.
    /// </summary>
    /// <returns>integer amount in years</returns>
    public static int CalculateYearAge(this DateTime dateTime)
    {
        int age = DateTime.Now.Year - dateTime.Year;
        if (DateTime.Now < dateTime.AddYears(age))
        {
            age--;
        }

        return age;
    }

    /// <summary>
    /// Figure out how old something is.
    /// </summary>
    /// <returns>integer amount in months</returns>
    public static int CalculateMonthAge(this DateTime dateTime)
    {
        int age = DateTime.Now.Year - dateTime.Year;
        if (DateTime.Now < dateTime.AddYears(age))
        {
            age--;
        }

        return age * 12;
    }

    /// <summary>
    /// Determines if the given <paramref name="dateTime"/> is older than <paramref name="days"/>.
    /// </summary>
    /// <returns><c>true</c> if older, <c>false</c> otherwise</returns>
    public static bool IsOlderThanDays(this DateTime dateTime, double days = 1.0)
    {
        TimeSpan timeDifference = DateTime.Now - dateTime;
        return timeDifference.TotalDays >= days;
    }

    /// <summary>
    /// Converts <see cref="TimeSpan"/> objects to a simple human-readable string.
    /// e.g. 420 milliseconds, 3.1 seconds, 2 minutes, 4.231 hours, etc.
    /// </summary>
    /// <param name="span"><see cref="TimeSpan"/></param>
    /// <param name="significantDigits">number of right side digits in output (precision)</param>
    /// <returns></returns>
    public static string ToTimeString(this TimeSpan span, int significantDigits = 3)
    {
        var format = $"G{significantDigits}";
        return span.TotalMilliseconds < 1000 ? span.TotalMilliseconds.ToString(format) + " milliseconds"
                : (span.TotalSeconds < 60 ? span.TotalSeconds.ToString(format) + " seconds"
                : (span.TotalMinutes < 60 ? span.TotalMinutes.ToString(format) + " minutes"
                : (span.TotalHours < 24 ? span.TotalHours.ToString(format) + " hours"
                : span.TotalDays.ToString(format) + " days")));
    }

    /// <summary>
    /// Converts <see cref="TimeSpan"/> objects to a simple human-readable string.
    /// e.g. 420 milliseconds, 3.1 seconds, 2 minutes, 4.231 hours, etc.
    /// </summary>
    /// <param name="span"><see cref="TimeSpan"/></param>
    /// <param name="significantDigits">number of right side digits in output (precision)</param>
    /// <returns></returns>
    public static string ToTimeString(this TimeSpan? span, int significantDigits = 3)
    {
        var format = $"G{significantDigits}";
        return span?.TotalMilliseconds < 1000 ? span?.TotalMilliseconds.ToString(format) + " milliseconds"
                : (span?.TotalSeconds < 60 ? span?.TotalSeconds.ToString(format) + " seconds"
                : (span?.TotalMinutes < 60 ? span?.TotalMinutes.ToString(format) + " minutes"
                : (span?.TotalHours < 24 ? span?.TotalHours.ToString(format) + " hours"
                : span?.TotalDays.ToString(format) + " days")));
    }

    /// <summary>
    /// Display a readable sentence as to when the time will happen.
    /// e.g. "in one second" or "in 2 days"
    /// </summary>
    /// <param name="value"><see cref="TimeSpan"/>the future time to compare from now</param>
    /// <returns>human friendly format</returns>
    public static string ToReadableTime(this TimeSpan value)
    {
        double delta = value.TotalSeconds;
        if (delta < 60) { return value.Seconds == 1 ? "one second" : value.Seconds + " seconds"; }
        if (delta < 120) { return "a minute"; }
        if (delta < 3000) { return value.Minutes + " minutes"; } // 50 * 60
        if (delta < 5400) { return "an hour"; } // 90 * 60
        if (delta < 86400) { return value.Hours + " hours"; } // 24 * 60 * 60
        if (delta < 172800) { return "one day"; } // 48 * 60 * 60
        if (delta < 2592000) { return value.Days + " days"; } // 30 * 24 * 60 * 60
        if (delta < 31104000) // 12 * 30 * 24 * 60 * 60
        {
            int months = Convert.ToInt32(Math.Floor((double)value.Days / 30));
            return months <= 1 ? "one month" : months + " months";
        }
        int years = Convert.ToInt32(Math.Floor((double)value.Days / 365));
        return years <= 1 ? "one year" : years + " years";
    }

    /// <summary>
    /// Similar to <see cref="GetReadableTime(TimeSpan)"/>.
    /// </summary>
    /// <param name="timeSpan"><see cref="TimeSpan"/></param>
    /// <returns>formatted text</returns>
    public static string ToReadableString(this TimeSpan span)
    {
        var parts = new StringBuilder();
        if (span.Days > 0)
            parts.Append($"{span.Days} day{(span.Days == 1 ? string.Empty : "s")} ");
        if (span.Hours > 0)
            parts.Append($"{span.Hours} hour{(span.Hours == 1 ? string.Empty : "s")} ");
        if (span.Minutes > 0)
            parts.Append($"{span.Minutes} minute{(span.Minutes == 1 ? string.Empty : "s")} ");
        if (span.Seconds > 0)
            parts.Append($"{span.Seconds} second{(span.Seconds == 1 ? string.Empty : "s")} ");
        if (span.Milliseconds > 0)
            parts.Append($"{span.Milliseconds} millisecond{(span.Milliseconds == 1 ? string.Empty : "s")} ");

        if (parts.Length == 0) // result was less than 1 millisecond
            return $"{span.TotalMilliseconds:N4} milliseconds"; // similar to span.Ticks
        else
            return parts.ToString().Trim();
    }

    /// <summary>
    /// Display a readable sentence as to when that time happened.
    /// e.g. "5 minutes ago" or "in 2 days"
    /// </summary>
    /// <param name="value"><see cref="DateTime"/>the past/future time to compare from now</param>
    /// <returns>human friendly format</returns>
    public static string ToReadableTime(this DateTime value, bool useUTC = false)
    {
        TimeSpan ts;
        if (useUTC) { ts = new TimeSpan(DateTime.UtcNow.Ticks - value.Ticks); }
        else { ts = new TimeSpan(DateTime.Now.Ticks - value.Ticks); }

        double delta = ts.TotalSeconds;
        if (delta < 0) // in the future
        {
            delta = Math.Abs(delta);
            if (delta < 60) { return Math.Abs(ts.Seconds) == 1 ? "in one second" : "in " + Math.Abs(ts.Seconds) + " seconds"; }
            if (delta < 120) { return "in a minute"; }
            if (delta < 3000) { return "in " + Math.Abs(ts.Minutes) + " minutes"; } // 50 * 60
            if (delta < 5400) { return "in an hour"; } // 90 * 60
            if (delta < 86400) { return "in " + Math.Abs(ts.Hours) + " hours"; } // 24 * 60 * 60
            if (delta < 172800) { return "tomorrow"; } // 48 * 60 * 60
            if (delta < 2592000) { return "in " + Math.Abs(ts.Days) + " days"; } // 30 * 24 * 60 * 60
            if (delta < 31104000) // 12 * 30 * 24 * 60 * 60
            {
                int months = Convert.ToInt32(Math.Floor((double)Math.Abs(ts.Days) / 30));
                return months <= 1 ? "in one month" : "in " + months + " months";
            }
            int years = Convert.ToInt32(Math.Floor((double)Math.Abs(ts.Days) / 365));
            return years <= 1 ? "in one year" : "in " + years + " years";
        }
        else // in the past
        {
            if (delta < 60) { return ts.Seconds == 1 ? "one second ago" : ts.Seconds + " seconds ago"; }
            if (delta < 120) { return "a minute ago"; }
            if (delta < 3000) { return ts.Minutes + " minutes ago"; } // 50 * 60
            if (delta < 5400) { return "an hour ago"; } // 90 * 60
            if (delta < 86400) { return ts.Hours + " hours ago"; } // 24 * 60 * 60
            if (delta < 172800) { return "yesterday"; } // 48 * 60 * 60
            if (delta < 2592000) { return ts.Days + " days ago"; } // 30 * 24 * 60 * 60
            if (delta < 31104000) // 12 * 30 * 24 * 60 * 60
            {
                int months = Convert.ToInt32(Math.Floor((double)ts.Days / 30));
                return months <= 1 ? "one month ago" : months + " months ago";
            }
            int years = Convert.ToInt32(Math.Floor((double)ts.Days / 365));
            return years <= 1 ? "one year ago" : years + " years ago";
        }
    }

    /// <summary>
    /// Display a readable sentence as to when the time will happen.
    /// e.g. "8 minutes 0 milliseconds"
    /// </summary>
    /// <param name="milliseconds">integer value</param>
    /// <returns>human friendly format</returns>
    public static string ToReadableTime(int milliseconds)
    {
        if (milliseconds < 0)
            throw new ArgumentException("Milliseconds cannot be negative.");

        TimeSpan timeSpan = TimeSpan.FromMilliseconds(milliseconds);

        if (timeSpan.TotalHours >= 1)
        {
            return string.Format("{0:0} hour{1} {2:0} minute{3}",
                timeSpan.Hours, timeSpan.Hours == 1 ? "" : "s",
                timeSpan.Minutes, timeSpan.Minutes == 1 ? "" : "s");
        }
        else if (timeSpan.TotalMinutes >= 1)
        {
            return string.Format("{0:0} minute{1} {2:0} second{3}",
                timeSpan.Minutes, timeSpan.Minutes == 1 ? "" : "s",
                timeSpan.Seconds, timeSpan.Seconds == 1 ? "" : "s");
        }
        else
        {
            return string.Format("{0:0} second{1} {2:0} millisecond{3}",
                timeSpan.Seconds, timeSpan.Seconds == 1 ? "" : "s",
                timeSpan.Milliseconds, timeSpan.Milliseconds == 1 ? "" : "s");
        }
    }

    public static string ToHoursMinutesSeconds(this TimeSpan ts) => ts.Days > 0 ? (ts.Days * 24 + ts.Hours) + ts.ToString("':'mm':'ss") : ts.ToString("hh':'mm':'ss");

    public static long TimeToTicks(int hour, int minute, int second)
    {
        long MaxSeconds = long.MaxValue / 10000000; // => MaxValue / TimeSpan.TicksPerSecond
        long MinSeconds = long.MinValue / 10000000; // => MinValue / TimeSpan.TicksPerSecond

        // "totalSeconds" is bounded by 2^31 * 2^12 + 2^31 * 2^8 + 2^31,
        // which is less than 2^44, meaning we won't overflow totalSeconds.
        long totalSeconds = (long)hour * 3600 + (long)minute * 60 + (long)second;

        if (totalSeconds > MaxSeconds || totalSeconds < MinSeconds)
            throw new Exception("Argument out of range: TimeSpan too long.");

        return totalSeconds * 10000000; // => totalSeconds * TimeSpan.TicksPerSecond
    }

    /// <summary>
    /// Converts a <see cref="TimeSpan"/> into a human-friendly readable string.
    /// </summary>
    /// <param name="timeSpan"><see cref="TimeSpan"/> to convert (can be negative)</param>
    /// <returns>human-friendly string representation of the given <see cref="TimeSpan"/></returns>
    public static string ToHumanFriendlyString(this TimeSpan timeSpan)
    {
        if (timeSpan == TimeSpan.Zero)
            return "0 seconds";

        bool isNegative = false;
        List<string> parts = new();

        // Check for negative TimeSpan.
        if (timeSpan < TimeSpan.Zero)
        {
            isNegative = true;
            timeSpan = timeSpan.Negate(); // Make it positive for the calculations.
        }

        if (timeSpan.Days > 0)
            parts.Add($"{timeSpan.Days} day{(timeSpan.Days > 1 ? "s" : "")}");
        if (timeSpan.Hours > 0)
            parts.Add($"{timeSpan.Hours} hour{(timeSpan.Hours > 1 ? "s" : "")}");
        if (timeSpan.Minutes > 0)
            parts.Add($"{timeSpan.Minutes} minute{(timeSpan.Minutes > 1 ? "s" : "")}");
        if (timeSpan.Seconds > 0)
            parts.Add($"{timeSpan.Seconds} second{(timeSpan.Seconds > 1 ? "s" : "")}");

        // If no large amounts so far, try milliseconds.
        if (parts.Count == 0 && timeSpan.Milliseconds > 0)
            parts.Add($"{timeSpan.Milliseconds} millisecond{(timeSpan.Milliseconds > 1 ? "s" : "")}");

        // If no milliseconds, use ticks (nanoseconds).
        if (parts.Count == 0 && timeSpan.Ticks > 0)
        {
            // A tick is equal to 100 nanoseconds. While this maps well into units of time
            // such as hours and days, any periods longer than that aren't representable in
            // a succinct fashion, e.g. a month can be between 28 and 31 days, while a year
            // can contain 365 or 366 days. A decade can have between 1 and 3 leap-years,
            // depending on when you map the TimeSpan into the calendar. This is why TimeSpan
            // does not provide a "Years" property or a "Months" property.
            // Internally TimeSpan uses long (Int64) for its values, so:
            //  - TimeSpan.MaxValue = long.MaxValue
            //  - TimeSpan.MinValue = long.MinValue
            parts.Add($"{(timeSpan.Ticks * TimeSpan.TicksPerMicrosecond)} microsecond{((timeSpan.Ticks * TimeSpan.TicksPerMicrosecond) > 1 ? "s" : "")}");
        }

        // Join the sections with commas and "and" for the last one.
        if (parts.Count == 1)
            return isNegative ? $"Negative {parts[0]}" : parts[0];
        else if (parts.Count == 2)
            return isNegative ? $"Negative {string.Join(" and ", parts)}" : string.Join(" and ", parts);
        else
        {
            string lastPart = parts[parts.Count - 1];
            parts.RemoveAt(parts.Count - 1);
            return isNegative ? $"Negative " + string.Join(", ", parts) + " and " + lastPart : string.Join(", ", parts) + " and " + lastPart;
        }
    }


    /// <summary>
    /// uint max = 4,294,967,295 (4.29 Gbps)
    /// </summary>
    /// <returns>formatted bit-rate string</returns>
    public static string FormatBitrate(this uint amount)
    {
        var sizes = new string[]
        {
            "bps",
            "Kbps", // kilo
            "Mbps", // mega
            "Gbps", // giga
            "Tbps", // tera
        };
        var order = amount.OrderOfMagnitude();
        var speed = amount / Math.Pow(1000, order);
        return $"{speed:0.##} {sizes[order]}";
    }

    /// <summary>
    /// ulong max = 18,446,744,073,709,551,615 (18.45 Ebps)
    /// </summary>
    /// <returns>formatted bit-rate string</returns>
    public static string FormatBitrate(this ulong amount)
    {
        var sizes = new string[] 
        { 
            "bps", 
            "Kbps", // kilo
            "Mbps", // mega
            "Gbps", // giga
            "Tbps", // tera
            "Pbps", // peta
            "Ebps", // exa
            "Zbps", // zetta
            "Ybps"  // yotta
        }; 
        var order = amount.OrderOfMagnitude();
        var speed = amount / Math.Pow(1000, order);
        return $"{speed:0.##} {sizes[order]}";
    }

    /// <summary>
    /// Returns the order of magnitude (10^3)
    /// </summary>
    public static int OrderOfMagnitude(this ulong amount) => (int)Math.Floor(Math.Log(amount, 1000));

    /// <summary>
    /// Returns the order of magnitude (10^3)
    /// </summary>
    public static int OrderOfMagnitude(this uint amount) => (int)Math.Floor(Math.Log(amount, 1000));

    /// <summary>
    /// Determines if the date is a working day, weekend, or determine the next workday coming up.
    /// </summary>
    /// <param name="date"><see cref="DateTime"/></param>
    public static bool WorkingDay(this DateTime date)
    {
        return date.DayOfWeek != DayOfWeek.Saturday && date.DayOfWeek != DayOfWeek.Sunday;
    }

    /// <summary>
    /// Determines if the date is on a weekend (i.e. Saturday or Sunday)
    /// </summary>
    /// <param name="date"><see cref="DateTime"/></param>
    public static bool IsWeekend(this DateTime date)
    {
        return date.DayOfWeek == DayOfWeek.Saturday || date.DayOfWeek == DayOfWeek.Sunday;
    }

    /// <summary>
    /// Gets the next date that is not a weekend.
    /// </summary>
    /// <param name="date"><see cref="DateTime"/></param>
    public static DateTime NextWorkday(this DateTime date)
    {
        DateTime nextDay = date.AddDays(1);
        while (!nextDay.WorkingDay())
        {
            nextDay = nextDay.AddDays(1);
        }
        return nextDay;
    }

    /// <summary>
    /// Determine the Next date by passing in a DayOfWeek (i.e. from this date, when is the next Tuesday?)
    /// </summary>
    public static DateTime Next(this DateTime current, DayOfWeek dayOfWeek)
    {
        int offsetDays = dayOfWeek - current.DayOfWeek;
        if (offsetDays <= 0)
        {
            offsetDays += 7;
        }
        DateTime result = current.AddDays(offsetDays);
        return result;
    }

    /// <summary>
    /// Converts a DateTime to a DateTimeOffset with the specified offset
    /// </summary>
    /// <param name="date">The DateTime to convert</param>
    /// <param name="offset">The offset to apply to the date field</param>
    /// <returns>The corresponding DateTimeOffset</returns>
    public static DateTimeOffset ToOffset(this DateTime date, TimeSpan offset) => new DateTimeOffset(date).ToOffset(offset);

    /// <summary>
    /// Accounts for once the <paramref name="date1"/> is past <paramref name="date2"/>
    /// or falls within the amount of <paramref name="days"/>.
    /// </summary>
    public static bool WithinDaysOrPast(this DateTime date1, DateTime date2, double days = 7.0)
    {
        if (date1 > date2) // Account for past-due amounts.
            return true;
        else
        {
            TimeSpan difference = date1 - date2;
            return Math.Abs(difference.TotalDays) <= days;
        }
    }

    /// <summary>
    /// Only accounts for date1 being within range of date2.
    /// </summary>
    public static bool WithinOneDay(this DateTime date1, DateTime date2)
    {
        TimeSpan difference = date1 - date2;
        return Math.Abs(difference.TotalDays) <= 1.0;
    }

    /// <summary>
    /// Only accounts for date1 being within range of date2 by some amount.
    /// </summary>
    public static bool WithinAmountOfDays(this DateTime date1, DateTime date2, double days)
    {
        TimeSpan difference = date1 - date2;
        return Math.Abs(difference.TotalDays) <= days;
    }

    public static DateTime ConvertToLastDayOfMonth(this DateTime date) => new DateTime(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));

    /// <summary>
    /// Multiplies the given <see cref="TimeSpan"/> by the scalar amount provided.
    /// </summary>
    public static TimeSpan Multiply(this TimeSpan timeSpan, double scalar) => new TimeSpan((long)(timeSpan.Ticks * scalar));

    /// <summary>
    /// Gets a <see cref="DateTime"/> object representing the time until midnight.
    /// <example><code>
    /// var hoursUntilMidnight = TimeUntilMidnight().TimeOfDay.TotalHours;
    /// </code></example>
    /// </summary>
    public static DateTime TimeUntilMidnight()
    {
        DateTime now = DateTime.Now;
        DateTime midnight = now.Date.AddDays(1);
        TimeSpan timeUntilMidnight = midnight - now;
        return new DateTime(timeUntilMidnight.Ticks);
    }

    /// <summary>
    /// Converts long file size into typical browser file size.
    /// </summary>
    public static string ToFileSize(this ulong size)
    {
        if (size < 1024) { return (size).ToString("F0") + " Bytes"; }
        if (size < Math.Pow(1024, 2)) { return (size / 1024).ToString("F0") + "KB"; }
        if (size < Math.Pow(1024, 3)) { return (size / Math.Pow(1024, 2)).ToString("F0") + "MB"; }
        if (size < Math.Pow(1024, 4)) { return (size / Math.Pow(1024, 3)).ToString("F0") + "GB"; }
        if (size < Math.Pow(1024, 5)) { return (size / Math.Pow(1024, 4)).ToString("F0") + "TB"; }
        if (size < Math.Pow(1024, 6)) { return (size / Math.Pow(1024, 5)).ToString("F0") + "PB"; }
        return (size / Math.Pow(1024, 6)).ToString("F0") + "EB";
    }

    /// <summary>
    /// Over-engineered using LINQ
    /// </summary>
    public static string ConvertToBinary(this int number) => Enumerable.Range(0, (int)Math.Log(number, 2) + 1).Aggregate(string.Empty, (collected, bitshifts) => ((number >> bitshifts) & 1) + collected);

    /// <summary>
    /// Removes all non-numerics values from a string.
    /// </summary>
    public static string ToNumeric(this string str) => Regex.Replace(str, "[^0-9]", "");

    /// <summary>
    /// Removes all non-numerics values from a string and formats to currency.
    /// </summary>
    public static string ToNumericCurrency(this decimal dec) => Regex.Replace(string.Format("{0:c}", dec), "[^0-9]", "");

    /// <summary>
    /// Returns the AppData path including the <paramref name="moduleName"/>.
    /// e.g. "C:\Users\UserName\AppData\Local\MenuDemo\Settings"
    /// </summary>
    public static string LocalApplicationDataFolder(string moduleName = "Settings")
    {
        var result = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}\\{moduleName}");
        return result;
    }

    /// <summary>
    /// Offers two ways to determine the local app folder.
    /// </summary>
    /// <returns></returns>
    public static string LocalApplicationDataFolder()
    {
        WindowsIdentity? currentUser = WindowsIdentity.GetCurrent();
        SecurityIdentifier? currentUserSID = currentUser.User;
        SecurityIdentifier? localSystemSID = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (currentUserSID != null && currentUserSID.Equals(localSystemSID))
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        }
        else
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }
    }

    /// <summary>
    /// Check if a file can be created in the directory.
    /// </summary>
    /// <param name="directoryPath">the directory path to evaluate</param>
    /// <returns>true if the directory is writable, false otherwise</returns>
    public static bool CanWriteToDirectory(string directoryPath)
    {
        try
        {
            using (FileStream fs = File.Create(Path.Combine(directoryPath, "test.txt"), 1, FileOptions.DeleteOnClose)) { /* no-op */ }
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static void PostWithComplete<T>(this SynchronizationContext context, Action<T> action, T state)
    {
        context.OperationStarted();
        context.Post(o => {
            try { action((T)o!); }
            finally { context.OperationCompleted(); }
        },
            state
        );
    }

    public static void PostWithComplete(this SynchronizationContext context, Action action)
    {
        context.OperationStarted();
        context.Post(_ => {
            try { action(); }
            finally { context.OperationCompleted(); }
        },
            null
        );
    }

    public static string SeparateCamelCase(this string input)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        StringBuilder result = new StringBuilder();
        result.Append(input[0]);

        for (int i = 1; i < input.Length; i++)
        {
            if (char.IsUpper(input[i]))
                result.Append(' ');

            result.Append(input[i]);
        }

        return result.ToString();
    }

    public static int CompareName(this Object obj1, Object obj2)
    {
        if (obj1 is null && obj2 is null)
            return 0;

        PropertyInfo? pi1 = obj1 as PropertyInfo;
        if (pi1 is null)
            return -1;

        PropertyInfo? pi2 = obj2 as PropertyInfo;
        if (pi2 is null)
            return 1;

        return String.Compare(pi1.Name, pi2.Name);
    }

    public const double Epsilon     = 0.000000000001;
    public const double RMS         = 0.707106781186; // 1 ÷ √2
    public const double RootTwo     = 1.414213562373;
    public const double GoldenRatio = 1.618033988749;
    public const double Pi          = 3.141592653589;
    public const double Tau         = 6.283185307179; // 2 x Pi

    /// <summary>
    /// Determine if one number is greater than another.
    /// </summary>
    /// <param name="left">First <see cref="double"/></param>
    /// <param name="right">Second <see cref="double"/></param>
    /// <returns>
    /// True if the first number is greater than the second, false otherwise.
    /// </returns>
    public static bool IsGreaterThan(double left, double right) => (left > right) && !AreClose(left, right);

    /// <summary>
    /// Determine if one number is less than or close to another.
    /// </summary>
    /// <param name="left">First <see cref="double"/></param>
    /// <param name="right">Second <see cref="double"/></param>
    /// <returns>
    /// True if the first number is less than or close to the second, false otherwise.
    /// </returns>
    public static bool IsLessThanOrClose(double left, double right) => (left < right) || AreClose(left, right);

    /// <summary>
    /// Determine if two numbers are close in value.
    /// </summary>
    /// <param name="left">First <see cref="double"/></param>
    /// <param name="right">Second <see cref="double"/></param>
    /// <returns>
    /// True if the first number is close in value to the second, false otherwise.
    /// </returns>
    public static bool AreClose(double left, double right)
    {
        if (left == right)
            return true;

        double a = (Math.Abs(left) + Math.Abs(right) + 10.0) * Epsilon;
        double b = left - right;
        return (-a < b) && (a > b);
    }

    /// <summary>
    /// Consider anything within an order of magnitude of epsilon to be zero.
    /// </summary>
    /// <param name="value">The <see cref="double"/> to check</param>
    /// <returns>
    /// True if the number is zero, false otherwise.
    /// </returns>
    public static bool IsZero(this double value) => Math.Abs(value) < Epsilon;

    public static bool IsInvalid(this double value)
    {
        if (value == double.NaN || value == double.NegativeInfinity || value == double.PositiveInfinity)
            return true;

        return false;
    }

    public static bool IsInvalidOrZero(this double value)
    {
        if (value == double.NaN || value == double.NegativeInfinity || value == double.PositiveInfinity || value <= 0)
            return true;

        return false;
    }

    public static double Mod(this double number, double divider)
    {
        var result = number % divider;
        if (double.IsNaN(result))
            return 0;
        result = result < 0 ? result + divider : result;
        return result;
    }

    /// <summary>
    /// Clamping function for any value of type <see cref="IComparable{T}"/>.
    /// </summary>
    /// <param name="val">initial value</param>
    /// <param name="min">lowest range</param>
    /// <param name="max">highest range</param>
    /// <returns>clamped value</returns>
    public static T Clamp<T>(this T val, T min, T max) where T : IComparable<T> => val.CompareTo(min) < 0 ? min : (val.CompareTo(max) > 0 ? max : val);

    /// <summary>
    /// Linear interpolation for a range of floats.
    /// </summary>
    public static float Lerp(this float start, float end, float amount = 0.5F) => start + (end - start) * amount;
    /// <summary>
    /// Linear interpolation for a range of double.
    /// </summary>
    public static double Lerp(this double start, double end, double amount = 0.5F) => start + (end - start) * amount;

    public static float LogLerp(this float start, float end, float percent, float logBase = 1.2F) => start + (end - start) * MathF.Log(percent, logBase);

    public static double LogLerp(this double start, double end, double percent, double logBase = 1.2F) => start + (end - start) * Math.Log(percent, logBase);

    /// <summary>
    /// Scales a range of integers. [baseMin to baseMax] will become [limitMin to limitMax]
    /// </summary>
    public static int Scale(this int valueIn, int baseMin, int baseMax, int limitMin, int limitMax) => ((limitMax - limitMin) * (valueIn - baseMin) / (baseMax - baseMin)) + limitMin;
    /// <summary>
    /// Scales a range of floats. [baseMin to baseMax] will become [limitMin to limitMax]
    /// </summary>
    public static float Scale(this float valueIn, float baseMin, float baseMax, float limitMin, float limitMax) => ((limitMax - limitMin) * (valueIn - baseMin) / (baseMax - baseMin)) + limitMin;
    /// <summary>
    /// Scales a range of double. [baseMin to baseMax] will become [limitMin to limitMax]
    /// </summary>
    public static double Scale(this double valueIn, double baseMin, double baseMax, double limitMin, double limitMax) => ((limitMax - limitMin) * (valueIn - baseMin) / (baseMax - baseMin)) + limitMin;

    public static int MapValue(this int val, int inMin, int inMax, int outMin, int outMax) => (val - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;

    public static float MapValue(this float val, float inMin, float inMax, float outMin, float outMax) => (val - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;

    public static double MapValue(this double val, double inMin, double inMax, double outMin, double outMax) => (val - inMin) * (outMax - outMin) / (inMax - inMin) + outMin;

    /// <summary>
    /// Used to gradually reduce the effect of certain changes over time.
    /// </summary>
    /// <param name="value">Some initial value, e.g. 40</param>
    /// <param name="target">Where we want the value to end up, e.g. 100</param>
    /// <param name="rate">How quickly we want to reach the target, e.g. 0.25</param>
    /// <returns></returns>
    public static float Dampen(this float value, float target, float rate)
    {
        float dampenedValue = value;
        if (value != target)
        {
            float dampeningFactor = MathF.Pow(1 - MathF.Abs((value - target) / rate), 2);
            dampenedValue = target + ((value - target) * dampeningFactor);
        }
        return dampenedValue;
    }

    public static float GetDecimalPortion(this float number)
    {
        // If the number is negative, make it positive.
        if (number < 0)
            number = -number;

        // Get the integer portion of the number.
        int integerPortion = (int)number;

        // Subtract the integer portion to get the decimal portion.
        float decimalPortion = number - integerPortion;

        return decimalPortion;
    }

    public static int GetDecimalPlacesCount(this string valueString) => valueString.SkipWhile(c => c.ToString(CultureInfo.CurrentCulture) != CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator).Skip(1).Count();

    /// <summary>
    /// This should only be used on instantiated objects, not static objects.
    /// </summary>
    public static string ToStringDump<T>(this T obj)
    {
        const string Seperator = "\r\n";
        const System.Reflection.BindingFlags BindingFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public;

        if (obj is null)
            return string.Empty;

        try
        {
            var objProperties =
                from property in obj?.GetType().GetProperties(BindingFlags)
                where property.CanRead
                select string.Format("{0} : {1}", property.Name, property.GetValue(obj, null));

            return string.Join(Seperator, objProperties);
        }
        catch (Exception ex)
        {
            return $"⇒ Probably a non-instanced object: {ex.Message}";
        }
    }

    /// <summary>
    /// var stack = GeneralExtensions.GetStackTrace(new StackTrace());
    /// </summary>
    public static string GetStackTrace(StackTrace st)
    {
        StringBuilder sb = new();
        for (int i = 0; i < st.FrameCount; i++)
        {
            StackFrame? sf = st.GetFrame(i);
            sb.AppendLine(sf?.GetMethod() + " <== ");
        }
        return $"{sb}";
    }

    public static string Flatten(this Exception? exception)
    {
        var sb = new StringBuilder();
        while (exception != null)
        {
            sb.AppendLine(exception.Message);
            sb.AppendLine(exception.StackTrace);
            exception = exception.InnerException;
        }
        return $"{sb}";
    }

    public static string DumpFrames(this Exception exception)
    {
        var sb = new StringBuilder();
        var st = new StackTrace(exception, true);
        var frames = st.GetFrames();
        foreach (var frame in frames)
        {
            if (frame != null)
            {
                if (frame.GetFileLineNumber() < 1)
                    continue;

                sb.Append($"File: {frame.GetFileName()}")
                  .Append($", Method: {frame.GetMethod()?.Name}")
                  .Append($", LineNumber: {frame.GetFileLineNumber()}")
                  .Append($"{Environment.NewLine}");
            }
        }
        return $"{sb}";
    }

    /// <summary>
    /// Removes any non-hex chars from the <paramref name="input"/>.
    /// </summary>
    public static string FilterNonHex(this string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        //var preFilter = Regex.Replace(input, @"[\p{C}-[\t\r\n]]+", "");

        // ? = allow zero or one occurrence of the "0x" prefix
        string pattern = @"(0x)?[0-9A-Fa-f]+";
        MatchCollection matches = Regex.Matches(input, pattern);
        return string.Join("", matches.Cast<Match>().Select(m => m.Value));
    }

    /// <summary>
    /// Helper for parsing command line arguments.
    /// </summary>
    /// <param name="inputArray"></param>
    /// <returns>string array of args excluding the 1st arg</returns>
    public static string[] IgnoreFirstTakeRest(this string[] inputArray)
    {
        if (inputArray.Length > 1)
            return inputArray.Skip(1).ToArray();
        else
            return inputArray;
    }

    /// <summary>
    /// Helper for parsing command line arguments.
    /// </summary>
    /// <param name="inputArray"></param>
    /// <returns>string array of args excluding the 1st arg</returns>
    public static string[] IgnoreNthTakeRest(this string[] inputArray, int skip = 1)
    {
        if (inputArray.Length > skip)
            return inputArray.Skip(skip).ToArray();
        else
            return inputArray;
    }

    /// <summary>
    /// Returns the first element from a tokenized string, e.g.
    /// Input:"{tag}"  Output:"tag"
    /// </summary>
    /// <example>
    /// var clean = ExtractFirst("{tag}", '{', '}');
    /// </example>
    public static string ExtractFirst(this string text, char start, char end)
    {
        string pattern = @"\" + start + "(.*?)" + @"\" + end; //pattern = @"\{(.*?)\}"
        Match match = Regex.Match(text, pattern);
        if (match.Success)
            return match.Groups[1].Value;
        else
            return "";
    }

    /// <summary>
    /// Returns the last element from a tokenized string, e.g.
    /// Input:"{tag}"  Output:"tag"
    /// </summary>
    /// <example>
    /// var clean = ExtractLast("{tag}", '{', '}');
    /// </example>
    public static string ExtractLast(this string text, char start, char end)
    {
        string pattern = @"\" + start + @"(.*?)\" + end; //pattern = @"\{(.*?)\}"
        MatchCollection matches = Regex.Matches(text, pattern);
        if (matches.Count > 0)
        {
            Match lastMatch = matches[matches.Count - 1];
            return lastMatch.Groups[1].Value;
        }
        else
            return "";
    }

    /// <summary>
    /// Returns all the elements from a tokenized string, e.g.
    /// Input:"{tag}"  Output:"tag"
    /// </summary>
    public static string[] ExtractAll(this string text, char start, char end)
    {
        string pattern = @"\" + start + @"(.*?)\" + end; //pattern = @"\{(.*?)\}"
        MatchCollection matches = Regex.Matches(text, pattern);
        string[] results = new string[matches.Count];
        for (int i = 0; i < matches.Count; i++)
            results[i] = matches[i].Groups[1].Value;

        return results;
    }

    /// <summary>
    /// Returns the specified occurrence of a character in a string.
    /// </summary>
    /// <returns>
    /// Index of requested occurrence if successful, -1 otherwise.
    /// </returns>
    /// <example>
    /// If you wanted to find the second index of the percent character in a string:
    /// int index = "blah%blah%blah".IndexOfNth('%', 2);
    /// </example>
    public static int IndexOfNth(this string input, char character, int position)
    {
        int index = -1;

        if (string.IsNullOrEmpty(input))
            return index;

        for (int i = 0; i < position; i++)
        {
            index = input.IndexOf(character, index + 1);
            if (index == -1)
                break;
        }

        return index;
    }

    /// <summary>
    /// Formatter for time stamping, e.g. "20250123074401943"
    /// </summary>
    /// <returns>yyyyMMddhhmmssff</returns>
    public static string GetTimeStamp() => string.Format(
        System.Globalization.CultureInfo.InvariantCulture,
        "{0:D4}{1:D2}{2:D2}{3:D2}{4:D2}{5:D2}{6:D3}",
        DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, DateTime.Now.Hour, DateTime.Now.Minute, DateTime.Now.Second, DateTime.Now.Millisecond);
    public static string GetTimeStamp12() => $"{DateTime.Now:yyyy-MM-dd hh:mm:ss.fff tt}";
    public static string GetTimeStamp24() => $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}";

    /// <summary>
    /// var collection = new[] { 10, 20, 30 };
    /// collection.ForEach(Debug.WriteLine);
    /// </summary>
    public static void ForEach<T>(this IEnumerable<T> ie, Action<T> action)
    {
        foreach (var i in ie)
        {
            try
            {
                action(i);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] ForEach: {ex.Message}");
            }
        }
    }

    public static IEnumerable<T> JoinLists<T>(this IEnumerable<T> list1, IEnumerable<T> list2)
    {
        var joined = new[] { list1, list2 }.Where(x => x != null).SelectMany(x => x);
        return joined ?? Enumerable.Empty<T>();
    }

    public static IEnumerable<T> JoinLists<T>(this IEnumerable<T> list1, IEnumerable<T> list2, IEnumerable<T> list3)
    {
        var joined = new[] { list1, list2, list3 }.Where(x => x != null).SelectMany(x => x);
        return joined ?? Enumerable.Empty<T>();
    }

    public static IEnumerable<T> JoinMany<T>(params IEnumerable<T>[] array)
    {
        var final = array.Where(x => x != null).SelectMany(x => x);
        return final ?? Enumerable.Empty<T>();
    }

    public static void AddRange<T>(this ICollection<T> target, IEnumerable<T> source)
    {
        if (target == null) { throw new ArgumentNullException(nameof(target)); }
        if (source == null) { throw new ArgumentNullException(nameof(source)); }
        foreach (var element in source) { target.Add(element); }
    }

    /// <summary>
    /// Merges the two input <see cref="IList{T}"/> instances and makes sure no duplicate items are present
    /// </summary>
    /// <typeparam name="T">The type of elements in the input collections</typeparam>
    /// <param name="a">The first <see cref="IList{T}"/> to merge</param>
    /// <param name="b">The second <see cref="IList{T}"/> to merge</param>
    /// <returns>An <see cref="IList{T}"/> instance with elements from both <paramref name="a"/> and <paramref name="b"/></returns>
    public static IList<T> Merge<T>(this IList<T> a, IList<T> b)
    {
        if (a.Any(b.Contains))
            Debug.WriteLine("[WARNING] The input collection has at least an item already present in the second collection");

        return a.Concat(b).ToArray();
    }

    /// <summary>
    /// Merges the two input <see cref="IEnumerable{T}"/> instances and makes sure no duplicate items are present
    /// </summary>
    /// <typeparam name="T">The type of elements in the input collections</typeparam>
    /// <param name="a">The first <see cref="IEnumerable{T}"/> to merge</param>
    /// <param name="b">The second <see cref="IEnumerable{T}"/> to merge</param>
    /// <returns>An <see cref="IEnumerable{T}"/> instance with elements from both <paramref name="a"/> and <paramref name="b"/></returns>
    public static IEnumerable<T> Merge<T>(this IEnumerable<T> a, IEnumerable<T> b)
    {
        if (a.Any(b.Contains))
            Debug.WriteLine("[WARNING] The input collection has at least an item already present in the second collection");

        return a.Concat(b).ToArray();
    }

    /// <summary>
    /// Merges the two input <see cref="IReadOnlyCollection{T}"/> instances and makes sure no duplicate items are present
    /// </summary>
    /// <typeparam name="T">The type of elements in the input collections</typeparam>
    /// <param name="a">The first <see cref="IReadOnlyCollection{T}"/> to merge</param>
    /// <param name="b">The second <see cref="IReadOnlyCollection{T}"/> to merge</param>
    /// <returns>An <see cref="IReadOnlyCollection{T}"/> instance with elements from both <paramref name="a"/> and <paramref name="b"/></returns>
    public static IReadOnlyCollection<T> Merge<T>(this IReadOnlyCollection<T> a, IReadOnlyCollection<T> b)
    {
        if (a.Any(b.Contains))
            Debug.WriteLine("[WARNING] The input collection has at least an item already present in the second collection");

        return a.Concat(b).ToArray();
    }

    /// <summary>
    /// Creates a new <see cref="Span{T}"/> over an input <see cref="List{T}"/> instance.
    /// </summary>
    /// <typeparam name="T">The type of elements in the input <see cref="List{T}"/> instance.</typeparam>
    /// <param name="list">The input <see cref="List{T}"/> instance.</param>
    /// <returns>A <see cref="Span{T}"/> instance with the values of <paramref name="list"/>.</returns>
    /// <remarks>
    /// Note that the returned <see cref="Span{T}"/> is only guaranteed to be valid as long as the items within
    /// <paramref name="list"/> are not modified. Doing so might cause the <see cref="List{T}"/> to swap its
    /// internal buffer, causing the returned <see cref="Span{T}"/> to become out of date. That means that in this
    /// scenario, the <see cref="Span{T}"/> would end up wrapping an array no longer in use. Always make sure to use
    /// the returned <see cref="Span{T}"/> while the target <see cref="List{T}"/> is not modified.
    /// </remarks>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Span<T> AsSpan<T>(this List<T>? list)
    {
        return CollectionsMarshal.AsSpan(list);
    }

    /// <summary>
    /// Returns a simple string representation of an array.
    /// </summary>
    /// <typeparam name="T">The element type of the array.</typeparam>
    /// <param name="array">The source array.</param>
    /// <returns>The <see cref="string"/> representation of the array.</returns>
    public static string ToArrayString<T>(this T?[] array)
    {
        // The returned string will be in the following format: [1, 2, 3]
        StringBuilder builder = new StringBuilder();
        builder.Append('[');
        for (int i = 0; i < array.Length; i++)
        {
            if (i != 0)
                builder.Append(",\t");

            builder.Append(array[i]?.ToString());
        }
        builder.Append(']');
        return builder.ToString();
    }

    /// <summary>
    /// Helper for web images.
    /// </summary>
    /// <returns><see cref="Stream"/></returns>
    public static async Task<Stream> CopyStream(this HttpContent source)
    {
        var stream = new MemoryStream();
        await source.CopyToAsync(stream);
        stream.Seek(0, SeekOrigin.Begin);
        return stream;
    }

    /// <summary>
    /// IEnumerable file reader.
    /// </summary>
    public static IEnumerable<string> ReadFileLines(string path)
    {
        string? line = string.Empty;

        if (!File.Exists(path))
            yield return line;
        else
        {
            using (TextReader reader = File.OpenText(path))
            {
                while ((line = reader.ReadLine()) != null)
                {
                    yield return line;
                }
            }
        }
    }

    /// <summary>
    /// IAsyncEnumerable file reader.
    /// </summary>
    public static async IAsyncEnumerable<string> ReadFileLinesAsync(string path)
    {
        string? line = string.Empty;

        if (!File.Exists(path))
            yield return line;
        else
        {
            using (TextReader reader = File.OpenText(path))
            {
                while ((line = await reader.ReadLineAsync()) != null)
                {
                    yield return line;
                }
            }
        }
    }

    /// <summary>
    /// File writer for <see cref="IEnumerable{T}"/> parameters.
    /// </summary>
    public static bool WriteFileLines(string path, IEnumerable<string> lines)
    {
        using (TextWriter writer = File.CreateText(path))
        {
            foreach (var line in lines)
            {
                writer.WriteLine(line);
            }
        }

        return true;
    }

    /// <summary>
    /// De-dupe file reader using a <see cref="HashSet{T}"/>.
    /// </summary>
    public static HashSet<string> ReadLines(string path)
    {
        if (!File.Exists(path))
            return new();

        return new HashSet<string>(File.ReadAllLines(path), StringComparer.InvariantCultureIgnoreCase);
    }

    /// <summary>
    /// De-dupe file writer using a <see cref="HashSet{T}"/>.
    /// </summary>
    public static bool WriteLines(string path, IEnumerable<string> lines)
    {
        var output = new HashSet<string>(lines, StringComparer.InvariantCultureIgnoreCase);

        using (TextWriter writer = File.CreateText(path))
        {
            foreach (var line in output)
            {
                writer.WriteLine(line);
            }
        }
        return true;
    }

    /// <summary>
    /// To populate parameters with a typical URI assigning format.
    /// This method assumes the format is like "mode=1,state=2,theme=dark"
    /// </summary>
    public static Dictionary<string, string> ParseAssignedValues(string inputString, string delimiter = ",")
    {
        Dictionary<string, string> parameters = new();

        try
        {
            var parts = inputString.Split(delimiter, StringSplitOptions.RemoveEmptyEntries);
            parameters = parts.Select(x => x.Split("=")).ToDictionary(x => x.First(), x => x.Last());
        }
        catch (Exception ex) { Debug.WriteLine($"[ERROR] ParseAssignedValues: {ex.Message}"); }

        return parameters;
    }

    /// <summary>
    /// Helper method for evaluating passed arguments.
    /// </summary>
    public static void PopulateArgDictionary(this string[]? argArray, ref Dictionary<string, string> dict)
    {
        if (argArray != null)
        {
            for (int i = 0; i < argArray.Length; i++)
            {
                var item = argArray[i].Split(" ", StringSplitOptions.RemoveEmptyEntries);
                if (item.Length % 2 == 0)
                    dict[item[0]] = item[1];
                else
                    Debug.WriteLine($"[WARNING] Index {i} has an odd number of segments.", $"{nameof(Extensions)}");
            }
        }
        else { Debug.WriteLine($"[WARNING] {nameof(argArray)} was null.", $"{nameof(Extensions)}"); }

        // To populate parameters with a typical URI assigning format...
        //string sampleString = "mode=1,state=2,theme=dark";
        //var parameters = Extensions.ParseAssignedValues(sampleString);
        //var code = parameters["mode"];
        //var state = parameters["state"];
        //var theme = parameters["theme"];
    }

    /// <summary>
    /// <example><code>
    /// Dictionary<char, int> charCount = GetCharacterCount("some input text string here");
    /// foreach (var kvp in charCount) { Debug.WriteLine($"Character: {kvp.Key}, Count: {kvp.Value}"); }
    /// </code></example>
    /// </summary>
    /// <param name="input">the text string to analyze</param>
    /// <returns><see cref="Dictionary{TKey, TValue}"/></returns>
    public static Dictionary<char, int> GetCharacterCount(this string input)
    {
        Dictionary<char, int> charCount = new();

        if (string.IsNullOrEmpty(input))
            return charCount;

        foreach (var ch in input)
        {
            if (charCount.ContainsKey(ch))
                charCount[ch]++;
            else
                charCount[ch] = 1;
        }

        return charCount;
    }

    public static T? DeserializeFromFile<T>(string filePath, ref string error)
    {
        try
        {
            string jsonString = System.IO.File.ReadAllText(filePath);
            T? result = System.Text.Json.JsonSerializer.Deserialize<T>(jsonString);
            error = string.Empty;
            return result;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] {nameof(DeserializeFromFile)}: {ex.Message}");
            error = ex.Message;
            return default;
        }
    }

    public static bool SerializeToFile<T>(T obj, string filePath, ref string error)
    {
        if (obj == null || string.IsNullOrEmpty(filePath))
            return false;

        try
        {
            string jsonString = System.Text.Json.JsonSerializer.Serialize(obj);
            System.IO.File.WriteAllText(filePath, jsonString);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] {nameof(SerializeToFile)}: {ex.Message}");
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// This method will find all occurrences of a string pattern that starts with a double 
    /// quote, followed by any number of characters (non-greedy), and ends with a double 
    /// quote followed by zero or more spaces and a colon. This pattern matches the typical 
    /// format of keys in a JSON string.
    /// </summary>
    /// <param name="jsonString">JSON formatted text</param>
    /// <returns><see cref="List{T}"/> of each key</returns>
    public static List<string> ExtractKeys(string jsonString)
    {
        var keys = new List<string>();
        var matches = Regex.Matches(jsonString, "[,\\{]\"(.*?)\"\\s*:");
        foreach (Match match in matches) { keys.Add(match.Groups[1].Value); }
        return keys;
    }

    /// <summary>
    /// This method will find all occurrences of a string pattern that starts with a colon, 
    /// followed by zero or more spaces, followed by any number of characters (non-greedy), 
    /// and ends with a comma, closing brace, or closing bracket. This pattern matches the 
    /// typical format of values in a JSON string.
    /// </summary>
    /// <param name="jsonString">JSON formatted text</param>
    /// <returns><see cref="List{T}"/> of each value</returns>
    public static List<string> ExtractValues(string jsonString)
    {
        var values = new List<string>();
        var matches = Regex.Matches(jsonString, ":\\s*(.*?)(,|}|\\])");
        foreach (Match match in matches) { values.Add(match.Groups[1].Value.Trim()); }
        return values;
    }

    /// <summary>
    /// Convert a <see cref="DateTime"/> object into an ISO 8601 formatted string.
    /// </summary>
    /// <param name="dateTime"><see cref="DateTime"/></param>
    /// <returns>ISO 8601 formatted string</returns>
    public static string ToJsonFriendlyFormat(this DateTime dateTime)
    {
        return dateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TValue CastTo<TValue>(TValue value) where TValue : unmanaged
    {
        return (TValue)(object)value;
    }

    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static TValue? CastToNullable<TValue>(TValue? value) where TValue : unmanaged
    {
        if (value is null)
            return null;

        TValue validValue = value.GetValueOrDefault();
        return (TValue)(object)validValue;
    }

    /// <summary>
    ///   Brute force alpha removal of <see cref="Version"/> text
    ///   is not always the best approach, e.g. the following:
    ///   "3.0.0-zmain.2211 (DCPP(199ff10ec000000)(cloudtest).160101.0800)"
    ///   ...converts to: 
    ///   "3.0.0.221119910000000.160101.0800" 
    ///   ...which is not accurate.
    /// </summary>
    /// <param name="fullPath">the entire path to the file</param>
    /// <returns>sanitized <see cref="Version"/></returns>
    public static Version GetFileVersion(this string fullPath)
    {
        try
        {
            var ver = System.Diagnostics.FileVersionInfo.GetVersionInfo(fullPath).FileVersion;
            if (string.IsNullOrEmpty(ver)) { return new Version(); }
            if (ver.HasSpace())
            {   // Some assemblies contain versions such as "10.0.22622.1030 (WinBuild.160101.0800)"
                // This will cause the Version constructor to throw an exception, so just take the first piece.
                var chunk = ver.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var firstPiece = Regex.Replace(chunk[0].Replace(',', '.'), "[^.0-9]", "");
                return new Version(firstPiece);
            }
            string cleanVersion = Regex.Replace(ver, "[^.0-9]", "");
            return new Version(cleanVersion);
        }
        catch (Exception)
        {
            return new Version(); // 0.0
        }
    }

    public static bool HasAlpha(this string str)
    {
        if (string.IsNullOrEmpty(str)) { return false; }
        return str.Any(x => char.IsLetter(x));
    }
    public static bool HasAlphaRegex(this string str)
    {
        return Regex.IsMatch(str ?? "", @"[+a-zA-Z]+");
    }

    public static bool HasNumeric(this string str)
    {
        if (string.IsNullOrEmpty(str)) { return false; }
        return str.Any(x => char.IsNumber(x));
    }
    public static bool HasNumericRegex(this string str)
    {
        return Regex.IsMatch(str ?? "", @"[0-9]+"); // [^\D+]
    }

    public static bool HasSpace(this string str)
    {
        if (string.IsNullOrEmpty(str)) { return false; }
        return str.Any(x => char.IsSeparator(x));
    }
    public static bool HasSpaceRegex(this string str)
    {
        return Regex.IsMatch(str ?? "", @"[\s]+");
    }

    public static bool HasPunctuation(this string str)
    {
        if (string.IsNullOrEmpty(str)) { return false; }
        return str.Any(x => char.IsPunctuation(x));
    }

    public static bool HasAlphaNumeric(this string str)
    {
        if (string.IsNullOrEmpty(str)) { return false; }
        return str.Any(x => char.IsNumber(x)) && str.Any(x => char.IsLetter(x));
    }
    public static bool HasAlphaNumericRegex(this string str)
    {
        return Regex.IsMatch(str ?? "", "[a-zA-Z0-9]+");
    }

    public static string RemoveAlphas(this string str)
    {
        return string.Concat(str?.Where(c => char.IsNumber(c) || c == '.') ?? string.Empty);
    }

    public static string RemoveNumerics(this string str)
    {
        return string.Concat(str?.Where(c => char.IsLetter(c)) ?? string.Empty);
    }

    public static string RemoveDiacritics(this string strThis)
    {
        if (string.IsNullOrEmpty(strThis))
            return string.Empty;

        var sb = new StringBuilder();

        foreach (char c in strThis.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString();
    }

    public static string RemoveLastSeparator(this string input, string separator)
    {
        if (string.IsNullOrEmpty(input))
            return input;

        int lastDelimiterIndex = input.LastIndexOf(separator);

        if (lastDelimiterIndex >= 0 && lastDelimiterIndex == input.Length - 1)
            return input.Substring(0, lastDelimiterIndex);

        return input;
    }

    /// <summary>
    /// <code>
    ///  var u1 = Extensions.GetNextUniquePath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "resources.pri"), false);
    ///  var u2 = Extensions.GetNextUniquePath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets"), true);
    /// </code>
    /// </summary>
    public static string GetNextUniquePath(this string path, bool isFolder = false)
    {
        string unique = path;
        if (System.IO.File.Exists(path) || System.IO.Directory.Exists(path))
        {
            string Name = isFolder ? System.IO.Path.GetFileName(path) : System.IO.Path.GetFileNameWithoutExtension(path);
            string Extension = isFolder ? string.Empty : System.IO.Path.GetExtension(path);
            string DirectoryPath = System.IO.Path.GetDirectoryName(path) ?? "C:\\";
            for (ushort Count = 1; System.IO.Directory.Exists(unique) || System.IO.File.Exists(unique); Count++)
            {
                // Check if matches "File(1).txt", "File(2).txt", "File(3).txt", etc.
                if (System.Text.RegularExpressions.Regex.IsMatch(Name, @".*\(\d+\)"))
                    unique = System.IO.Path.Combine(DirectoryPath, $"{Name.Substring(0, Name.LastIndexOf("(", StringComparison.InvariantCultureIgnoreCase))}({Count}){Extension}");
                else
                    unique = System.IO.Path.Combine(DirectoryPath, $"{Name}({Count}){Extension}");
            }
        }
        return unique;
    }


    /// <summary>
    ///   Fetch all <see cref="ProcessModule"/>s in the current running process.
    /// </summary>
    /// <param name="excludeWinSys">if <c>true</c> any file path starting with %windir% will be excluded from the results</param>
    public static List<string> GatherReferenceAssemblies(bool excludeWinSys)
    {
        List<string> modules = new();
        var winSys = Environment.GetFolderPath(Environment.SpecialFolder.Windows) ?? "N/A";
        var winProg = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) ?? "N/A";
        try
        {
            var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                var fn = module.FileName ?? "Empty";
                if (excludeWinSys && !fn.StartsWith(winSys, StringComparison.OrdinalIgnoreCase) && !fn.StartsWith(winProg, StringComparison.OrdinalIgnoreCase))
                    modules.Add($"{Path.GetFileName(fn)} (v{GetFileVersion(fn)})");
                else if (!excludeWinSys)
                    modules.Add($"{Path.GetFileName(fn)} (v{GetFileVersion(fn)})");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] GatherReferencedAssemblies: {ex.Message}");
        }
        return modules;
    }

    public static async Task<int> WatchProcessUntilExit(int processId, CancellationToken token)
    {
        try
        {
            Process handle = Process.GetProcessById(processId);
            if (handle == null) { return -1; }
            try { await handle.WaitForExitAsync(token); }
            catch (TaskCanceledException) { /* Nothing to do, normal operation */ }
            if (handle.HasExited) { return handle.ExitCode; }
            return -1;
        }
        catch (Exception ex)
        {
            App.DebugLog($"Couldn't add a waiter for process to exit (PID {processId}): {ex.Message}");
            return -1;
        }
    }

    /// <summary>
    ///   Returns a random enum of type <typeparamref name="T"/>.
    /// </summary>
    public static T GetRandomEnum<T>() where T : Enum
    {
        var values = Enum.GetValues(typeof(T)).Cast<T>();
        return values.ElementAt(Random.Shared.Next(values.Count()));
    }

    /// <summary>
    ///   Removes all elements from the specified index to the end of the list.
    /// </summary>
    public static List<T> RemoveFrom<T>(this List<T> list, int index)
    {
        if (!list.Any())
            return list;

        return index <= 0 ? [] : list.Take(index - 1).ToList();
    }

    public static T[] CloneArray<T>(this T[] array)
    {
        var clonedArray = new T[array.Length];
        Array.Copy(array, 0, clonedArray, 0, array.Length);

        return clonedArray;
    }

    public static Task<IList<T>> ToListAsync<T>(this IEnumerable<T> source)
    {
        return Task.Run(() => (IList<T>)source.ToList());
    }

    /// <summary>
    ///   Determines whether <paramref name="enumerable"/> is empty or not (this function is null-safe).
    /// </summary>
    /// <remarks>
    ///   This function is faster than enumerable.Count == 0 since it will only iterate one element instead of all elements.
    /// </remarks>
    public static bool IsEmpty<T>(this IEnumerable<T> enumerable)
    {
        return enumerable is null || !enumerable.Any();
    }

    [return: NotNullIfNotNull(nameof(defaultValue))]
    public static TOut? TryCast<TOut>(this object? value, Func<TOut>? defaultValue = null)
    {
        if (value is TOut outValue)
            return outValue;

        return defaultValue is not null ? defaultValue() : default;
    }

    /// <summary>
    ///   Returns <c>true</c> if within 10 minutes +/- of 2AM, <c>false</c> otherwise.
    /// </summary>
    public static bool IsCloseTo2AM()
    {
        DateTime now = DateTime.Now;
        // "now.Date" will always return midnight
        DateTime threeAM = now.Date.AddHours(2);
        DateTime tenMinutesBefore = threeAM.AddMinutes(-10);
        DateTime tenMinutesAfter = threeAM.AddMinutes(10);
        return now >= tenMinutesBefore && now <= tenMinutesAfter;
    }

    /// <summary>
    ///   Determines the type of an image file by inspecting its header.
    /// </summary>
    /// <param name="filePath">Path to the image file.</param>
    /// <returns>The type of the image (e.g., "jpg", "png", "gif", etc.) or "Unknown" if not recognized.</returns>
    public static string DetermineImageType(this string imageFilePath, bool dumpHeader = false)
    {
        if (!File.Exists(imageFilePath)) { return string.Empty; }

        try
        {
            using (var stream = new FileStream(imageFilePath, FileMode.Open, FileAccess.Read))
            {
                using (var reader = new BinaryReader(stream))
                {
                    byte[] header = reader.ReadBytes(16);

                    if (dumpHeader)
                    {
                        Debug.WriteLine($"[IMAGE HEADER]");
                        foreach (var b in header)
                        {
                            if (b > 31)
                                Debug.Write($"{(char)b}");
                        }
                        Debug.WriteLine($"");
                    }
                    // Check for JPEG signature (bytes 6-9 should be 'J', 'F', 'I', 'F' or 'E' 'x' 'i' 'f')
                    if (header.Length >= 10 &&
                        header[6] == 'J' && header[7] == 'F' && header[8] == 'I' && header[9] == 'F')
                    {
                        return "jpg";
                    }
                    if (header.Length >= 9 &&
                        header[6] == 'E' &&
                       (header[7] == 'x' || header[7] == 'X') &&
                       (header[8] == 'i' || header[8] == 'I') &&
                       (header[9] == 'f' || header[7] == 'F'))
                    {
                        return "jpg";
                    }
                    if (header.Length >= 9 &&
                        header[6] == 'J' &&
                       (header[7] == 'P' || header[7] == 'p') &&
                       (header[8] == 'E' || header[8] == 'e') &&
                       (header[9] == 'G' || header[9] == 'g'))
                    {
                        return "jpg";
                    }
                    // Check for PNG signature (bytes 0-7: 89 50 4E 47 0D 0A 1A 0A)
                    if (header.Length >= 8 &&
                        header[0] == 0x89 && header[1] == 0x50 && header[2] == 0x4E &&
                        header[3] == 0x47 && header[4] == 0x0D && header[5] == 0x0A &&
                        header[6] == 0x1A && header[7] == 0x0A)
                    {
                        return "png";
                    }
                    // Check for GIF signature (bytes 0-2: "GIF")
                    if (header.Length >= 6 &&
                        header[0] == 'G' && header[1] == 'I' && header[2] == 'F')
                    {
                        return "gif";
                    }
                    // Check for BMP signature (bytes 0-1: "BM")
                    if (header.Length >= 2 &&
                        header[0] == 'B' && header[1] == 'M')
                    {
                        return "bmp";
                    }
                    // Check for TIFF signature (bytes 0-3: "II*" or "MM*")
                    if (header.Length >= 4 &&
                        ((header[0] == 'I' && header[1] == 'I' && header[2] == 0x2A && header[3] == 0x00) ||
                         (header[0] == 'M' && header[1] == 'M' && header[2] == 0x00 && header[3] == 0x2A)))
                    {
                        return "tiff";
                    }
                    // Check for WebP signature (bytes 0-3: "RIFF", bytes 8-11: "WEBP")
                    if (header.Length >= 12 &&
                        header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F' &&
                        header[8] == 'W' && header[9] == 'E' && header[10] == 'B' && header[11] == 'P')
                    {
                        return "webp";
                    }
                    // Check for HEIC/HEIF signature (bytes 4-11: "ftypheic" or "ftypheif")
                    if (header.Length >= 12 &&
                        header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p' &&
                       (header[8] == 'h' && header[9] == 'e' && header[10] == 'i' && header[11] == 'c'))
                    {
                        return "heic";
                    }
                    if (header.Length >= 12 &&
                        header[4] == 'f' && header[5] == 't' && header[6] == 'y' && header[7] == 'p' &&
                       (header[8] == 'h' && header[9] == 'e' && header[10] == 'i' && header[11] == 'f'))
                    {
                        return "heif";
                    }
                    // Signature not defined
                    return "Unknown";
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] DetermineImageType: {ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>
    ///   Determines how similar <paramref name="s1"/> is to <paramref name="s2"/> by computing the Jaccard Similarity.
    /// </summary>
    /// <returns>
    ///   0.00 to 0.99
    /// </returns>
    /// <remarks>
    ///   The lower the score the closer they are to being identical, 0.0 = identical
    /// </remarks>
    public static double GetJaccardSimilarity(string s1, string s2)
    {
        var set1 = new HashSet<string>(s1.Split(' '));
        var set2 = new HashSet<string>(s2.Split(' '));
        var intersection = set1.Intersect(set2).Count();
        var union = set1.Union(set2).Count();
        var score = (double)intersection / (double)union;
        Debug.WriteLine($"[INFO] Jaccard similarity score: {score:N2}");
        return score;
    }

    /// <summary>
    ///   Computes the Levenshtein Distance between two strings.
    /// </summary>
    /// <remarks>
    ///   The lower the score the closer they are to being identical, e.g. 0 = identical
    /// </remarks>
    public static int GetLevenshteinDistance(string s1, string s2)
    {
        int len1 = s1.Length;
        int len2 = s2.Length;
        int[,] dp = new int[len1 + 1, len2 + 1];

        for (int i = 0; i <= len1; i++)
            dp[i, 0] = i;

        for (int j = 0; j <= len2; j++)
            dp[0, j] = j;

        for (int i = 1; i <= len1; i++)
        {
            for (int j = 1; j <= len2; j++)
            {
                int cost = s1[i - 1] == s2[j - 1] ? 0 : 1;

                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);
            }
        }
        Debug.WriteLine($"[INFO] Levenshtein score: {dp[len1, len2]}");
        return dp[len1, len2];
    }

    /// <summary>
    ///   Computes the Damerau-Levenshtein Distance between two strings.
    ///   Detects insertions, deletions, and substitutions, similar to Levenshtein,
    ///   but can also handle adjacent character swaps (transpositions).
    /// </summary>
    /// <remarks>
    ///   The lower the score the closer they are to being identical, e.g. 0 = identical
    /// </remarks>
    public static int GetDamerauLevenshteinDistance(string s1, string s2)
    {
        int len1 = s1.Length;
        int len2 = s2.Length;
        int[,] dp = new int[len1 + 1, len2 + 1];

        for (int i = 0; i <= len1; i++)
            dp[i, 0] = i;

        for (int j = 0; j <= len2; j++)
            dp[0, j] = j;

        for (int i = 1; i <= len1; i++)
        {
            for (int j = 1; j <= len2; j++)
            {
                int cost = s1[i - 1] == s2[j - 1] ? 0 : 1;

                dp[i, j] = Math.Min(Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1), dp[i - 1, j - 1] + cost);

                // Check for transpositions
                if (i > 1 && j > 1 && s1[i - 1] == s2[j - 2] && s1[i - 2] == s2[j - 1])
                    dp[i, j] = Math.Min(dp[i, j], dp[i - 2, j - 2] + cost);
            }
        }
        Debug.WriteLine($"[INFO] Damerau-Levenshtein score: {dp[len1, len2]}");
        return dp[len1, len2];
    }

    /// <summary><para>
    ///   Basic key/pswd generator for unique IDs. This employs the standard 
    ///   MS key table which accounts for the 36 Latin letters and Arabic 
    ///   numerals used in most Western European languages.
    /// </para><para>
    ///   24 chars are favored: 2346789 BCDFGHJKMPQRTVWXY
    /// </para><para>
    ///   12 chars are avoided: 015 AEIOU LNSZ
    /// </para><para>
    ///   Only 2 chars are occasionally mistaken: 8 & B (depends on the font).
    /// </para><para>
    ///   The base of possible codes is large (about 3.2 * 10^34).
    /// </para></summary>
    public static string GenerateKeyCode(int length = 8)
    {
        const string pwChars = "2346789BCDFGHJKMPQRTVWXY";
        if (length < 1)
            length = 1;

        char[] charArray = pwChars.Distinct().ToArray();

        var result = new char[length];

        for (int x = 0; x < length; x++)
            result[x] = pwChars[Random.Shared.Next() % pwChars.Length]; //-or- "result[x] = pwChars[Random.Shared.Next(pwChars.Length)];"

        return (new string(result));
    }

    /// <summary>
    /// Basic attenuation function for light sources.
    /// </summary>
    public static float Attenuate(float distance, float radius, float maxIntensity, float falloff)
    {
        var tmp1 = distance / radius;
        if (tmp1 >= 1f) { return 0f; }
        var tmp2 = Square(tmp1);
        return maxIntensity * Square(1f - tmp2) / (1 + falloff * tmp1);
        float Square(float n) => n * n;
    }

    /// <summary>
    /// Utilizes MD5 for <paramref name="fullPath"/>'s checksum.
    /// </summary>
    public static string CalculateFileChecksum(string fullPath)
    {
        if (!File.Exists(fullPath))
            return string.Empty;
        
        var buffer = Encoding.UTF8.GetBytes(fullPath);
        // Reserved word "stackalloc" (flexibility added in C# v7.2)
        // The memory allocated using stackalloc is only valid within
        // the scope of the current method or block. Once the method
        // exits, the memory is automatically deallocated.
        // The stack will always be faster than the heap, but use
        // stackalloc with care, as it bypasses garbage collection
        // and requires manual memory management.
        Span<byte> hash = stackalloc byte[MD5.HashSizeInBytes];
        MD5.HashData(buffer, hash);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async static Task<string> CreateMD5Async(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await MD5.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(bytes).ToLower();
    }

    public async static Task<string> CreateSHA1Async(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await SHA1.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(bytes).ToLower();
    }

    public async static Task<string> CreateSHA256Async(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(bytes).ToLower();
    }

    public async static Task<string> CreateSHA384Async(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await SHA384.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(bytes).ToLower();
    }

    public async static Task<string> CreateSHA512Async(Stream stream, CancellationToken cancellationToken = default)
    {
        var bytes = await SHA512.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(bytes).ToLower();
    }

    /// <summary>
    /// <code>
    ///   ulong currentPosition = 0;
    ///   using (FileStream input = File.OpenRead(inFileName))
    ///   {
    ///      ulong TotalSize = Convert.ToUInt64(input.Length);
    ///      using (FileStream output = File.Open(outFileName, FileMode.OpenOrCreate, FileAccess.Write))
    ///      {
    ///         input.CopyTo(output, Convert.ToInt64(input.Length), token, (s, e) =>
    ///         {
    ///            Debug.WriteLine($" Progress: " + Convert.ToString(Math.Ceiling((currentPosition + Convert.ToUInt64(e.ProgressPercentage / 100d * input.Length)) * 100d / TotalSize)));
    ///         });
    ///      }
    ///   }
    /// </code>
    /// </summary>
    public static void CopyTo(this Stream From, Stream To, long Length = -1, CancellationToken CancelToken = default, ProgressChangedEventHandler? ProgressHandler = null)
    {
        if (From is null)
            throw new ArgumentNullException(nameof(From), "Argument cannot be null");
        if (To is null)
            throw new ArgumentNullException(nameof(To), "Argument cannot be null");

        try
        {
            long TotalBytesRead = 0;
            long TotalBytesLength = Length > 0 ? Length : From.Length;
            byte[] DataBuffer = new byte[4096];
            int ProgressValue = 0;
            int BytesRead = 0;

            while ((BytesRead = From.Read(DataBuffer, 0, DataBuffer.Length)) > 0)
            {
                To.Write(DataBuffer, 0, BytesRead);
                TotalBytesRead += BytesRead;
                if (TotalBytesLength > 1024 * 1024)
                {
                    int LatestValue = Math.Min(100, Math.Max(0, Convert.ToInt32(Math.Ceiling(TotalBytesRead * 100d / TotalBytesLength))));
                    if (LatestValue > ProgressValue)
                    {
                        ProgressValue = LatestValue;
                        ProgressHandler?.Invoke(null, new ProgressChangedEventArgs(LatestValue, null));
                    }
                }
                CancelToken.ThrowIfCancellationRequested();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            From?.CopyTo(To);
        }
        finally
        {
            To?.Flush();
            ProgressHandler?.Invoke(null, new ProgressChangedEventArgs(100, null));
        }
    }

    #region [Spans]
    /// <summary>
    /// Finds the first occurrence of a value within a span.
    /// </summary>
    public static int FindFirstIndexOf<T>(this Span<T> span, T value) where T : IEquatable<T>
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (span[i].Equals(value))
                return i;
        }
        return -1; // Not found
    }

    /// <summary>
    /// Finds the last occurrence of a value within a span.
    /// </summary>
    /// <returns>the index of the first occurrence, otherwise -1 for not found</returns>
    public static int FindLastIndexOf<T>(this Span<T> span, T value) where T : IEquatable<T>
    {
        for (int i = span.Length-1; i > -1; i--)
        {
            if (span[i].Equals(value))
                return i;
        }
        return -1; // Not found
    }

    /// <summary>
    /// Reverses the elements within a span.
    /// </summary>
    public static void Reverse<T>(this Span<T> span)
    {
        int left = 0;
        int right = span.Length - 1;
        while (left < right)
        {
            (span[left], span[right]) = (span[right], span[left]);
            left++; right--;
        }
    }

    /// <summary>
    /// Checks if a span contains only unique elements.
    /// </summary>
    public static bool IsUnique<T>(this Span<T> span) where T : IEquatable<T>
    {
        for (int i = 0; i < span.Length - 1; i++)
        {
            for (int j = i + 1; j < span.Length; j++)
            {
                if (span[i].Equals(span[j]))
                    return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Checks if a span contains any of the elements from another span.
    /// </summary>
    public static bool ContainsAny<T>(this Span<T> span, Span<T> otherSpan) where T : IEquatable<T>
    {
        foreach (var element in otherSpan)
        {
            if (span.Contains(element))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Finds the index of the first occurrence of any element from another span.
    /// </summary>
    public static int IndexOfAny<T>(this Span<T> span, Span<T> otherSpan) where T : IEquatable<T>
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (otherSpan.Contains(span[i]))
                return i;
        }
        return -1; // Not found
    }

    /// <summary>
    /// Fills a span with a specific <paramref name="value"/>.
    /// </summary>
    public static void Fill<T>(this Span<T> span, T value)
    {
        for (int i = 0; i < span.Length; i++)
            span[i] = value;
    }

    /// <summary>
    /// Bisects the <paramref name="span"/> at the given <paramref name="splitIndex"/>.
    /// </summary>
    /// <remarks>
    /// A tuple return is not possible in this scenario due to the type 'Span<T>' 
    /// may not be a ref struct or a type parameter allowing ref structs in order 
    /// to use it as parameter 'T1' in the generic type or method '(T1, T2)'
    /// </remarks>
    public static void Split<T>(this Span<T> span, int splitIndex, out Span<T> left, out Span<T> right)
    {
        if (splitIndex < 0 || splitIndex >= span.Length)
            throw new ArgumentOutOfRangeException(nameof(splitIndex));

        left = span[0..splitIndex];
        right = span[splitIndex..];
    }

    /// <summary>
    /// Calculates the sum of all elements in a span of numeric types.
    /// </summary>
    public static T Sum<T>(this Span<T> span) where T : struct, IAdditionOperators<T, T, T>
    {
        T sum = default;
        
        for (int i = 0; i < span.Length; i++)
            sum += span[i];

        return sum;
    }

    /// <summary>
    /// Calculates the average of all elements in a span of numeric types.
    /// </summary>
    public static double Average<T>(this Span<T> span) where T : struct, IAdditionOperators<T, T, T>, IConvertible
    {
        if (span.Length == 0)
            return 0;

        T sum = default;

        for (int i = 0; i < span.Length; i++)
            sum += span[i];

        return (double)Convert.ChangeType(sum, typeof(double)) / span.Length;
    }

    /// <summary>
    /// Calculates the median of a span of numeric values.
    /// </summary>
    public static double Median<T>(this Span<T> span) where T : struct, IComparable<T>
    {
        if (span.Length == 0)
            throw new ArgumentException("Span cannot be empty.");

        // Create a copy of the span in an array.
        T[] array = new T[span.Length];
        span.CopyTo(array);

        // Sort the array in ascending order.
        Array.Sort(array);

        int middleIndex = array.Length / 2;

        if (array.Length % 2 == 0) // If the length is even, the median is the average of the two middle elements.
            return (Convert.ToDouble(array[middleIndex - 1]) + Convert.ToDouble(array[middleIndex])) / 2;
        else // If the length is odd, the median is the middle element.
            return Convert.ToDouble(array[middleIndex]);
    }
    #endregion

    #region [Task Helpers]
    /// <summary>
    /// Chainable task helper.
    /// var result = Task.Run(() => SomeAsyncMethodWithReturnValue()).AsCancellable(Cancellation.Token).Result;
    /// </summary>
    public static async Task<T> AsCancellable<T>(this Task<T> Instance, CancellationToken token)
    {
        if (!token.CanBeCanceled)
            return await Instance;

        TaskCompletionSource<T> TCS = new TaskCompletionSource<T>();

        using (CancellationTokenRegistration CancelRegistration = token.Register(() => TCS.TrySetCanceled(token), false))
        {
            _ = Instance.ContinueWith((PreviousTask) =>
            {
                CancelRegistration.Dispose();

                if (Instance.IsCanceled)
                    TCS.TrySetCanceled();
                else if (Instance.IsFaulted)
                    TCS.TrySetException(PreviousTask.Exception ?? new AggregateException("aggregate exception was empty"));
                else
                    TCS.TrySetResult(PreviousTask.Result);

            }, TaskContinuationOptions.ExecuteSynchronously);

            return await TCS.Task;
        }
    }

    /// <summary>
    /// Chainable task helper.
    /// Task.Run(() => SomeAsyncMethodWithNoReturnValue()).AsCancellable(Cancellation.Token);
    /// </summary>
    public static Task AsCancellable(this Task Instance, CancellationToken token)
    {
        TaskCompletionSource TCS = new TaskCompletionSource();

        if (!token.CanBeCanceled)
            return TCS.Task;

        using (CancellationTokenRegistration CancelRegistration = token.Register(() => TCS.TrySetCanceled(token), false))
        {
            _ = Instance.ContinueWith((PreviousTask) =>
            {
                CancelRegistration.Dispose();

                if (Instance.IsCanceled)
                    TCS.TrySetCanceled();
                else if (Instance.IsFaulted)
                    TCS.TrySetException(PreviousTask.Exception ?? new AggregateException("aggregate exception was empty"));
                else
                    TCS.TrySetResult();

            }, TaskContinuationOptions.ExecuteSynchronously);

            return TCS.Task;
        }
    }

    public static async Task WithTimeoutAsync(this Task task, TimeSpan timeout)
    {
        if (task == await Task.WhenAny(task, Task.Delay(timeout))) { await task; }
    }

    public static async Task<T?> WithTimeoutAsync<T>(this Task<T> task, TimeSpan timeout, T? defaultValue = default)
    {
        if (task == await Task.WhenAny(task, Task.Delay(timeout)))
            return await task;

        return defaultValue;
    }

    public static async Task<TOut> AndThen<TIn, TOut>(
        this Task<TIn> inputTask, 
        Func<TIn, Task<TOut>> mapping)
    {
         var input = await inputTask;
         return (await mapping(input));
    }

    public static async Task<TOut?> AndThen<TIn, TOut>(
        this Task<TIn> inputTask, 
        Func<TIn, Task<TOut>> mapping, 
        Func<Exception, TOut>? errorHandler = null)
    {
        try
        {
            var input = await inputTask;
            return (await mapping(input));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] AndThen: {ex.Message}");
            if (errorHandler != null)
                return errorHandler(ex);
            
            throw; // Re-throw if no handler is provided.
        }
    }

    /// <summary>
    /// Runs the specified asynchronous method with return type.
    /// NOTE: Will not catch exceptions generated by the task.
    /// </summary>
    /// <param name="asyncMethod">The asynchronous method to execute.</param>
    public static T RunSynchronously<T>(this Func<Task<T>> asyncMethod)
    {
        if (asyncMethod == null)
            throw new ArgumentNullException($"{nameof(asyncMethod)} cannot be null");

        var prevCtx = SynchronizationContext.Current;
        try
        {   // Invoke the function and alert the context when it completes.
            var t = asyncMethod();
            if (t == null)
                throw new InvalidOperationException("No task provided.");

            return t.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(prevCtx); }
    }

    /// <summary>
    /// Runs the specified asynchronous method without return type.
    /// NOTE: Will not catch exceptions generated by the task.
    /// </summary>
    /// <param name="asyncMethod">The asynchronous method to execute.</param>
    public static void RunSynchronously(this Func<Task> asyncMethod)
    {
        if (asyncMethod == null)
            throw new ArgumentNullException($"{nameof(asyncMethod)}");

        var prevCtx = SynchronizationContext.Current;
        try
        {   // Invoke the function and alert the context when it completes
            var t = asyncMethod();
            if (t == null)
                throw new InvalidOperationException("No task provided.");

            t.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(prevCtx); }
    }

    /// <summary>
    /// Chainable task helper.
    /// var result = await SomeLongAsyncFunction().WithTimeout(TimeSpan.FromSeconds(2));
    /// </summary>
    /// <typeparam name="TResult">the type of task result</typeparam>
    /// <returns><see cref="Task"/>TResult</returns>
    public async static Task<TResult> WithTimeout<TResult>(this Task<TResult> task, TimeSpan timeout)
    {
        Task winner = await (Task.WhenAny(task, Task.Delay(timeout)));

        if (winner != task)
            throw new TimeoutException();

        return await task;   // Unwrap result/re-throw
    }

    /// <summary>
    /// Task extension to add a timeout.
    /// </summary>
    /// <returns>The task with timeout.</returns>
    /// <param name="task">Task.</param>
    /// <param name="timeoutInMilliseconds">Timeout duration in Milliseconds.</param>
    /// <typeparam name="T">The 1st type parameter.</typeparam>
    public async static Task<T> WithTimeout<T>(this Task<T> task, int timeoutInMilliseconds)
    {
        var retTask = await Task.WhenAny(task, Task.Delay(timeoutInMilliseconds))
            .ConfigureAwait(false);

#pragma warning disable CS8603 // Possible null reference return.
        return retTask is Task<T> ? task.Result : default;
#pragma warning restore CS8603 // Possible null reference return.
    }

    /// <summary>
    /// Chainable task helper.
    /// var result = await SomeLongAsyncFunction().WithCancellation(cts.Token);
    /// </summary>
    /// <typeparam name="TResult">the type of task result</typeparam>
    /// <returns><see cref="Task"/>TResult</returns>
    public static Task<TResult> WithCancellation<TResult>(this Task<TResult> task, CancellationToken cancelToken)
    {
        var tcs = new TaskCompletionSource<TResult>();
        var reg = cancelToken.Register(() => tcs.TrySetCanceled());
        task.ContinueWith(ant =>
        {
            reg.Dispose();
            if (ant.IsCanceled)
                tcs.TrySetCanceled();
            else if (ant.IsFaulted)
                tcs.TrySetException(ant.Exception?.InnerException ?? new Exception("Antecedent faulted."));
            else
                tcs.TrySetResult(ant.Result);
        });
        return tcs.Task;  // Return the TaskCompletionSource result
    }

    public static Task<T> WithAllExceptions<T>(this Task<T> task)
    {
        TaskCompletionSource<T> tcs = new TaskCompletionSource<T>();

        task.ContinueWith(ignored =>
        {
            switch (task.Status)
            {
                case TaskStatus.Canceled:
                    Debug.WriteLine($"[TaskStatus.Canceled]");
                    tcs.SetCanceled();
                    break;
                case TaskStatus.RanToCompletion:
                    tcs.SetResult(task.Result);
                    //Debug.WriteLine($"[TaskStatus.RanToCompletion({task.Result})]");
                    break;
                case TaskStatus.Faulted:
                    // SetException will automatically wrap the original AggregateException
                    // in another one. The new wrapper will be removed in TaskAwaiter, leaving
                    // the original intact.
                    Debug.WriteLine($"[TaskStatus.Faulted: {task.Exception?.Message}]");
                    tcs.SetException(task.Exception ?? new Exception("Task faulted."));
                    break;
                default:
                    Debug.WriteLine($"[TaskStatus: Continuation called illegally.]");
                    tcs.SetException(new InvalidOperationException("Continuation called illegally."));
                    break;
            }
        });

        return tcs.Task;
    }

#pragma warning disable RECS0165 // Asynchronous methods should return a Task instead of void
    /// <summary>
    /// Attempts to await on the task and catches exception
    /// </summary>
    /// <param name="task">Task to execute</param>
    /// <param name="onException">What to do when method has an exception</param>
    /// <param name="continueOnCapturedContext">If the context should be captured.</param>
    public static async void SafeFireAndForget(this Task task, Action<Exception>? onException = null, bool continueOnCapturedContext = false)
#pragma warning restore RECS0165 // Asynchronous methods should return a Task instead of void
    {
        try
        {
            await task.ConfigureAwait(continueOnCapturedContext);
        }
        catch (Exception ex) when (onException != null)
        {
            onException.Invoke(ex);
        }
        catch (Exception ex) when (onException == null)
        {
            Debug.WriteLine($"SafeFireAndForget: {ex.Message}");
        }
    }

    /// <summary>
    /// Task.Factory.StartNew (() => { throw null; }).IgnoreExceptions();
    /// </summary>
    public static void IgnoreExceptions(this Task task)
    {
        task.ContinueWith(t =>
        {
            var ignore = t.Exception;
            var inners = ignore?.Flatten()?.InnerExceptions;
            if (inners != null)
            {
                foreach (Exception ex in inners)
                    Debug.WriteLine($"[{ex.GetType()}]: {ex.Message}");
            }
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    public static bool IgnoreExceptions(Action action, Type? exceptionToIgnore = null, [CallerMemberName] string? caller = null)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            if (exceptionToIgnore is null || exceptionToIgnore.IsAssignableFrom(ex.GetType()))
            {
                App.DebugLog($"{caller ?? "N/A"}: {ex.Message}");
                return false;
            }
            else
                throw;
        }
    }


    /// <summary>
    /// Gets the result of a <see cref="Task"/> if available, or <see langword="null"/> otherwise.
    /// </summary>
    /// <param name="task">The input <see cref="Task"/> instance to get the result for.</param>
    /// <returns>The result of <paramref name="task"/> if completed successfully, or <see langword="default"/> otherwise.</returns>
    /// <remarks>
    /// This method does not block if <paramref name="task"/> has not completed yet. Furthermore, it is not generic
    /// and uses reflection to access the <see cref="Task{TResult}.Result"/> property and boxes the result if it's
    /// a value type, which adds overhead. It should only be used when using generics is not possible.
    /// </remarks>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static object? GetResultOrDefault(this Task task)
    {
        // Check if the instance is a completed Task
        if (
#if NETSTANDARD2_1
            task.IsCompletedSuccessfully
#else
            task.Status == TaskStatus.RanToCompletion
#endif
        )
        {
            // We need an explicit check to ensure the input task is not the cached
            // Task.CompletedTask instance, because that can internally be stored as
            // a Task<T> for some given T (e.g. on dotNET 5 it's VoidTaskResult), which
            // would cause the following code to return that result instead of null.
            if (task != Task.CompletedTask)
            {
                // Try to get the Task<T>.Result property. This method would've
                // been called anyway after the type checks, but using that to
                // validate the input type saves some additional reflection calls.
                // Furthermore, doing this also makes the method flexible enough to
                // cases whether the input Task<T> is actually an instance of some
                // runtime-specific type that inherits from Task<T>.
                PropertyInfo? propertyInfo =
#if NETSTANDARD1_4
                    task.GetType().GetRuntimeProperty(nameof(Task<object>.Result));
#else
                    task.GetType().GetProperty(nameof(Task<object>.Result));
#endif

                // Return the result, if possible
                return propertyInfo?.GetValue(task);
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the result of a <see cref="Task{TResult}"/> if available, or <see langword="default"/> otherwise.
    /// </summary>
    /// <typeparam name="T">The type of <see cref="Task{TResult}"/> to get the result for.</typeparam>
    /// <param name="task">The input <see cref="Task{TResult}"/> instance to get the result for.</param>
    /// <returns>The result of <paramref name="task"/> if completed successfully, or <see langword="default"/> otherwise.</returns>
    /// <remarks>This method does not block if <paramref name="task"/> has not completed yet.</remarks>
    [Pure]
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static T? GetResultOrDefault<T>(this Task<T?> task)
    {
#if NETSTANDARD2_1
        return task.IsCompletedSuccessfully ? task.Result : default;
#else
        return task.Status == TaskStatus.RanToCompletion ? task.Result : default;
#endif
    }
    #endregion

    #region [TaskCompletionSource via Thread encapsulation]
    public static Task StartSTATask(Func<Task> func)
    {
        var tcs = new TaskCompletionSource();
        Thread t = new Thread(async () => {
            try
            {
                await func();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                //tcs.SetException(ex);
                tcs.TrySetResult();
                App.DebugLog($"StartSTATask(Func<Task>): {ex.Message}");
            }
        }){ IsBackground = true, Priority = ThreadPriority.Lowest };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    public static Task StartSTATask(Action action)
    {
        var tcs = new TaskCompletionSource();
        Thread t = new Thread(() => {
            try
            {
                action();
                tcs.TrySetResult();
            }
            catch (Exception ex)
            {
                //tcs.SetException(ex);
                tcs.TrySetResult();
                App.DebugLog($"StartSTATask(Action): {ex.Message}");
            }
        }){ IsBackground = true, Priority = ThreadPriority.Lowest };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    public static Task<T?> StartSTATask<T>(Func<T> func)
    {
        var tcs = new TaskCompletionSource<T?>();
        Thread t = new Thread(() => {
            try
            {
                tcs.TrySetResult(func());
            }
            catch (Exception ex)
            {
                //tcs.SetException(ex);
                tcs.TrySetResult(default);
                App.DebugLog($"StartSTATask(Func<T>): {ex.Message}");
            }
        }){ IsBackground = true, Priority = ThreadPriority.Lowest };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    public static Task<T?> StartSTATask<T>(Func<Task<T>> func)
    {
        var tcs = new TaskCompletionSource<T?>();
        Thread t = new Thread(async () => {
            try
            {
                tcs.TrySetResult(await func());
            }
            catch (Exception ex)
            {
                //tcs.SetException(ex);
                tcs.SetResult(default);
                App.DebugLog($"StartSTATask(Func<Task<T>>): {ex.Message}");
            }
        }){ IsBackground = true, Priority = ThreadPriority.Lowest };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }
    #endregion

    #region [WinUI Specific]
    /// <summary>
    /// Can be useful if you only have a root (not merged) resource dictionary.
    /// var rdBrush = Extensions.GetResource{SolidColorBrush}("PrimaryBrush");
    /// </summary>
    /// <remarks>Only call under UI sync context (on the UI thread)</remarks>
    public static T? GetResource<T>(string resourceName) where T : class
    {
        try
        {
            if (Application.Current.Resources.TryGetValue($"{resourceName}", out object value))
                return (T)value;
            else
                return default(T);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetResource: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Can be useful if you only have a root (not merged) resource dictionary.
    /// var rdBrush = Extensions.GetResource{SolidColorBrush}("PrimaryBrush");
    /// </summary>
    /// <remarks>Only call under UI sync context (on the UI thread)</remarks>
    public static Task<T?> GetResourceAsync<T>(string resourceName) where T : class
    {
        var tcs = new TaskCompletionSource<T?>();
        try
        {
            if (App.Current.Resources.TryGetValue($"{resourceName}", out object value))
                tcs.TrySetResult((T)value);
            else
                tcs.TrySetResult(default(T));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetResourceAsync: {ex.Message}");
            //tcs.SetException(ex);
            tcs.SetResult(default(T));
        }
        return tcs.Task;
    }

    /// <summary>
    /// Can be useful if you have merged theme resource dictionaries.
    /// var darkBrush = Extensions.GetThemeResource{SolidColorBrush}("PrimaryBrush", ElementTheme.Dark);
    /// var lightBrush = Extensions.GetThemeResource{SolidColorBrush}("PrimaryBrush", ElementTheme.Light);
    /// </summary>
    public static T? GetThemeResource<T>(string resourceName, ElementTheme? theme) where T : class
    {
        try
        {
            theme ??= ElementTheme.Default;

            var dictionaries = Application.Current.Resources.MergedDictionaries;
            foreach (var item in dictionaries)
            {
                // A typical IList<ResourceDictionary> will contain:
                //   - 'Default'
                //   - 'Light'
                //   - 'Dark'
                //   - 'HighContrast'
                foreach (var kv in item.ThemeDictionaries.Keys)
                {
                    // Examine the ICollection<T> for the key names.
                    Debug.WriteLine($"ThemeDictionary is named '{kv}'");
                }

                // Do we have any themes in this resource dictionary?
                if (item.ThemeDictionaries.Count > 0)
                {
                    if (theme == ElementTheme.Dark)
                    {
                        if (item.ThemeDictionaries.TryGetValue("Dark", out var drd))
                        {
                            ResourceDictionary? dark = drd as ResourceDictionary;
                            if (dark != null)
                            {
                                Debug.WriteLine($"Found dark theme resource dictionary");
                                if (dark.TryGetValue($"{resourceName}", out var tmp))
                                    return (T)tmp;
                                else
                                    Debug.WriteLine($"Could not find '{resourceName}'");
                            }
                        }
                        else { Debug.WriteLine($"{nameof(ElementTheme.Dark)} theme was not found"); }
                    }
                    else if (theme == ElementTheme.Light)
                    {
                        if (item.ThemeDictionaries.TryGetValue("Light", out var lrd))
                        {
                            ResourceDictionary? light = lrd as ResourceDictionary;
                            if (light != null)
                            {
                                Debug.WriteLine($"Found light theme resource dictionary");
                                if (light.TryGetValue($"{resourceName}", out var tmp))
                                    return (T)tmp;
                                else
                                    Debug.WriteLine($"Could not find '{resourceName}'");
                            }
                        }
                        else { Debug.WriteLine($"{nameof(ElementTheme.Light)} theme was not found"); }
                    }
                    else if (theme == ElementTheme.Default)
                    {
                        if (item.ThemeDictionaries.TryGetValue("Default", out var drd))
                        {
                            ResourceDictionary? dflt = drd as ResourceDictionary;
                            if (dflt != null)
                            {
                                Debug.WriteLine($"Found default theme resource dictionary");
                                if (dflt.TryGetValue($"{resourceName}", out var tmp))
                                    return (T)tmp;
                                else
                                    Debug.WriteLine($"Could not find '{resourceName}'");
                            }
                        }
                        else { Debug.WriteLine($"{nameof(ElementTheme.Default)} theme was not found"); }
                    }
                    else
                        Debug.WriteLine($"No theme to match");
                }
                else
                    Debug.WriteLine($"No theme dictionaries found");
            }

            return default(T);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"GetThemeResource: {ex.Message}");
            return null;
        }
    }

    /// <summary><code>
    ///   Extensions.AddKeyboardAccelerator(SomePage, Windows.System.VirtualKeyModifiers.None, Windows.System.VirtualKey.Up, static (_, kaea) => 
    ///   {
    ///      if (kaea.Element is Page ctrl) 
    ///      {
    ///         ctrl.Background = CreateLinearGradientBrush(Colors.Transparent, Colors.DodgerBlue, Colors.MidnightBlue);
    ///         kaea.Handled = true;
    ///      }
    ///   });
    /// </code></summary>
    public static void AddKeyboardAccelerator(UIElement element, Windows.System.VirtualKeyModifiers keyModifiers, Windows.System.VirtualKey key, Windows.Foundation.TypedEventHandler<KeyboardAccelerator, KeyboardAcceleratorInvokedEventArgs> handler)
    {
        var accelerator = new KeyboardAccelerator()
        {
            Modifiers = keyModifiers,
            Key = key
        };
        accelerator.Invoked += handler;
        element.KeyboardAccelerators.Add(accelerator);
    }

    /// <summary>
    /// Renders a snapshot of the <see cref="Microsoft.UI.Xaml.UIElement"/> 
    /// visual tree to an <see cref="Microsoft.UI.Xaml.Media.ImageSource"/>.
    /// </summary>
    /// <param name="canvas"><see cref="Microsoft.UI.Xaml.Controls.Canvas"/></param>
    /// <returns><see cref="Microsoft.UI.Xaml.Media.ImageBrush"/></returns>
    public static async Task<Microsoft.UI.Xaml.Media.ImageBrush> CreateBrushFromCanvas(this Microsoft.UI.Xaml.Controls.Canvas canvas)
    {
        var renderTargetBitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await renderTargetBitmap.RenderAsync(canvas);
        var brush = new Microsoft.UI.Xaml.Media.ImageBrush { ImageSource = renderTargetBitmap };
        return brush;
    }

    /// <summary>
    /// Returns the selected item's content from a <see cref="ComboBox"/>.
    /// </summary>
    public static string GetSelectedText(this ComboBox comboBox)
    {
        var item = comboBox.SelectedItem as ComboBoxItem;
        if (item != null)
        {
            return (string)item.Content;
        }

        return "";
    }

    public static void SetOrientation(this VirtualizingLayout layout, Orientation orientation)
    {
        // Note:
        // The public properties of UniformGridLayout and FlowLayout interpret
        // orientation the opposite to how FlowLayoutAlgorithm interprets it. 
        // For simplicity, our validation code is written in terms that match
        // the implementation. For this reason, we need to switch the orientation
        // whenever we set UniformGridLayout.Orientation or StackLayout.Orientation.
        if (layout is StackLayout)
        {
            ((StackLayout)layout).Orientation = orientation;
        }
        else if (layout is UniformGridLayout)
        {
            ((UniformGridLayout)layout).Orientation = orientation;
        }
        else
        {
            throw new InvalidOperationException("layout unknown");
        }
    }

    public static void BindCenterPoint(this Microsoft.UI.Composition.Visual target)
    {
        var exp = target.Compositor.CreateExpressionAnimation("Vector3(this.Target.Size.X / 2, this.Target.Size.Y / 2, 0f)");
        target.StartAnimation("CenterPoint", exp);
    }

    public static void BindSize(this Microsoft.UI.Composition.Visual target, Microsoft.UI.Composition.Visual source)
    {
        var exp = target.Compositor.CreateExpressionAnimation("host.Size");
        exp.SetReferenceParameter("host", source);
        target.StartAnimation("Size", exp);
    }

    public static Microsoft.UI.Composition.ImplicitAnimationCollection CreateImplicitAnimation(this Microsoft.UI.Composition.ImplicitAnimationCollection source, string Target, TimeSpan? Duration = null)
    {
        Microsoft.UI.Composition.KeyFrameAnimation animation = null;
        switch (Target.ToLower())
        {
            case "offset":
            case "scale":
            case "centerPoint":
            case "rotationAxis":
                animation = source.Compositor.CreateVector3KeyFrameAnimation();
                break;

            case "size":
                animation = source.Compositor.CreateVector2KeyFrameAnimation();
                break;

            case "opacity":
            case "blueRadius":
            case "rotationAngle":
            case "rotationAngleInDegrees":
                animation = source.Compositor.CreateScalarKeyFrameAnimation();
                break;

            case "color":
                animation = source.Compositor.CreateColorKeyFrameAnimation();
                break;
        }

        if (animation == null) throw new ArgumentNullException("Unknown Target");
        if (!Duration.HasValue) Duration = TimeSpan.FromSeconds(0.2d);
        animation.InsertExpressionKeyFrame(1f, "this.FinalValue");
        animation.Duration = Duration.Value;
        animation.Target = Target;

        source[Target] = animation;
        return source;
    }

    /// <summary>
    /// Finds the contrast ratio.
    /// This is helpful for determining if one control's foreground and another control's background will be hard to distinguish.
    /// https://www.w3.org/WAI/GL/wiki/Contrast_ratio
    /// (L1 + 0.05) / (L2 + 0.05), where
    /// L1 is the relative luminance of the lighter of the colors, and
    /// L2 is the relative luminance of the darker of the colors.
    /// </summary>
    /// <param name="first"><see cref="Windows.UI.Color"/></param>
    /// <param name="second"><see cref="Windows.UI.Color"/></param>
    /// <returns>ratio between relative luminance</returns>
    public static double CalculateContrastRatio(Windows.UI.Color first, Windows.UI.Color second)
    {
        double relLuminanceOne = GetRelativeLuminance(first);
        double relLuminanceTwo = GetRelativeLuminance(second);
        return (Math.Max(relLuminanceOne, relLuminanceTwo) + 0.05) / (Math.Min(relLuminanceOne, relLuminanceTwo) + 0.05);
    }

    /// <summary>
    /// Gets the relative luminance.
    /// https://www.w3.org/WAI/GL/wiki/Relative_luminance
    /// For the sRGB colorspace, the relative luminance of a color is defined as L = 0.2126 * R + 0.7152 * G + 0.0722 * B
    /// </summary>
    /// <param name="c"><see cref="Windows.UI.Color"/></param>
    /// <remarks>This is mainly used by <see cref="Helpers.CalculateContrastRatio(Color, Color)"/></remarks>
    public static double GetRelativeLuminance(Windows.UI.Color c)
    {
        double rSRGB = c.R / 255.0;
        double gSRGB = c.G / 255.0;
        double bSRGB = c.B / 255.0;

        // WebContentAccessibilityGuideline 2.x definition was 0.03928 (incorrect)
        // WebContentAccessibilityGuideline 3.x definition is 0.04045 (correct)
        double r = rSRGB <= 0.04045 ? rSRGB / 12.92 : Math.Pow(((rSRGB + 0.055) / 1.055), 2.4);
        double g = gSRGB <= 0.04045 ? gSRGB / 12.92 : Math.Pow(((gSRGB + 0.055) / 1.055), 2.4);
        double b = bSRGB <= 0.04045 ? bSRGB / 12.92 : Math.Pow(((bSRGB + 0.055) / 1.055), 2.4);
        return 0.2126 * r + 0.7152 * g + 0.0722 * b;
    }

    /// <summary>
    /// Calculates the linear interpolated Color based on the given Color values.
    /// </summary>
    /// <param name="colorFrom">Source Color.</param>
    /// <param name="colorTo">Target Color.</param>
    /// <param name="amount">Weight given to the target color.</param>
    /// <returns>Linear Interpolated Color.</returns>
    public static Windows.UI.Color Lerp(this Windows.UI.Color colorFrom, Windows.UI.Color colorTo, float amount)
    {
        // Convert colorFrom components to lerp-able floats
        float sa = colorFrom.A, sr = colorFrom.R, sg = colorFrom.G, sb = colorFrom.B;

        // Convert colorTo components to lerp-able floats
        float ea = colorTo.A, er = colorTo.R, eg = colorTo.G, eb = colorTo.B;

        // lerp the colors to get the difference
        byte a = (byte)Math.Max(0, Math.Min(255, sa.Lerp(ea, amount))),
             r = (byte)Math.Max(0, Math.Min(255, sr.Lerp(er, amount))),
             g = (byte)Math.Max(0, Math.Min(255, sg.Lerp(eg, amount))),
             b = (byte)Math.Max(0, Math.Min(255, sb.Lerp(eb, amount)));

        // return the new color
        return Windows.UI.Color.FromArgb(a, r, g, b);
    }

    /// <summary>
    /// Darkens the color by the given percentage using lerp.
    /// </summary>
    /// <param name="color">Source color.</param>
    /// <param name="amount">Percentage to darken. Value should be between 0 and 1.</param>
    /// <returns>Color</returns>
    public static Windows.UI.Color DarkerBy(this Windows.UI.Color color, float amount)
    {
        return color.Lerp(Colors.Black, amount);
    }

    /// <summary>
    /// Lightens the color by the given percentage using lerp.
    /// </summary>
    /// <param name="color">Source color.</param>
    /// <param name="amount">Percentage to lighten. Value should be between 0 and 1.</param>
    /// <returns>Color</returns>
    public static Windows.UI.Color LighterBy(this Windows.UI.Color color, float amount)
    {
        return color.Lerp(Colors.White, amount);
    }

    /// <summary>
    /// Creates a <see cref="LinearGradientBrush"/> from 3 input colors.
    /// </summary>
    /// <param name="c1">offset 0.0 color</param>
    /// <param name="c2">offset 0.5 color</param>
    /// <param name="c3">offset 1.0 color</param>
    /// <returns><see cref="LinearGradientBrush"/></returns>
    public static LinearGradientBrush CreateLinearGradientBrush(Windows.UI.Color c1, Windows.UI.Color c2, Windows.UI.Color c3)
    {
        var gs1 = new GradientStop(); gs1.Color = c1; gs1.Offset = 0.0;
        var gs2 = new GradientStop(); gs2.Color = c2; gs2.Offset = 0.5;
        var gs3 = new GradientStop(); gs3.Color = c3; gs3.Offset = 1.0;
        var gsc = new GradientStopCollection();
        gsc.Add(gs1); gsc.Add(gs2); gsc.Add(gs3);
        var lgb = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops = gsc
        };
        return lgb;
    }

    public static LinearGradientBrush CreateDiagonalGradientBrush(Windows.UI.Color c1, Windows.UI.Color c2, Windows.UI.Color c3)
    {
        var gs1 = new GradientStop(); gs1.Color = c1; gs1.Offset = 0.0;
        var gs2 = new GradientStop(); gs2.Color = c2; gs2.Offset = 0.3;
        var gs3 = new GradientStop(); gs3.Color = c3; gs3.Offset = 1.0;
        var gsc = new GradientStopCollection();
        gsc.Add(gs1); gsc.Add(gs2); gsc.Add(gs3);
        var lgb = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.25, 0.25),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops = gsc
        };
        return lgb;
    }

    public static LinearGradientBrush CreateDiagonalGradientBrush(Windows.UI.Color c1, Windows.UI.Color c2, Windows.UI.Color c3, Windows.UI.Color c4)
    {
        var gs1 = new GradientStop(); gs1.Color = c1; gs1.Offset = 0.0;
        var gs2 = new GradientStop(); gs2.Color = c2; gs2.Offset = 0.2;
        var gs3 = new GradientStop(); gs3.Color = c3; gs3.Offset = 0.4;
        var gs4 = new GradientStop(); gs4.Color = c4; gs4.Offset = 1.0;
        var gsc = new GradientStopCollection();
        gsc.Add(gs1); gsc.Add(gs2); gsc.Add(gs3); gsc.Add(gs4);
        var lgb = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0.25, 0.25),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops = gsc
        };
        return lgb;
    }

    /// <summary>
    /// Creates a Color object from the hex color code and returns the result.
    /// </summary>
    /// <param name="hexColorCode">text representation of the color</param>
    /// <returns><see cref="Windows.UI.Color"/></returns>
    public static Windows.UI.Color? GetColorFromHexString(string hexColorCode)
    {
        if (string.IsNullOrEmpty(hexColorCode))
            return null;

        try
        {
            byte a = 255; byte r = 0; byte g = 0; byte b = 0;

            if (hexColorCode.Length == 9)
            {
                hexColorCode = hexColorCode.Substring(1, 8);
            }
            if (hexColorCode.Length == 8)
            {
                a = Convert.ToByte(hexColorCode.Substring(0, 2), 16);
                hexColorCode = hexColorCode.Substring(2, 6);
            }
            if (hexColorCode.Length == 6)
            {
                r = Convert.ToByte(hexColorCode.Substring(0, 2), 16);
                g = Convert.ToByte(hexColorCode.Substring(2, 2), 16);
                b = Convert.ToByte(hexColorCode.Substring(4, 2), 16);
            }

            return Windows.UI.Color.FromArgb(a, r, g, b);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Uses the <see cref="System.Reflection.PropertyInfo"/> of the 
    /// <see cref="Microsoft.UI.Colors"/> class to return the matching 
    /// <see cref="Windows.UI.Color"/> object.
    /// </summary>
    /// <param name="colorName">name of color, e.g. "Aquamarine"</param>
    /// <returns><see cref="Windows.UI.Color"/></returns>
    public static Windows.UI.Color? GetColorFromNameString(string colorName)
    {
        if (string.IsNullOrEmpty(colorName))
            return Windows.UI.Color.FromArgb(255, 128, 128, 128);

        try
        {
            var prop = typeof(Microsoft.UI.Colors).GetTypeInfo().GetDeclaredProperty(colorName);
            if (prop != null)
            {
                var tmp = prop.GetValue(null);
                if (tmp != null)
                    return (Windows.UI.Color)tmp;
            }
            else
            {
                Debug.WriteLine($"[WARNING] \"{colorName}\" could not be resolved as a {nameof(Windows.UI.Color)}.");
            }

            return Windows.UI.Color.FromArgb(255, 128, 128, 128);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] GetColorFromNameString: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Generates a completely random <see cref="Windows.UI.Color"/>.
    /// </summary>
    /// <returns><see cref="Windows.UI.Color"/></returns>
    public static Windows.UI.Color GetRandomWinUIColor(byte alpha = 255)
    {
        byte[] buffer = new byte[3];
        Random.Shared.NextBytes(buffer);
        return Windows.UI.Color.FromArgb(alpha, buffer[0], buffer[1], buffer[2]);
    }

    public static Windows.UI.Color[] GetAllColors()
    {
        return typeof(Colors)
            .GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(p => p.PropertyType == typeof(Windows.UI.Color))
            .Select(p => (Windows.UI.Color)p.GetValue(null)!)
            .ToArray();
    }

    public static Windows.UI.Color[] CreateBlueGreenColorScale(int start, int end)
    {
        var colors = new Windows.UI.Color[end - start + 1];
        for (int i = 0; i < colors.Length; i++)
        {
            float factor = ((float)i / (end - start)) * 255; // map the position to 0-255
            // Using red and green channels only.
            colors[i] = Windows.UI.Color.FromArgb(255, 0, (byte)(255 - 10 * factor), (byte)(200 * factor)); // create a color gradient from light to dark
        }
        return colors;
    }

    public static Windows.UI.Color[] CreateColorScale(int start, int end, byte alpha = 255)
    {
        var colors = new Windows.UI.Color[end - start + 1];
        for (int i = 0; i < colors.Length; i++)
        {
            float factor = (float)i / (end - start); // Normalize factor to 0-1
            byte red = (byte)(200 * factor);         // Red increases
            byte green = (byte)(255 - 10 * factor);  // Green decreases
            byte blue = (byte)(155 - 10 * factor);   // Blue varies smoothly

            colors[i] = Windows.UI.Color.FromArgb(alpha, red, green, blue);
        }
        return colors;
    }

    /// <summary>
    /// Returns a random selection from <see cref="Microsoft.UI.Colors"/>.
    /// </summary>
    /// <returns><see cref="Windows.UI.Color"/></returns>
    public static Windows.UI.Color GetRandomMicrosoftUIColor()
    {
        try
        {
            var colorType = typeof(Microsoft.UI.Colors);
            var colors = colorType.GetProperties()
                .Where(p => p.PropertyType == typeof(Windows.UI.Color) && p.GetMethod.IsStatic && p.GetMethod.IsPublic)
                .Select(p => (Windows.UI.Color)p.GetValue(null))
                .ToList();

            if (colors.Count > 0)
            {
                var randomIndex = Random.Shared.Next(colors.Count);
                var randomColor = colors[randomIndex];
                return randomColor;
            }
            else
            {
                return Microsoft.UI.Colors.Gray;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] GetRandomColor: {ex.Message}");
            return Microsoft.UI.Colors.Red;
        }
    }


    /// <summary>
    /// Creates a Color from the hex color code and returns the result 
    /// as a <see cref="Microsoft.UI.Xaml.Media.SolidColorBrush"/>.
    /// </summary>
    /// <param name="hexColorCode">text representation of the color</param>
    /// <returns><see cref="Microsoft.UI.Xaml.Media.SolidColorBrush"/></returns>
    public static Microsoft.UI.Xaml.Media.SolidColorBrush? GetBrushFromHexString(string hexColorCode)
    {
        if (string.IsNullOrEmpty(hexColorCode))
            return null;

        try
        {
            byte a = 255; byte r = 0; byte g = 0; byte b = 0;

            if (hexColorCode.Length == 9)
                hexColorCode = hexColorCode.Substring(1, 8);

            if (hexColorCode.Length == 8)
            {
                a = Convert.ToByte(hexColorCode.Substring(0, 2), 16);
                hexColorCode = hexColorCode.Substring(2, 6);
            }

            if (hexColorCode.Length == 6)
            {
                r = Convert.ToByte(hexColorCode.Substring(0, 2), 16);
                g = Convert.ToByte(hexColorCode.Substring(2, 2), 16);
                b = Convert.ToByte(hexColorCode.Substring(4, 2), 16);
            }

            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(a, r, g, b));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] GetBrushFromHexString: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Verifies if the given brush is a SolidColorBrush and its color does not include transparency.
    /// </summary>
    /// <param name="brush">Brush</param>
    /// <returns>true if yes, otherwise false</returns>
    public static bool IsOpaqueSolidColorBrush(this Microsoft.UI.Xaml.Media.Brush brush)
    {
        return (brush as Microsoft.UI.Xaml.Media.SolidColorBrush)?.Color.A == 0xff;
    }

    /// <summary>
    /// Generates a 7 digit color string including the # sign.
    /// If the <see cref="ElementTheme"/> is dark then 0, 1 & 2 options are 
    /// removed so dark colors such as 000000/111111/222222 are not possible.
    /// If the <see cref="ElementTheme"/> is light then D, E & F options are 
    /// removed so light colors such as DDDDDD/EEEEEE/FFFFFF are not possible.
    /// </summary>
    public static string GetRandomColorString(ElementTheme? theme)
    {
        StringBuilder sb = new StringBuilder();
        string chTable = "012346789ABCDEF";

        if (theme.HasValue && theme == ElementTheme.Dark)
            chTable = "346789ABCDEF";
        else if (theme.HasValue && theme == ElementTheme.Light)
            chTable = "012346789ABC";

        //char[] charArray = chTable.Distinct().ToArray();

        for (int x = 0; x < 6; x++)
            sb.Append(chTable[Random.Shared.Next() % chTable.Length]);

        return $"#{sb}";
    }

    /// <summary>
    /// Returns the given <see cref="Windows.UI.Color"/> as a hex string.
    /// </summary>
    /// <param name="color">color to convert</param>
    /// <returns>hex string (including pound sign)</returns>
    public static string ToHexString(this Windows.UI.Color color)
    {
        return $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    /// <summary>
    /// Returns a new <see cref="Windows.Foundation.Rect(double, double, double, double)"/> representing the size of the <see cref="Vector2"/>.
    /// </summary>
    /// <param name="vector"><see cref="System.Numerics.Vector2"/> vector representing object size for Rectangle.</param>
    /// <returns><see cref="Windows.Foundation.Rect(double, double, double, double)"/> value.</returns>
    public static Windows.Foundation.Rect ToRect(this System.Numerics.Vector2 vector)
    {
        return new Windows.Foundation.Rect(0, 0, vector.X, vector.Y);
    }

    /// <summary>
    /// Returns a new <see cref="System.Numerics.Vector2"/> representing the <see cref="Windows.Foundation.Size(double, double)"/>.
    /// </summary>
    /// <param name="size"><see cref="Windows.Foundation.Size(double, double)"/> value.</param>
    /// <returns><see cref="System.Numerics.Vector2"/> value.</returns>
    public static System.Numerics.Vector2 ToVector2(this Windows.Foundation.Size size)
    {
        return new System.Numerics.Vector2((float)size.Width, (float)size.Height);
    }

    /// <summary>
    /// Deflates rectangle by given thickness.
    /// </summary>
    /// <param name="rect">Rectangle</param>
    /// <param name="thick">Thickness</param>
    /// <returns>Deflated Rectangle</returns>
    public static Windows.Foundation.Rect Deflate(this Windows.Foundation.Rect rect, Microsoft.UI.Xaml.Thickness thick)
    {
        return new Windows.Foundation.Rect(
            rect.Left + thick.Left,
            rect.Top + thick.Top,
            Math.Max(0.0, rect.Width - thick.Left - thick.Right),
            Math.Max(0.0, rect.Height - thick.Top - thick.Bottom));
    }

    /// <summary>
    /// Inflates rectangle by given thickness.
    /// </summary>
    /// <param name="rect">Rectangle</param>
    /// <param name="thick">Thickness</param>
    /// <returns>Inflated Rectangle</returns>
    public static Windows.Foundation.Rect Inflate(this Windows.Foundation.Rect rect, Microsoft.UI.Xaml.Thickness thick)
    {
        return new Windows.Foundation.Rect(
            rect.Left - thick.Left,
            rect.Top - thick.Top,
            Math.Max(0.0, rect.Width + thick.Left + thick.Right),
            Math.Max(0.0, rect.Height + thick.Top + thick.Bottom));
    }

    /// <summary>
    /// Starts an <see cref="Microsoft.UI.Composition.ExpressionAnimation"/> to keep the size of the source <see cref="Microsoft.UI.Composition.CompositionObject"/> in sync with the target <see cref="UIElement"/>
    /// </summary>
    /// <param name="source">The <see cref="Microsoft.UI.Composition.CompositionObject"/> to start the animation on</param>
    /// <param name="target">The target <see cref="UIElement"/> to read the size updates from</param>
    public static void BindSize(this Microsoft.UI.Composition.CompositionObject source, UIElement target)
    {
        var visual = ElementCompositionPreview.GetElementVisual(target);
        var bindSizeAnimation = source.Compositor.CreateExpressionAnimation($"{nameof(visual)}.Size");
        bindSizeAnimation.SetReferenceParameter(nameof(visual), visual);
        // Start the animation
        source.StartAnimation("Size", bindSizeAnimation);
    }

    /// <summary>
    /// Starts an animation on the given property of a <see cref="Microsoft.UI.Composition.CompositionObject"/>
    /// </summary>
    /// <typeparam name="T">The type of the property to animate</typeparam>
    /// <param name="target">The target <see cref="Microsoft.UI.Composition.CompositionObject"/></param>
    /// <param name="property">The name of the property to animate</param>
    /// <param name="value">The final value of the property</param>
    /// <param name="duration">The animation duration</param>
    /// <returns>A <see cref="Task"/> that completes when the created animation completes</returns>
    public static Task StartAnimationAsync<T>(this Microsoft.UI.Composition.CompositionObject target, string property, T value, TimeSpan duration) where T : unmanaged
    {
        // Stop previous animations
        target.StopAnimation(property);

        // Setup the animation to run
        Microsoft.UI.Composition.KeyFrameAnimation animation;

        // Switch on the value to determine the necessary KeyFrameAnimation type
        switch (value)
        {
            case float f:
                var scalarAnimation = target.Compositor.CreateScalarKeyFrameAnimation();
                scalarAnimation.InsertKeyFrame(1f, f);
                animation = scalarAnimation;
                break;
            case Windows.UI.Color c:
                var colorAnimation = target.Compositor.CreateColorKeyFrameAnimation();
                colorAnimation.InsertKeyFrame(1f, c);
                animation = colorAnimation;
                break;
            case System.Numerics.Vector4 v4:
                var vector4Animation = target.Compositor.CreateVector4KeyFrameAnimation();
                vector4Animation.InsertKeyFrame(1f, v4);
                animation = vector4Animation;
                break;
            default: throw new ArgumentException($"Invalid animation type: {typeof(T)}", nameof(value));
        }

        animation.Duration = duration;

        // Get the batch and start the animations
        var batch = target.Compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);

        // Create a TCS for the result
        var tcs = new TaskCompletionSource<object>();

        batch.Completed += (s, e) => tcs.SetResult(null);

        target.StartAnimation(property, animation);

        batch.End();

        return tcs.Task;
    }

    /// <summary>
    /// Creates a <see cref="Microsoft.UI.Composition.CompositionGeometricClip"/> from the specified <see cref="Windows.Graphics.IGeometrySource2D"/>.
    /// </summary>
    /// <param name="compositor"><see cref="Microsoft.UI.Composition.Compositor"/></param>
    /// <param name="geometry"><see cref="Windows.Graphics.IGeometrySource2D"/></param>
    /// <returns>CompositionGeometricClip</returns>
    public static Microsoft.UI.Composition.CompositionGeometricClip CreateGeometricClip(this Microsoft.UI.Composition.Compositor compositor, Windows.Graphics.IGeometrySource2D geometry)
    {
        // Create the CompositionPath
        var path = new Microsoft.UI.Composition.CompositionPath(geometry);
        // Create the CompositionPathGeometry
        var pathGeometry = compositor.CreatePathGeometry(path);
        // Create the CompositionGeometricClip
        return compositor.CreateGeometricClip(pathGeometry);
    }

    public static async Task LaunchUrlFromTextBox(Microsoft.UI.Xaml.Controls.TextBox textBox)
    {
        string text = "";
        textBox.DispatcherQueue.TryEnqueue(() => { text = textBox.Text; });
        Uri? uriResult;
        bool isValidUrl = Uri.TryCreate(text, UriKind.Absolute, out uriResult) && (uriResult.Scheme == Uri.UriSchemeHttp || uriResult.Scheme == Uri.UriSchemeHttps);
        if (isValidUrl)
            await Windows.System.Launcher.LaunchUriAsync(uriResult);
        else
            await Task.CompletedTask;
    }

    public static async Task LocateAndLaunchUrlFromTextBox(Microsoft.UI.Xaml.Controls.TextBox textBox)
    {
        string text = "";
        textBox.DispatcherQueue.TryEnqueue(() => { text = textBox.Text; });
        List<string> urls = text.ExtractUrls();
        if (urls.Count > 0)
        {
            Uri uriResult = new Uri(urls[0]);
            await Windows.System.Launcher.LaunchUriAsync(uriResult);
        }
        else
            await Task.CompletedTask;
    }

    public static async Task LocateAndLaunchUrlFromString(string text)
    {
        List<string> urls = text.ExtractUrls();
        if (urls.Count > 0)
        {
            Uri uriResult = new Uri(urls[0]);
            await Windows.System.Launcher.LaunchUriAsync(uriResult);
        }
        else
            await Task.CompletedTask;
    }

    /// <summary>
    /// Sets the element's Grid.Column property and sets the
    /// ColumnSpan attached property to the specified value.
    /// </summary>
    public static void SetColumnAndSpan(this FrameworkElement element, int column = 0, int columnSpan = 99)
    {
        Grid.SetColumn(element, column);
        Grid.SetColumnSpan(element, columnSpan);
    }

    /// <summary>
    /// Returns the <see cref="Microsoft.UI.Xaml.PropertyPath"/> based on the provided <see cref="Microsoft.UI.Xaml.Data.Binding"/>.
    /// </summary>
    public static string? GetBindingPropertyName(this Microsoft.UI.Xaml.Data.Binding binding)
    {
        return binding?.Path?.Path?.Split('.')?.LastOrDefault();
    }

    public static Windows.Foundation.Size GetTextSize(FontFamily font, double fontSize, string text)
    {
        var tb = new TextBlock { Text = text, FontFamily = font, FontSize = fontSize };
        tb.Measure(new Windows.Foundation.Size(Double.PositiveInfinity, Double.PositiveInfinity));
        return tb.DesiredSize;
    }

    public static bool IsMonospacedFont(FontFamily font)
    {
        var tb1 = new TextBlock { Text = "(!aiZ%#BIm,. ~`", FontFamily = font };
        tb1.Measure(new Windows.Foundation.Size(Double.PositiveInfinity, Double.PositiveInfinity));
        var tb2 = new TextBlock { Text = "...............", FontFamily = font };
        tb2.Measure(new Windows.Foundation.Size(Double.PositiveInfinity, Double.PositiveInfinity));
        var off = Math.Abs(tb1.DesiredSize.Width - tb2.DesiredSize.Width);
        return off < 0.01;
    }

    /// <summary>
    /// Gets a list of the specified FrameworkElement's DependencyProperties. This method will return all
    /// DependencyProperties of the element unless 'useBlockList' is true, in which case all bindings on elements
    /// that are typically not used as input controls will be ignored.
    /// </summary>
    /// <param name="element">FrameworkElement of interest</param>
    /// <param name="useBlockList">If true, ignores elements not typically used for input</param>
    /// <returns>List of DependencyProperties</returns>
    public static List<DependencyProperty> GetDependencyProperties(this FrameworkElement element, bool useBlockList)
    {
        List<DependencyProperty> dependencyProperties = new List<DependencyProperty>();

        bool isBlocklisted = useBlockList &&
            (element is Panel || element is Button || element is Image || element is ScrollViewer ||
             element is TextBlock || element is Border || element is Microsoft.UI.Xaml.Shapes.Shape || element is ContentPresenter);

        if (!isBlocklisted)
        {
            Type type = element.GetType();
            FieldInfo[] fields = type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.FlattenHierarchy);
            foreach (FieldInfo field in fields)
            {
                if (field.FieldType == typeof(DependencyProperty))
                {
                    var dp = (DependencyProperty)field.GetValue(null);
                    if (dp != null)
                        dependencyProperties.Add(dp);
                }
            }
        }

        return dependencyProperties;
    }

    public static bool IsXamlRootAvailable(bool UWP = false)
    {
        if (UWP)
            return Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Windows.UI.Xaml.UIElement", "XamlRoot");
        else
            return Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent("Microsoft.UI.Xaml.UIElement", "XamlRoot");
    }

    /// <summary>
    /// Helper function to calculate an element's rectangle in root-relative coordinates.
    /// </summary>
    public static Windows.Foundation.Rect GetElementRect(this Microsoft.UI.Xaml.FrameworkElement element)
    {
        try
        {
            Microsoft.UI.Xaml.Media.GeneralTransform transform = element.TransformToVisual(null);
            Windows.Foundation.Point point = transform.TransformPoint(new Windows.Foundation.Point());
            return new Windows.Foundation.Rect(point, new Windows.Foundation.Size(element.ActualWidth, element.ActualHeight));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] GetElementRect: {ex.Message}");
            return new Windows.Foundation.Rect(0, 0, 0, 0);
        }
    }

    public static IconElement? GetIcon(string imagePath, string imageExt = ".png")
    {
        IconElement? result = null;

        try
        {
            result = imagePath.ToLowerInvariant().EndsWith(imageExt) ?
                        (IconElement)new BitmapIcon() { UriSource = new Uri(imagePath, UriKind.RelativeOrAbsolute), ShowAsMonochrome = false } :
                        (IconElement)new FontIcon() { Glyph = imagePath };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] GetIcon: {ex.Message}");
        }

        return result;
    }

    public static FontIcon GenerateFontIcon(Windows.UI.Color brush, string glyph = "\uF127", int width = 10, int height = 10)
    {
        return new FontIcon()
        {
            Glyph = glyph,
            FontSize = 1.5,
            Width = (double)width,
            Height = (double)height,
            Foreground = new SolidColorBrush(brush),
        };
    }

    public static async Task<byte[]> AsPng(this UIElement control)
    {
        // Get XAML Visual in BGRA8 format
        var rtb = new RenderTargetBitmap();
        await rtb.RenderAsync(control, (int)control.ActualSize.X, (int)control.ActualSize.Y);

        // Encode as PNG
        var pixelBuffer = (await rtb.GetPixelsAsync()).ToArray();
        IRandomAccessStream mraStream = new InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, mraStream);
        encoder.SetPixelData(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)rtb.PixelWidth,
            (uint)rtb.PixelHeight,
            184,
            184,
            pixelBuffer);
        await encoder.FlushAsync();

        // Transform to byte array
        var bytes = new byte[mraStream.Size];
        await mraStream.ReadAsync(bytes.AsBuffer(), (uint)mraStream.Size, InputStreamOptions.None);

        return bytes;
    }

    /// <summary>
    /// This redundant call can also be found in App.xaml.cs
    /// </summary>
    /// <param name="window"><see cref="Microsoft.UI.Xaml.Window"/></param>
    /// <returns><see cref="Microsoft.UI.Windowing.AppWindow"/></returns>
    public static Microsoft.UI.Windowing.AppWindow GetAppWindow(this Microsoft.UI.Xaml.Window window)
    {
        System.IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        Microsoft.UI.WindowId wndId = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hWnd);
        return Microsoft.UI.Windowing.AppWindow.GetFromWindowId(wndId);
    }

    /// <summary>
    /// This assumes your images reside in an "Assets" folder.
    /// </summary>
    /// <param name="assetName"></param>
    /// <returns><see cref="BitmapImage"/></returns>
    public static BitmapImage? GetImageFromAssets(this string assetName)
    {
        BitmapImage? img = null;

        try
        {
            Uri? uri = new Uri($"ms-appx:///Assets/" + assetName.Replace("./", ""));
            img = new BitmapImage(uri);
            Debug.WriteLine($"[INFO] Image resolved for '{assetName}'");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WARNING] GetImageFromAssets: {ex.Message}");
        }

        return img;
    }

    /// <summary>
    /// Gets a string value from a <see cref="StorageFile"/> located in the application local folder.
    /// </summary>
    /// <param name="fileName">
    /// The relative <see cref="string"/> file path.
    /// </param>
    /// <returns>
    /// The stored <see cref="string"/> value.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// Exception thrown if the <paramref name="fileName"/> is null or empty.
    /// </exception>
    public static async Task<string> ReadLocalFileAsync(string fileName)
    {
        if (string.IsNullOrEmpty(fileName))
            throw new ArgumentNullException(nameof(fileName));

        if (App.IsPackaged)
        {
            var folder = ApplicationData.Current.LocalFolder;
            var file = await folder.GetFileAsync(fileName);
            return await FileIO.ReadTextAsync(file, Windows.Storage.Streams.UnicodeEncoding.Utf8);
        }
        else
        {
            using (TextReader reader = File.OpenText(Path.Combine(AppContext.BaseDirectory, fileName)))
            {
                return await reader.ReadToEndAsync(); // uses UTF8 by default
            }
        }
    }

    public static async Task PackagedWriteToFileAsync(string fileName, string content)
    {
        if (App.IsPackaged)
        {
            StorageFolder localFolder = ApplicationData.Current.LocalFolder;
            StorageFile file = await localFolder.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
            byte[] data = Encoding.UTF8.GetBytes(content);
            IBuffer buffer = data.AsBuffer();
            using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                using (DataWriter writer = new DataWriter(stream))
                {
                    writer.WriteBuffer(buffer);
                    await writer.StoreAsync();
                    writer.DetachStream();
                }
            }
        }
    }

    public static async Task<string> PackagedReadFromFileAsync(string fileName)
    {
        if (App.IsPackaged)
        {
            StorageFolder localFolder = ApplicationData.Current.LocalFolder;
            StorageFile file = await localFolder.GetFileAsync(fileName);
            using (IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read))
            {
                IBuffer buffer = new Windows.Storage.Streams.Buffer((uint)stream.Size);
                await stream.ReadAsync(buffer, buffer.Capacity, InputStreamOptions.None);
                DataReader reader = DataReader.FromBuffer(buffer);
                string content = reader.ReadString(buffer.Length);
                return content;
            }
        }
        else
            throw new Exception("Method is only viable with a packaged application.");
    }

    /// <summary>
    /// Reads the lines of a <see cref="StorageFile"/> asynchronously.
    /// </summary>
    /// <param name="file">The <see cref="StorageFile"/> to read.</param>
    /// <returns>An asynchronous operation that returns an enumerable collection of strings representing the lines of the file.</returns>
    public static async Task<IList<string>> ReadStorageFileLinesAsync(this IStorageFile file)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));

        return await FileIO.ReadLinesAsync(file);
    }

    /// <summary>
    /// Reads the entire contents of a <see cref="StorageFile"/> as a byte array asynchronously.
    /// </summary>
    /// <param name="file">The <see cref="StorageFile"/> to read.</param>
    /// <returns>An asynchronous operation that returns a byte array containing the file's contents.</returns>
    public static async Task<byte[]> ReadStorageFileBufferAsync(this IStorageFile file)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));

        var fileBuffer = await FileIO.ReadBufferAsync(file);
        
        // Convert the IBuffer to a byte array
        byte[] fileData = new byte[fileBuffer.Length];
        fileBuffer.CopyTo(fileData);
        
        return fileData;
    }

    /// <summary>
    /// Reads the contents of a <see cref="StorageFile"/> as a string.
    /// </summary>
    /// <param name="file">The <see cref="StorageFile"/> to read.</param>
    /// <returns>The contents of the file as a string.</returns>
    public static async Task<string> ReadStorageFileAsStringAsync(this IStorageFile file)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));

        using (var stream = await file.OpenAsync(FileAccessMode.Read))
        {
            using (var reader = new StreamReader(stream.AsStreamForRead()))
            {
                return await reader.ReadToEndAsync();
            }
        }
    }

    /// <summary>
    /// Reads the contents of a file as a string.
    /// </summary>
    /// <param name="file">Full path to the file to read.</param>
    /// <returns>The contents of the file as a string.</returns>
    public static async Task<string> ReadStorageFileAsStringAsync(string filePath)
    {
        if (string.IsNullOrEmpty(filePath))
            throw new ArgumentNullException(nameof(filePath));

        var file = await StorageFile.GetFileFromPathAsync(filePath);
        using (var stream = await file.OpenAsync(FileAccessMode.Read))
        {
            using (var reader = new StreamReader(stream.AsStreamForRead()))
            {
                return await reader.ReadToEndAsync();
            }
        }
    }

    /// <summary>
    /// Reads the contents of a <see cref="StorageFile"/> as a byte array.
    /// </summary>
    /// <param name="file">The <see cref="StorageFile"/> to read.</param>
    /// <returns>The contents of the file as a byte array.</returns>
    public static async Task<byte[]> ReadStorageFileAsByteArrayAsync(this IStorageFile file)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));

        using (var stream = await file.OpenAsync(FileAccessMode.Read))
        {
            using (var reader = new BinaryReader(stream.AsStreamForRead()))
            {
                return reader.ReadBytes((int)stream.Size);
            }
        }
    }

    /// <summary>
    /// Write the entire contents of <paramref name="buffer"/> asynchronously.
    /// </summary>
    /// <param name="file">The <see cref="StorageFile"/> to read.</param>
    /// <returns>An asynchronous operation that returns a byte array containing the file's contents.</returns>
    public static async Task WriteStorageFileBufferAsync(this IStorageFile file, byte[] buffer)
    {
        if (buffer.Length == 0)
            throw new ArgumentException(nameof(buffer));
        if (file == null)
            throw new ArgumentNullException(nameof(file));

        await FileIO.WriteBufferAsync(file, buffer.AsBuffer());
    }


    /// <summary>
    /// Writes an array of strings to a <see cref="StorageFile"/> asynchronously.
    /// </summary>
    /// <param name="file">The <see cref="StorageFile"/> to write to.</param>
    /// <param name="lines">The array of strings to write.</param>
    public static async Task WriteStorageFileLinesAsync(this IStorageFile file, string[] lines, bool append = true)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));
        if (lines == null)
            throw new ArgumentNullException(nameof(lines));

        if (append)
            await FileIO.AppendLinesAsync(file, lines);
        else
            await FileIO.WriteLinesAsync(file, lines);
    }

    /// <summary>
    /// Writes an array of strings to a <see cref="StorageFile"/> asynchronously.
    /// </summary>
    /// <param name="file">The <see cref="StorageFile"/> to write to.</param>
    /// <param name="lines">The IEnumerable strings to write.</param>
    public static async Task WriteStorageFileLinesAsync(this IStorageFile file, IEnumerable<string> lines, bool append = true)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));
        if (lines == null)
            throw new ArgumentNullException(nameof(lines));

        if (append)
            await FileIO.AppendLinesAsync(file, lines);
        else
            await FileIO.WriteLinesAsync(file, lines);
    }

    /// <summary>
    /// Writes a string to a <see cref="StorageFile"/>.
    /// </summary>
    /// <param name="file">The <see cref="StorageFile"/> to write to.</param>
    /// <param name="content">The string to write.</param>
    public static async Task WriteStringToStorageFileAsync(this IStorageFile file, string content)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));

        if (string.IsNullOrEmpty(content))
            throw new ArgumentException("Content cannot be null or empty.", nameof(content));

        using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            using (var writer = new StreamWriter(stream.AsStreamForWrite()))
            {
                await writer.WriteAsync(content);
            }
        }
    }

    /// <summary>
    /// Writes a byte array to a <see cref="StorageFile"/>.
    /// </summary>
    /// <param name="file">The <see cref="StorageFile"/> to write to.</param>
    /// <param name="data">The byte array to write.</param>
    public static async Task WriteByteArrayToStorageFileAsync(this IStorageFile file, byte[] data)
    {
        if (file == null)
            throw new ArgumentNullException(nameof(file));
        if (data == null)
            throw new ArgumentNullException(nameof(data));

        using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
        {
            using (var writer = new BinaryWriter(stream.AsStreamForWrite()))
            {
                writer.Write(data);
            }
        }
    }

    /// <summary>
    /// Starts an animation and returns a <see cref="Task"/> that reports when it completes.
    /// </summary>
    /// <param name="storyboard">The target storyboard to start.</param>
    /// <returns>A <see cref="Task"/> that completes when <paramref name="storyboard"/> completes.</returns>
    public static Task BeginAsync(this Storyboard storyboard)
    {
        TaskCompletionSource<object?> taskCompletionSource = new TaskCompletionSource<object?>();

        void OnCompleted(object? sender, object e)
        {
            if (sender is Storyboard storyboard)
                storyboard.Completed -= OnCompleted;

            taskCompletionSource.SetResult(null);
        }

        storyboard.Completed += OnCompleted;
        storyboard.Begin();

        return taskCompletionSource.Task;
    }

    /// <summary>
    /// To get all buttons contained in a StackPanel:
    /// IEnumerable{Button} kids = GetChildren(rootStackPanel).Where(ctrl => ctrl is Button).Cast{Button}();
    /// </summary>
    /// <remarks>You must call this on a UI thread.</remarks>
    public static IEnumerable<UIElement> GetChildren(this UIElement parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (VisualTreeHelper.GetChild(parent, i) is UIElement child)
            {
                yield return child;
            }
        }
    }

    /// <summary>
    /// Walks the visual tree to determine if a particular child is contained within a parent DependencyObject.
    /// </summary>
    /// <param name="element">Parent DependencyObject</param>
    /// <param name="child">Child DependencyObject</param>
    /// <returns>True if the parent element contains the child</returns>
    public static bool ContainsChild(this DependencyObject element, DependencyObject child)
    {
        if (element != null)
        {
            while (child != null)
            {
                if (child == element)
                    return true;

                // Walk up the visual tree. If the root is hit, try using the framework element's
                // parent. This is done because Popups behave differently with respect to the visual tree,
                // and it could have a parent even if the VisualTreeHelper doesn't find it.
                DependencyObject parent = VisualTreeHelper.GetParent(child);
                if (parent == null)
                {
                    FrameworkElement? childElement = child as FrameworkElement;
                    if (childElement != null)
                    {
                        parent = childElement.Parent;
                    }
                }
                child = parent;
            }
        }
        return false;
    }

    /// <summary>
    /// Provides the distance in a <see cref="Point"/> from the passed in element to the element being called on.
    /// For instance, calling child.CoordinatesFrom(container) will return the position of the child within the container.
    /// Helper for <see cref="UIElement.TransformToVisual(UIElement)"/>.
    /// </summary>
    /// <param name="target">Element to measure distance.</param>
    /// <param name="parent">Starting parent element to provide coordinates from.</param>
    /// <returns><see cref="Windows.Foundation.Point"/> containing difference in position of elements.</returns>
    public static Windows.Foundation.Point CoordinatesFrom(this UIElement target, UIElement parent)
    {
        return target.TransformToVisual(parent).TransformPoint(default(Windows.Foundation.Point));
    }

    /// <summary>
    /// Provides the distance in a <see cref="Point"/> to the passed in element from the element being called on.
    /// For instance, calling container.CoordinatesTo(child) will return the position of the child within the container.
    /// Helper for <see cref="UIElement.TransformToVisual(UIElement)"/>.
    /// </summary>
    /// <param name="parent">Starting parent element to provide coordinates from.</param>
    /// <param name="target">Element to measure distance to.</param>
    /// <returns><see cref="Windows.Foundation.Point"/> containing difference in position of elements.</returns>
    public static Windows.Foundation.Point CoordinatesTo(this UIElement parent, UIElement target)
    {
        return target.TransformToVisual(parent).TransformPoint(default(Windows.Foundation.Point));
    }

    public static bool IsCtrlKeyDown()
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        return ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    public static bool IsAltKeyDown()
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu);
        return ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
    }

    public static bool IsCapsLockOn()
    {
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.CapitalLock);
        return ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Locked);
    }

    /// <summary>
    /// I created this to show what controls are members of <see cref="Microsoft.UI.Xaml.FrameworkElement"/>.
    /// </summary>
    public static void FindControlsInheritingFromFrameworkElement()
    {
        var controlAssembly = typeof(Microsoft.UI.Xaml.Controls.Control).GetTypeInfo().Assembly;
        var controlTypes = controlAssembly.GetTypes()
            .Where(type => type.Namespace == "Microsoft.UI.Xaml.Controls" &&
            typeof(Microsoft.UI.Xaml.FrameworkElement).IsAssignableFrom(type));

        foreach (var controlType in controlTypes)
        {
            Debug.WriteLine($"[FrameworkElement] ControlInheritingFrom: {controlType.FullName}");
        }
    }

    public static IEnumerable<Type?> GetHierarchyFromUIElement(this Type element)
    {
        if (element.GetTypeInfo().IsSubclassOf(typeof(UIElement)) != true)
        {
            yield break;
        }

        Type? current = element;

        while (current != null && current != typeof(UIElement))
        {
            yield return current;
            current = current?.GetTypeInfo()?.BaseType;
        }
    }

    public static void DumpControlsInheritingFromPanel()
    {
        var controlAssembly = typeof(Microsoft.UI.Xaml.Controls.Control).GetTypeInfo().Assembly;
        var controlTypes = controlAssembly.GetTypes().Where(type => type.IsSubclassOf(typeof(Microsoft.UI.Xaml.Controls.Panel)));
        foreach (var controlType in controlTypes)
        {
            Debug.WriteLine($"[DEBUG] SubClassOfPanel: {controlType.FullName}");
        }
    }

    public static void DisplayRoutedEventsForUIElement()
    {
        Type uiElementType = typeof(Microsoft.UI.Xaml.UIElement);
        var routedEvents = uiElementType.GetEvents();
        Debug.WriteLine($"[All RoutedEvents for {nameof(Microsoft.UI.Xaml.UIElement)}]");
        foreach (var routedEvent in routedEvents)
        {
            if (routedEvent.EventHandlerType == typeof(Microsoft.UI.Xaml.RoutedEventHandler) ||
                routedEvent.EventHandlerType == typeof(Microsoft.UI.Xaml.RoutedEvent) ||
                routedEvent.EventHandlerType == typeof(EventHandler))
            {
                Debug.WriteLine($" - '{routedEvent.Name}'");
            }
            else if (routedEvent.MemberType == System.Reflection.MemberTypes.Event)
            {
                Debug.WriteLine($" - '{routedEvent.Name}'");
            }
        }
    }

    public static void DisplayRoutedEventsForFrameworkElement()
    {
        Type fwElementType = typeof(Microsoft.UI.Xaml.FrameworkElement);
        var routedEvents = fwElementType.GetEvents();
        Debug.WriteLine($"[All RoutedEvents for {nameof(Microsoft.UI.Xaml.FrameworkElement)}]");
        foreach (var routedEvent in routedEvents)
        {
            if (routedEvent.EventHandlerType == typeof(Microsoft.UI.Xaml.RoutedEventHandler) ||
                routedEvent.EventHandlerType == typeof(Microsoft.UI.Xaml.RoutedEvent) ||
                routedEvent.EventHandlerType == typeof(EventHandler))
            {
                Debug.WriteLine($" - '{routedEvent.Name}'");
            }
            else if (routedEvent.MemberType == System.Reflection.MemberTypes.Event)
            {
                Debug.WriteLine($" - '{routedEvent.Name}'");
            }
        }
    }

    public static void DisplayRoutedEventsForControl()
    {
        Type ctlElementType = typeof(Microsoft.UI.Xaml.Controls.Control);
        var routedEvents = ctlElementType.GetEvents();
        Debug.WriteLine($"[All RoutedEvents for {nameof(Microsoft.UI.Xaml.Controls.Control)}]");
        foreach (var routedEvent in routedEvents)
        {
            if (routedEvent.EventHandlerType == typeof(Microsoft.UI.Xaml.RoutedEventHandler) ||
                routedEvent.EventHandlerType == typeof(Microsoft.UI.Xaml.RoutedEvent) ||
                routedEvent.EventHandlerType == typeof(EventHandler))
            {
                Debug.WriteLine($" - '{routedEvent.Name}'");
            }
            else if (routedEvent.MemberType == System.Reflection.MemberTypes.Event)
            {
                Debug.WriteLine($" - '{routedEvent.Name}'");
            }
        }
    }

    /// <summary>
    /// This should be moved to a shared module, but I want to keep these behaviors portable.
    /// </summary>
    public static Microsoft.UI.Composition.CompositionEasingFunction CreatePennerEquation(Microsoft.UI.Composition.Compositor compositor, string pennerType = "SineEaseInOut")
    {
        System.Numerics.Vector2 controlPoint1;
        System.Numerics.Vector2 controlPoint2;
        switch (pennerType)
        {
            case "SineEaseIn":
            case "EaseInSine":
                controlPoint1 = new System.Numerics.Vector2(0.47f, 0.0f);
                controlPoint2 = new System.Numerics.Vector2(0.745f, 0.715f);
                break;
            case "SineEaseOut":
            case "EaseOutSine":
                controlPoint1 = new System.Numerics.Vector2(0.39f, 0.575f);
                controlPoint2 = new System.Numerics.Vector2(0.565f, 1.0f);
                break;
            case "SineEaseInOut":
            case "EaseInOutSine":
                controlPoint1 = new System.Numerics.Vector2(0.445f, 0.05f);
                controlPoint2 = new System.Numerics.Vector2(0.55f, 0.95f);
                break;
            case "QuadEaseIn":
            case "EaseInQuad":
                controlPoint1 = new System.Numerics.Vector2(0.55f, 0.085f);
                controlPoint2 = new System.Numerics.Vector2(0.68f, 0.53f);
                break;
            case "QuadEaseOut":
            case "EaseOutQuad":
                controlPoint1 = new System.Numerics.Vector2(0.25f, 0.46f);
                controlPoint2 = new System.Numerics.Vector2(0.45f, 0.94f);
                break;
            case "QuadEaseInOut":
            case "EaseInOutQuad":
                controlPoint1 = new System.Numerics.Vector2(0.445f, 0.03f);
                controlPoint2 = new System.Numerics.Vector2(0.515f, 0.955f);
                break;
            case "CubicEaseIn":
            case "EaseInCubic":
                controlPoint1 = new System.Numerics.Vector2(0.55f, 0.055f);
                controlPoint2 = new System.Numerics.Vector2(0.675f, 0.19f);
                break;
            case "CubicEaseOut":
            case "EaseOutCubic":
                controlPoint1 = new System.Numerics.Vector2(0.215f, 0.61f);
                controlPoint2 = new System.Numerics.Vector2(0.355f, 1.0f);
                break;
            case "CubicEaseInOut":
            case "EaseInOutCubic":
                controlPoint1 = new System.Numerics.Vector2(0.645f, 0.045f);
                controlPoint2 = new System.Numerics.Vector2(0.355f, 1.0f);
                break;
            case "QuarticEaseIn":
            case "EaseInQuartic":
                controlPoint1 = new System.Numerics.Vector2(0.895f, 0.03f);
                controlPoint2 = new System.Numerics.Vector2(0.685f, 0.22f);
                break;
            case "QuarticEaseOut":
            case "EaseOutQuartic":
                controlPoint1 = new System.Numerics.Vector2(0.165f, 0.84f);
                controlPoint2 = new System.Numerics.Vector2(0.44f, 1.0f);
                break;
            case "QuarticEaseInOut":
            case "EaseInOutQuartic":
                controlPoint1 = new System.Numerics.Vector2(0.77f, 0.0f);
                controlPoint2 = new System.Numerics.Vector2(0.175f, 1.0f);
                break;
            case "QuinticEaseIn":
            case "EaseInQuintic":
                controlPoint1 = new System.Numerics.Vector2(0.755f, 0.05f);
                controlPoint2 = new System.Numerics.Vector2(0.855f, 0.06f);
                break;
            case "QuinticEaseOut":
            case "EaseOutQuintic":
                controlPoint1 = new System.Numerics.Vector2(0.23f, 1.0f);
                controlPoint2 = new System.Numerics.Vector2(0.32f, 1.0f);
                break;
            case "QuinticEaseInOut":
            case "EaseInOutQuintic":
                controlPoint1 = new System.Numerics.Vector2(0.86f, 0.0f);
                controlPoint2 = new System.Numerics.Vector2(0.07f, 1.0f);
                break;
            case "ExponentialEaseIn":
            case "EaseInExponential":
                controlPoint1 = new System.Numerics.Vector2(0.95f, 0.05f);
                controlPoint2 = new System.Numerics.Vector2(0.795f, 0.035f);
                break;
            case "ExponentialEaseOut":
            case "EaseOutExponential":
                controlPoint1 = new System.Numerics.Vector2(0.19f, 1.0f);
                controlPoint2 = new System.Numerics.Vector2(0.22f, 1.0f);
                break;
            case "ExponentialEaseInOut":
            case "EaseInOutExponential":
                controlPoint1 = new System.Numerics.Vector2(1.0f, 0.0f);
                controlPoint2 = new System.Numerics.Vector2(0.0f, 1.0f);
                break;
            case "CircleEaseIn":
                controlPoint1 = new System.Numerics.Vector2(0.6f, 0.04f);
                controlPoint2 = new System.Numerics.Vector2(0.98f, 0.335f);
                break;
            case "CircleEaseOut":
                controlPoint1 = new System.Numerics.Vector2(0.075f, 0.82f);
                controlPoint2 = new System.Numerics.Vector2(0.165f, 1.0f);
                break;
            case "CircleEaseInOut":
                controlPoint1 = new System.Numerics.Vector2(0.785f, 0.135f);
                controlPoint2 = new System.Numerics.Vector2(0.15f, 0.86f);
                break;
            case "BackEaseIn":
                controlPoint1 = new System.Numerics.Vector2(0.6f, -0.28f);
                controlPoint2 = new System.Numerics.Vector2(0.735f, 0.045f);
                break;
            case "BackEaseOut":
                controlPoint1 = new System.Numerics.Vector2(0.175f, 0.885f);
                controlPoint2 = new System.Numerics.Vector2(0.32f, 1.275f);
                break;
            case "BackEaseInOut":
                controlPoint1 = new System.Numerics.Vector2(0.68f, -0.55f);
                controlPoint2 = new System.Numerics.Vector2(0.265f, 1.55f);
                break;
            default:
                controlPoint1 = new System.Numerics.Vector2(0.0f);
                controlPoint2 = new System.Numerics.Vector2(0.0f);
                break;
        }
        Microsoft.UI.Composition.CompositionEasingFunction pennerEquation = compositor.CreateCubicBezierEasingFunction(controlPoint1, controlPoint2);
        return pennerEquation;
    }

    /// <summary>
    /// Converts a <see cref="string"/> value to a <see cref="Vector2"/> value.
    /// This method always assumes the invariant culture for parsing values (',' separates numbers, '.' is the decimal separator).
    /// The input text can either represents a single number (mapped to <see cref="Vector2(float)"/>, or multiple components.
    /// Additionally, the format "&lt;float, float&gt;" is also allowed (though less efficient to parse).
    /// </summary>
    /// <param name="text">A <see cref="string"/> with the values to parse.</param>
    /// <returns>The parsed <see cref="Vector2"/> value.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="text"/> doesn't represent a valid <see cref="Vector2"/> value.</exception>
    [Pure]
    public static System.Numerics.Vector2 ToVector2(this string text)
    {
        if (text.Length == 0)
        {
            return System.Numerics.Vector2.Zero;
        }
        else
        {
            // The format <x> or <x, y> is supported
            text = Unbracket(text);

            // Skip allocations when only a component is used
            if (text.IndexOf(',') == -1)
            {
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float x))
                {
                    return new(x);
                }
            }
            else
            {
                string[] values = text.Split(',');

                if (values.Length == 2)
                {
                    if (float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                        float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y))
                    {
                        return new(x, y);
                    }
                }
            }
        }

        return Throw(text);

        static System.Numerics.Vector2 Throw(string text) => throw new FormatException($"Cannot convert \"{text}\" to {nameof(System.Numerics.Vector2)}. Use the format \"float, float\"");
    }

    /// <summary>
    /// Converts a <see cref="string"/> value to a <see cref="Vector3"/> value.
    /// This method always assumes the invariant culture for parsing values (',' separates numbers, '.' is the decimal separator).
    /// The input text can either represents a single number (mapped to <see cref="Vector3(float)"/>, or multiple components.
    /// Additionally, the format "&lt;float, float, float&gt;" is also allowed (though less efficient to parse).
    /// </summary>
    /// <param name="text">A <see cref="string"/> with the values to parse.</param>
    /// <returns>The parsed <see cref="Vector3"/> value.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="text"/> doesn't represent a valid <see cref="Vector3"/> value.</exception>
    [Pure]
    public static System.Numerics.Vector3 ToVector3(this string text)
    {
        if (text.Length == 0)
        {
            return System.Numerics.Vector3.Zero;
        }
        else
        {
            text = Unbracket(text);

            if (text.IndexOf(',') == -1)
            {
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float x))
                {
                    return new(x);
                }
            }
            else
            {
                string[] values = text.Split(',');

                if (values.Length == 3)
                {
                    if (float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                        float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                        float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                    {
                        return new(x, y, z);
                    }
                }
                else if (values.Length == 2)
                {
                    return new(text.ToVector2(), 0);
                }
            }
        }

        return Throw(text);

        static System.Numerics.Vector3 Throw(string text) => throw new FormatException($"Cannot convert \"{text}\" to {nameof(System.Numerics.Vector3)}. Use the format \"float, float, float\"");
    }

    /// <summary>
    /// Converts a <see cref="string"/> value to a <see cref="Vector4"/> value.
    /// This method always assumes the invariant culture for parsing values (',' separates numbers, '.' is the decimal separator).
    /// The input text can either represents a single number (mapped to <see cref="Vector4(float)"/>, or multiple components.
    /// Additionally, the format "&lt;float, float, float, float&gt;" is also allowed (though less efficient to parse).
    /// </summary>
    /// <param name="text">A <see cref="string"/> with the values to parse.</param>
    /// <returns>The parsed <see cref="Vector4"/> value.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="text"/> doesn't represent a valid <see cref="Vector4"/> value.</exception>
    [Pure]
    public static System.Numerics.Vector4 ToVector4(this string text)
    {
        if (text.Length == 0)
        {
            return System.Numerics.Vector4.Zero;
        }
        else
        {
            text = Unbracket(text);

            if (text.IndexOf(',') == -1)
            {
                if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float x))
                {
                    return new(x);
                }
            }
            else
            {
                string[] values = text.Split(',');

                if (values.Length == 4)
                {
                    if (float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                        float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                        float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) &&
                        float.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float w))
                    {
                        return new(x, y, z, w);
                    }
                }
                else if (values.Length == 3)
                {
                    return new(text.ToVector3(), 0);
                }
                else if (values.Length == 2)
                {
                    return new(text.ToVector2(), 0, 0);
                }
            }
        }

        return Throw(text);

        static System.Numerics.Vector4 Throw(string text) => throw new FormatException($"Cannot convert \"{text}\" to {nameof(System.Numerics.Vector4)}. Use the format \"float, float, float, float\"");
    }

    /// <summary>
    /// Converts a <see cref="string"/> value to a <see cref="Quaternion"/> value.
    /// This method always assumes the invariant culture for parsing values (',' separates numbers, '.' is the decimal separator).
    /// Additionally, the format "&lt;float, float, float, float&gt;" is also allowed (though less efficient to parse).
    /// </summary>
    /// <param name="text">A <see cref="string"/> with the values to parse.</param>
    /// <returns>The parsed <see cref="Quaternion"/> value.</returns>
    /// <exception cref="FormatException">Thrown when <paramref name="text"/> doesn't represent a valid <see cref="Quaternion"/> value.</exception>
    [Pure]
    public static System.Numerics.Quaternion ToQuaternion(this string text)
    {
        if (text.Length == 0)
        {
            return new();
        }
        else
        {
            text = Unbracket(text);

            string[] values = text.Split(',');

            if (values.Length == 4)
            {
                if (float.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) &&
                    float.TryParse(values[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float w))
                {
                    return new(x, y, z, w);
                }
            }
        }

        return Throw(text);

        static System.Numerics.Quaternion Throw(string text) => throw new FormatException($"Cannot convert \"{text}\" to {nameof(System.Numerics.Quaternion)}. Use the format \"float, float, float, float\"");
    }

    /// <summary>
    /// Converts an angle bracketed <see cref="string"/> value to its unbracketed form (e.g. "&lt;float, float&gt;" to "float, float").
    /// If the value is already unbracketed, this method will return the value unchanged.
    /// </summary>
    /// <param name="text">A bracketed <see cref="string"/> value.</param>
    /// <returns>The unbracketed <see cref="string"/> value.</returns>
    static string Unbracket(string text)
    {
        if (text.Length >= 2 &&
            text[0] == '<' &&
            text[text.Length - 1] == '>')
        {
            text = text.Substring(1, text.Length - 2);
        }

        return text;
    }

    /// <summary>
    /// Gets the image data from a Uri.
    /// </summary>
    /// <param name="uri">Image Uri</param>
    /// <returns>Image Stream as <see cref="Windows.Storage.Streams.IRandomAccessStream"/></returns>
    public static async Task<Windows.Storage.Streams.IRandomAccessStream?> GetImageStream(this Uri uri)
    {
        Windows.Storage.Streams.IRandomAccessStream? imageStream = null;
        string localPath = string.Empty;
        if (uri.LocalPath.StartsWith("\\\\"))
            localPath = $"{uri.LocalPath}".Replace("//", "/");
        else
            localPath = $"{uri.Host}/{uri.LocalPath}".Replace("//", "/");

        // If we don't have Internet, then try to see if we have a packaged copy.
        try
        {
            if (App.IsPackaged)
            {
                imageStream = await GetPackagedFileStreamAsync(localPath);
            }
            else
            {
                imageStream = await GetLocalFileStreamAsync(localPath);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[INFO] {localPath}");
            Debug.WriteLine($"[WARNING] GetImageStream: {ex.Message}");
        }

        return imageStream;
    }

    /// <summary>
    /// Gets a stream to a specified file from the installation folder.
    /// </summary>
    /// <param name="fileName">Relative name of the file to open. Can contains subfolders.</param>
    /// <param name="accessMode">File access mode. Default is read.</param>
    /// <returns>The file stream</returns>
    public static Task<IRandomAccessStream> GetPackagedFileStreamAsync(string fileName, FileAccessMode accessMode = FileAccessMode.Read)
    {
        StorageFolder workingFolder = Package.Current.InstalledLocation;
        return GetFileStreamAsync(fileName, accessMode, workingFolder);
    }

    /// <summary>
    /// Gets a stream to a specified file from the application local folder.
    /// </summary>
    /// <param name="fileName">Relative name of the file to open. Can contains subfolders.</param>
    /// <param name="accessMode">File access mode. Default is read.</param>
    /// <returns>The file stream</returns>
    public static Task<IRandomAccessStream> GetLocalFileStreamAsync(string fileName, FileAccessMode accessMode = FileAccessMode.Read)
    {
        StorageFolder workingFolder = ApplicationData.Current.LocalFolder;
        return GetFileStreamAsync(fileName, accessMode, workingFolder);
    }

    static async Task<IRandomAccessStream> GetFileStreamAsync(string fullFileName, FileAccessMode accessMode, StorageFolder workingFolder)
    {
        var fileName = Path.GetFileName(fullFileName);
        workingFolder = await GetSubFolderAsync(fullFileName, workingFolder);
        var file = await workingFolder.GetFileAsync(fileName);
        return await file.OpenAsync(accessMode);
    }

    static async Task<StorageFolder> GetSubFolderAsync(string fullFileName, StorageFolder workingFolder)
    {
        var folderName = Path.GetDirectoryName(fullFileName);
        if (!string.IsNullOrEmpty(folderName) && folderName != @"\")
        {
            return await workingFolder.GetFolderAsync(folderName);
        }
        return workingFolder;
    }


    /// <summary>
    /// Gets <see cref="IRandomAccessStreamReference"/> for the <paramref name="file"/>.
    /// </summary>
    public static IRandomAccessStreamReference? GetRandomAccessStreamReferenceAsync(this StorageFile file)
    {
        if (file is not null)
           return RandomAccessStreamReference.CreateFromFile(file);

        return null;
    }

    #region [File I/O]
    public static async Task<bool> WriteBytesUsingDataWriterAsync(StorageFile file, byte[] data)
    {
        if (file is not null)
            return await WriteBytesUsingDataWriterAsync(file.Path, data);

        return false;
    }

    public static async Task<bool> WriteBytesUsingDataWriterAsync(string filePath, byte[] data)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                using (Windows.Storage.Streams.IRandomAccessStream stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
                {
                    using (DataWriter dataWriter = new DataWriter(stream))
                    {
                        dataWriter.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;
                        dataWriter.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;

                        dataWriter.WriteBytes(data);
                        await dataWriter.StoreAsync();
                        //await dataWriter.FlushAsync();

                        /** [NOTE ON FLUSHING]
                        DataWriter.StoreAsync() handles flushing:
                           - The DataWriter.StoreAsync() method, which you are already using, 
                             takes care of flushing the data to the underlying stream. It waits 
                             until the data is written to the storage before returning.
                        Potential for performance impact:
                           - Adding writer.FlushAsync() might introduce unnecessary overhead, 
                             especially if you are writing large amounts of data. It can cause 
                             a slight performance impact.
                        DataWriter is designed for efficiency:
                           - The DataWriter class is designed to be efficient for writing data 
                             to streams. It buffers data internally and flushes it to the stream 
                             at appropriate times.
                        **/
                    }
                }
                return true;
            }
            else
            {
                string? folderPath = System.IO.Path.GetDirectoryName(filePath);
                string? fileName = System.IO.Path.GetFileName(filePath);
                Windows.Storage.StorageFolder storageFolder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(folderPath);
                Windows.Storage.StorageFile file = await storageFolder.CreateFileAsync(fileName, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                using (Windows.Storage.Streams.IRandomAccessStream stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
                {
                    using (DataWriter dataWriter = new DataWriter(stream))
                    {
                        dataWriter.WriteBytes(data);
                        await dataWriter.StoreAsync();
                        //await dataWriter.FlushAsync();

                        /** [NOTE ON FLUSHING]
                        DataWriter.StoreAsync() handles flushing:
                           - The DataWriter.StoreAsync() method, which you are already using, 
                             takes care of flushing the data to the underlying stream. It waits 
                             until the data is written to the storage before returning.
                        Potential for performance impact:
                           - Adding writer.FlushAsync() might introduce unnecessary overhead, 
                             especially if you are writing large amounts of data. It can cause 
                             a slight performance impact.
                        DataWriter is designed for efficiency:
                           - The DataWriter class is designed to be efficient for writing data 
                             to streams. It buffers data internally and flushes it to the stream 
                             at appropriate times.
                        **/
                    }
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] WriteBytesUsingDataWriterAsync: {ex.Message}");
            return false;
        }
    }

    public static async Task<byte[]> ReadBytesUsingDataReaderAsync(StorageFile file)
    {
        if (file is not null)
            return await ReadBytesUsingDataReaderAsync(file.Path);

        return new byte[0];
    }

    public static async Task<byte[]> ReadBytesUsingDataReaderAsync(string filePath)
    {
        if (File.Exists(filePath))
        {
            var buff = await FileIO.ReadBufferAsync(await StorageFile.GetFileFromPathAsync(filePath));

            Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
            using (Windows.Storage.Streams.IRandomAccessStream stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read))
            {
                using (DataReader reader = new DataReader(stream))
                {
                    reader.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;
                    reader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                    byte[] bytes = new byte[stream.Size];
                    await reader.LoadAsync((uint)stream.Size);
                    reader.ReadBytes(bytes);
                    return bytes;
                }
            }
        }

        return new byte[0];
    }

    /// <summary>
    /// Reads the entire contents of a file into an IBuffer
    /// </summary>
    /// <param name="filePath"></param>
    /// <returns><see cref="IBuffer"/></returns>
    public static async Task<IBuffer?> ReadFileUsingReadBufferAsync(string filePath)
    {
        if (File.Exists(filePath))
        {
            IBuffer? buffer = await FileIO.ReadBufferAsync(await StorageFile.GetFileFromPathAsync(filePath));
            return buffer;
        }
        return null;
    }

    public static async Task<byte[]> ReadFileBytesUsingReadBufferAsync(string filePath)
    {
        if (File.Exists(filePath))
        {
            IBuffer? buffer = await FileIO.ReadBufferAsync(await StorageFile.GetFileFromPathAsync(filePath));
            return buffer.ToArray();
        }
        return new byte[0];
    }

    public static async Task<bool> WriteFileUsingWriteBufferAsync(string filePath, IBuffer buffer)
    {
        if (File.Exists(filePath))
        {
            await FileIO.WriteBufferAsync(await StorageFile.GetFileFromPathAsync(filePath), buffer);
            return true;
        }
        return false;
    }

    public static async Task<bool> WriteFileUsingWriteBufferAsync(string filePath, byte[] buffer)
    {
        if (File.Exists(filePath) && buffer.Length > 0)
        {
            await FileIO.WriteBufferAsync(await StorageFile.GetFileFromPathAsync(filePath), buffer.AsBuffer());
            return true;
        }
        return false;
    }

    /// <summary>
    /// Reads the entire contents of a file into a string
    /// </summary>
    /// <param name="filePath"></param>
    /// <returns><see cref="String"/></returns>
    public static async Task<string?> ReadFileAsStringAsync(string filePath)
    {
        if (File.Exists(filePath))
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
                using (var stream = await file.OpenAsync(FileAccessMode.Read))
                {
                    using (var reader = new DataReader(stream))
                    {
                        reader.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;
                        reader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;

                        uint size = (uint)stream.Size;
                        await reader.LoadAsync(size);
                        return reader.ReadString(size);
                    }
                }
            }
            catch (Exception)
            {
                return null; // problem reading the file
            }
        }
        return string.Empty; // there was no file to read
    }

    /// <summary>
    /// Reads the entire contents of a file into a string
    /// </summary>
    /// <param name="filePath"></param>
    /// <returns><see cref="String"/></returns>
    public static async Task<string?> ReadFileAsStringAsync(string filePath, ulong startPosition)
    {
        if (File.Exists(filePath))
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
                using (var stream = await file.OpenAsync(FileAccessMode.Read))
                {
                    using (var streamPosition = stream.GetInputStreamAt(startPosition))
                    {
                        using (var reader = new DataReader(streamPosition))
                        {
                            reader.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;
                            reader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;

                            uint size = (uint)stream.Size;
                            await reader.LoadAsync(size);
                            return reader.ReadString(size);
                        }
                    }
                }
            }
            catch (Exception)
            {
                return null; // problem reading the file
            }
        }
        return string.Empty; // there was no file to read
    }

    /// <summary>
    /// Writes a string to a file
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="content"></param>
    public static async Task<bool> WriteStringToFileAsync(string filePath, string content)
    {
        try
        {
            StorageFile? file;
            if (File.Exists(filePath))
            {
                file = await StorageFile.GetFileFromPathAsync(filePath);
            }
            else // Create the file if it doesn't exist
            {
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(filePath));
                file = await folder.CreateFileAsync(Path.GetFileName(filePath), CreationCollisionOption.ReplaceExisting);
            }

            // This will throw an exception if the file does not exist.
            file = await StorageFile.GetFileFromPathAsync(filePath);

            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                using (var writer = new DataWriter(stream))
                {
                    writer.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                    writer.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;
                    writer.WriteString(content);
                    await writer.StoreAsync();
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] WriteStringToFileAsync: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// Writes a string to a file
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="content"></param>
    public static async Task<bool> WriteStringToFileAsync(string filePath, string content, ulong startPosition)
    {
        try
        {
            StorageFile? file;
            if (File.Exists(filePath))
            {
                file = await StorageFile.GetFileFromPathAsync(filePath);
            }
            else // Create the file if it doesn't exist
            {
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(filePath));
                file = await folder.CreateFileAsync(Path.GetFileName(filePath), CreationCollisionOption.ReplaceExisting);
            }

            // This will throw an exception if the file does not exist.
            file = await StorageFile.GetFileFromPathAsync(filePath);

            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                using (var streamPosition = stream.GetOutputStreamAt(startPosition))
                {
                    using (var writer = new DataWriter(streamPosition))
                    {
                        writer.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;
                        writer.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                        writer.WriteString(content);
                        await writer.StoreAsync();
                    }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] WriteStringToFileAsync: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// Appends a string to a file
    /// </summary>
    /// <param name="filePath"></param>
    /// <param name="content"></param>
    public static async Task<bool> AppendStringToFileAsync(string filePath, string content)
    {
        try
        {
            StorageFile? file;
            if (File.Exists(filePath))
            {
                file = await StorageFile.GetFileFromPathAsync(filePath);
            }
            else // Create the file if it doesn't exist
            {
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(filePath));
                file = await folder.CreateFileAsync(Path.GetFileName(filePath), CreationCollisionOption.ReplaceExisting);
            }

            // This will throw an exception if the file does not exist.
            file = await StorageFile.GetFileFromPathAsync(filePath);
            
            using (var stream = await file.OpenAsync(FileAccessMode.ReadWrite))
            {
                stream.Seek(stream.Size); // Move to the end of the file
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteString(content);
                    await writer.StoreAsync();
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] WriteStringToFileAsync: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// Copies a file to a new location via <see cref="StorageFile.CopyAsync(IStorageFolder, string, NameCollisionOption)"/>.
    /// </summary>
    /// <param name="sourcePath"></param>
    /// <param name="destinationPath"></param>
    public static async Task<bool> CopyFileAsync(string sourcePath, string destinationPath)
    {
        try
        {
            StorageFile sourceFile = await StorageFile.GetFileFromPathAsync(sourcePath);
            await sourceFile.CopyAsync(await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destinationPath)), Path.GetFileName(destinationPath), NameCollisionOption.ReplaceExisting);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] CopyFileAsync: {ex.Message}");
        }
        return false;
    }

    /// <summary>
    /// Deletes a file via <see cref="StorageFile.DeleteAsync()"/>.
    /// </summary>
    /// <param name="filePath">target file</param>
    public static async Task<bool> DeleteFileAsync(string filePath)
    {
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
            await file.DeleteAsync();
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] DeleteFileAsync: {ex.Message}");
        }
        return false;

    }

    /// <summary>
    /// Fetches the <see cref="BasicProperties"/> for the given <paramref name="filePath"/>.
    /// </summary>
    /// <param name="filePath">target file</param>
    public static async Task<BasicProperties?> GetFilePropertiesAsync(string filePath)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(filePath);
        return await file.GetBasicPropertiesAsync();
    }

    /// <summary>
    /// Testing <see cref="DateTimeOffset"/> write via the <see cref="DataWriter"/>
    /// </summary>
    public static async Task<bool> WriteDTOUsingDataWriterAsync(string filePath, DateTimeOffset dt)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
                using (Windows.Storage.Streams.IRandomAccessStream stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
                {
                    using (DataWriter writer = new DataWriter(stream))
                    {
                        writer.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                        writer.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;
                        writer.WriteDateTime(dt);
                        await writer.StoreAsync();
                    }
                }
                return true;
            }
            else
            {
                string? folderPath = System.IO.Path.GetDirectoryName(filePath);
                string? fileName = System.IO.Path.GetFileName(filePath);
                Windows.Storage.StorageFolder storageFolder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(folderPath);
                Windows.Storage.StorageFile file = await storageFolder.CreateFileAsync(fileName, Windows.Storage.CreationCollisionOption.ReplaceExisting);
                using (Windows.Storage.Streams.IRandomAccessStream stream = await file.OpenAsync(Windows.Storage.FileAccessMode.ReadWrite))
                {
                    using (DataWriter writer = new DataWriter(stream))
                    {
                        writer.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                        writer.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;
                        writer.WriteDateTime(dt);
                        await writer.StoreAsync();
                    }
                }
                return true;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ERROR] WriteBytesUsingDataWriterAsync: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Testing <see cref="DateTimeOffset"/> read via the <see cref="DataReader"/>
    /// </summary>
    public static async Task<DateTimeOffset> ReadDTOUsingDataReaderAsync(string filePath)
    {
        if (File.Exists(filePath))
        {
            List<byte> bytes = new List<byte>();
            DateTimeOffset result = DateTimeOffset.MinValue; ;
            var buff = await FileIO.ReadBufferAsync(await StorageFile.GetFileFromPathAsync(filePath));
            Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(filePath);
            using (Windows.Storage.Streams.IRandomAccessStream stream = await file.OpenAsync(Windows.Storage.FileAccessMode.Read))
            {
                using (DataReader reader = new DataReader(stream))
                {
                    // The encoding and byte order need to match the settings of the writer previously used.
                    reader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                    reader.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;
                    reader.InputStreamOptions = InputStreamOptions.None;
                        

                    uint dtoSize = 0;

                    // Type 'System.DateTimeOffset' cannot be marshaled as an unmanaged structure.
                    //dtoSize = Marshal.SizeOf<DateTimeOffset>(result);

                    // NOTE: The sizeof(DateTimeOffset) will typically return 16, which does work.
                    //       But it should be 11 (once written to disk), so use the stream size.

                    //unsafe { dtoSize = sizeof(DateTimeOffset); }
                    //await reader.LoadAsync((uint)dtoSize);

                    //result = reader.ReadDateTime();


                    dtoSize = (uint)stream.Size;
                    await reader.LoadAsync(dtoSize);
                    result = reader.ReadDateTime();


                    Debug.WriteLine($"[DateTimeOffset] {result}");
                }
            }
            return result;
        }
        return DateTimeOffset.MinValue;
    }

    /// <summary>
    /// Here we will parse the strings contained in the ElementsToWrite text block, 
    /// write them to a stream using DataWriter, retrieve them using DataReader, and 
    /// output the results in the ElementsRead text block.
    /// </summary>
    /// <param name="ElementsToWrite">comma separated values</param>
    /// <returns>ElementsRead</returns>
    public static async Task<string?> TransferDataInMemoryStream(string ElementsToWrite, string separator = ";")
    {
        string results = string.Empty;

        // Initialize the in-memory stream where data will be stored.
        using (var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream())
        {
            // Create the data writer object backed by the in-memory stream.
            using (var dataWriter = new Windows.Storage.Streams.DataWriter(stream))
            {
                dataWriter.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                dataWriter.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;

                // Parse the input stream and write each element separately.
                string[] inputElements = ElementsToWrite.Split(separator);
                foreach (string inputElement in inputElements)
                {
                    uint inputElementSize = dataWriter.MeasureString(inputElement);
                    dataWriter.WriteUInt32(inputElementSize);
                    dataWriter.WriteString(inputElement);
                }

                // Send the contents of the writer to the backing stream.
                await dataWriter.StoreAsync();

                // For the in-memory stream implementation we are using, the flushAsync call 
                // is superfluous,but other types of streams may require it.
                await dataWriter.FlushAsync();

                // In order to prolong the lifetime of the stream, detach it from the 
                // DataWriter so that it will not be closed when Dispose() is called on 
                // dataWriter. Were we to fail to detach the stream, the call to 
                // dataWriter.Dispose() would close the underlying stream, preventing 
                // its subsequent use by the DataReader below.
                dataWriter.DetachStream();
            }

            // Create the input stream at position 0 so that the stream can be read from the beginning.
            using (var inputStream = stream.GetInputStreamAt(0))
            {
                using (var dataReader = new Windows.Storage.Streams.DataReader(inputStream))
                {
                    // The encoding and byte order need to match the settings of the writer previously used.
                    dataReader.UnicodeEncoding = Windows.Storage.Streams.UnicodeEncoding.Utf8;
                    dataReader.ByteOrder = Windows.Storage.Streams.ByteOrder.LittleEndian;
                    //dataReader.InputStreamOptions = InputStreamOptions.None;

                    // Once we have written the contents successfully we load the stream.
                    await dataReader.LoadAsync((uint)stream.Size);

                    var receivedStrings = "";

                    // Keep reading until we consume the complete stream.
                    while (dataReader.UnconsumedBufferLength > 0)
                    {
                        // Note that the call to readString requires a length of "code units" to read.
                        // This is the reason each string is preceded by its length when "on the wire".
                        uint bytesToRead = dataReader.ReadUInt32();
                        receivedStrings += dataReader.ReadString(bytesToRead) + separator;
                    }

                    // Populate the ElementsRead block with the items we read from the stream.
                    results = receivedStrings.RemoveLastSeparator(separator);
                }
            }
        }

        return results;
    }
    #endregion

    /// <summary>
    /// You can use the <see cref="StorageFile.CreateStreamedFileAsync(string, StreamedFileDataRequestedHandler, IRandomAccessStreamReference)"/>
    /// method to create a virtual StorageFile. You give it a name, a delegate, and an optional thumbnail. 
    /// When the virtual StorageFile is first accessed, your delegate will be invoked, and its job is to
    /// fill the provided output stream with data.
    /// </summary>
    public static async Task<StorageFile?> CreateStreamedAsync(StorageFile file, byte[] writeOnAccessed, bool addThumbnail = false)
    {
        //byte[] writeOnAccessed = Encoding.UTF8.GetBytes("example file contents");

        IBuffer buffer = writeOnAccessed.AsBuffer();

        try
        {
            RandomAccessStreamReference? thumb = null;
            if (addThumbnail)
            {
                var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem);
                if (thumbnail != null)
                   thumb = RandomAccessStreamReference.CreateFromFile(file);
            }

            // The temp file will normally appear here "C:\Users\AccountName\AppData\Local\Temp\WinUI_Temp.txt"
            return await StorageFile.CreateStreamedFileAsync($"WinUI_Temp.txt",
               async (request) =>
               {
                   using (request)
                   {
                       await request.WriteAsync(buffer);
                   }
               }, thumb);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WARNING] CreateStreamedAsync: {ex.Message}");
        }
        return null;
    }

 
    /// <summary>
    /// Gets the thumbnail associated with the <paramref name="thumbnailFilePath"/> and saves it as 
    /// a PNG file with the name <paramref name="outputName"/> in the app's local execution folder.
    /// </summary>
    public static async Task SaveThumbnailAsPngAsync(string thumbnailFilePath, string outputName)
    {
        var file = await StorageFile.GetFileFromPathAsync(thumbnailFilePath);
        if (file != null)
        {
            await SaveThumbnailAsPngAsync(file, outputName);
        }
        else
        {
            Debug.WriteLine($"[WARNING] File not found '{thumbnailFilePath}'");
        }
    }

    /// <summary>
    /// Gets the thumbnail associated with the <paramref name="file"/> and saves it as 
    /// a PNG file with the name <paramref name="outputName"/> in the app's local execution folder.
    /// </summary>
    public static async Task SaveThumbnailAsPngAsync(StorageFile file, string outputName)
    {
        //var file = await StorageFile.GetFileFromPathAsync(givenStringFilePath);

        // Retrieve the thumbnail image for file
        var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.SingleItem);

        // Make sure the file has a thumbnail associated with it.
        if (thumbnail != null)
        {
            try
            {
                Debug.WriteLine($"[INFO] Thumbnail size will be {thumbnail.OriginalWidth}x{thumbnail.OriginalHeight}");

                #region [Create a decoder for the thumbnail]
                //var stream = RandomAccessStreamReference.CreateFromFile(file);
                //var streamWithContent = await stream.OpenReadAsync();
                var st = thumbnail.AsStreamForRead();
                var decoder = await BitmapDecoder.CreateAsync(st.AsRandomAccessStream());
                var softwareBitmap = await decoder.GetSoftwareBitmapAsync();

                //StorageFile.CreateStreamedFileAsync("thumbnail.png", async (request) =>
                //{
                //    using (var stream = request.AsStreamForWrite())
                //    {
                //        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
                //        encoder.SetSoftwareBitmap(softwareBitmap);
                //        await encoder.FlushAsync();
                //    }
                //}, null);
                #endregion

                try { Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "Thumbnails")); }
                catch (Exception) { }

                #region [Create temp file to save thumbnail]
                StorageFolder folder = await StorageFolder.GetFolderFromPathAsync(Path.Combine(AppContext.BaseDirectory, "Thumbnails")); // don't use "Windows.Storage.ApplicationData.Current.LocalFolder";
                using (var str = await folder.OpenStreamForWriteAsync(outputName, CreationCollisionOption.ReplaceExisting))
                {
                    str.Write(new byte[0], 0, 0);
                }
                StorageFile saveFile = await folder.GetFileAsync(outputName);
                #endregion

                #region [Save the new PNG file]
                //var saveFile = await StorageFile.GetFileFromPathAsync("D:\\ThumbnailOutput.png");
                using (var saveStream = await saveFile.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, saveStream);
                    encoder.SetSoftwareBitmap(softwareBitmap);
                    await encoder.FlushAsync();
                }
                #endregion
            }
            catch (COMException ex) when (ex.ErrorCode == -2003292336)
            {
                Debug.WriteLine($"[WARNING] The error likely occurred during the creation of the BitmapDecoder or the retrieval of the stream.");
                Debug.WriteLine($"[WARNING] The COMException suggests that the file or thumbnail might not be in the expected format, or may be corrupted.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[ERROR] SaveThumbnailAsPngAsync: {ex.Message}");
            }
        }
        else
        {
            Debug.WriteLine($"[INFO] File has no thumbnail associated '{file.Name}'");
        }
    }
    #endregion
}
