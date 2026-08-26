using System.Diagnostics;
using System.IO;

namespace YMM4CloudSync.Core.Commons.Utilities;

public static class SentryReporter
{
    private static readonly Lock Gate = new();

    private static SentryClient? _client;

    private static readonly string[] SensitivePaths = BuildSensitivePaths();

    private const string RedactionPlaceholder = "<user>";

    public static bool IsEnabled
    {
        get
        {
            lock (Gate) return _client != null;
        }
    }

    public static void Initialize(string dsn, string release, bool sendDefaultPii)
    {
        if (string.IsNullOrWhiteSpace(dsn)) return;

        lock (Gate)
        {
            if (_client != null) return;

            try
            {
                var options = new SentryOptions
                {
                    Dsn = dsn,
                    Release = release,
                    SendDefaultPii = sendDefaultPii,
                    AutoSessionTracking = false
                };

                options.SetBeforeSend(Scrub);

                _client = new SentryClient(options);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[YMM4CS][Sentry] Initialization failed: {ex.Message}");
            }
        }
    }

    public static SentryId Capture(Exception exception)
    {
        SentryClient? client;
        lock (Gate) client = _client;

        if (client == null)
        {
            Debug.WriteLine($"[YMM4CS][Sentry] Not initialized, dropping: {exception}");
            return SentryId.Empty;
        }

        try
        {
            return client.CaptureException(exception);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YMM4CS][Sentry] Capture failed: {ex.Message}");
            return SentryId.Empty;
        }
    }

    public static void CaptureFeedback(SentryFeedback feedback)
    {
        SentryClient? client;
        lock (Gate) client = _client;

        if (client == null)
        {
            Debug.WriteLine("[YMM4CS][Sentry] Not initialized, dropping feedback.");
            return;
        }

        try
        {
            client.CaptureFeedback(feedback);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YMM4CS][Sentry] Feedback capture failed: {ex.Message}");
        }
    }

    public static void Shutdown()
    {
        SentryClient? client;

        lock (Gate)
        {
            client = _client;
            _client = null;
        }

        try
        {
            client?.Dispose();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YMM4CS][Sentry] Shutdown failed: {ex.Message}");
        }
    }

    internal static SentryEvent Scrub(SentryEvent sentryEvent)
    {
        try
        {
            if (sentryEvent.Message != null)
            {
                sentryEvent.Message.Message = Redact(sentryEvent.Message.Message);
                sentryEvent.Message.Formatted = Redact(sentryEvent.Message.Formatted);
            }

            if (sentryEvent.SentryExceptions != null)
            {
                foreach (var sentryException in sentryEvent.SentryExceptions)
                {
                    sentryException.Value = Redact(sentryException.Value);

                    if (sentryException.Stacktrace?.Frames == null) continue;

                    foreach (var frame in sentryException.Stacktrace.Frames)
                    {
                        frame.FileName = Redact(frame.FileName);
                        frame.AbsolutePath = Redact(frame.AbsolutePath);
                    }
                }
            }

            foreach (var (key, value) in sentryEvent.Extra.ToList())
            {
                if (value is string text)
                    sentryEvent.SetExtra(key, Redact(text));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[YMM4CS][Sentry] Scrubbing failed: {ex.Message}");
        }

        return sentryEvent;
    }

    internal static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        foreach (var sensitivePath in SensitivePaths)
        {
            text = text.Replace(sensitivePath, RedactionPlaceholder, StringComparison.OrdinalIgnoreCase);
        }

        return text;
    }

    private static string[] BuildSensitivePaths()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        if (string.IsNullOrEmpty(profile)) return [];

        profile = profile.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        return
        [
            profile,
            profile.Replace(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        ];
    }
}
