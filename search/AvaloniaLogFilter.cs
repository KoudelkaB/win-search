using Avalonia.Logging;

namespace search
{
    /// <summary>
    /// Wraps Avalonia's log sink and drops one known-harmless error.
    /// When a window closes, Avalonia asks IBus to destroy that window's input context through
    /// org.freedesktop.IBus.Service.Destroy. Current IBus (1.5.3x) no longer has that method,
    /// so every closed window logs an UnknownMethod error with a stack trace. IBus frees the
    /// context when the app disconnects anyway, and Avalonia's IME code is internal, so the
    /// call itself cannot be avoided - only its log entry. Any other IME error still gets through.
    /// </summary>
    internal sealed class AvaloniaLogFilter(ILogSink inner) : ILogSink
    {
        public bool IsEnabled(LogEventLevel level, string area) => inner.IsEnabled(level, area);

        public void Log(LogEventLevel level, string area, object source, string messageTemplate)
        {
            if (!IsIbusDestroyNoise(area, messageTemplate))
                inner.Log(level, area, source, messageTemplate);
        }

        public void Log(LogEventLevel level, string area, object source, string messageTemplate,
            params object[] propertyValues)
        {
            if (!IsIbusDestroyNoise(area, messageTemplate))
                inner.Log(level, area, source, messageTemplate, propertyValues);
        }

        internal static bool IsIbusDestroyNoise(string area, string messageTemplate)
            => area == "IME"
               && messageTemplate.StartsWith("Error while destroying the context", System.StringComparison.Ordinal)
               && messageTemplate.Contains("org.freedesktop.DBus.Error.UnknownMethod", System.StringComparison.Ordinal);
    }
}
