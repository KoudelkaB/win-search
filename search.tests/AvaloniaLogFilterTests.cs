using System.Collections.Generic;
using Avalonia.Logging;
using Xunit;

namespace search.Tests
{
    public class AvaloniaLogFilterTests
    {
        sealed class RecordingSink : ILogSink
        {
            public readonly List<string> Messages = new();
            public bool IsEnabled(LogEventLevel level, string area) => true;
            public void Log(LogEventLevel level, string area, object source, string messageTemplate)
                => Messages.Add(messageTemplate);
            public void Log(LogEventLevel level, string area, object source, string messageTemplate,
                params object[] propertyValues)
                => Messages.Add(messageTemplate);
        }

        [Fact]
        public void DropsOnlyTheIbusMissingDestroyError()
        {
            var inner = new RecordingSink();
            var filter = new AvaloniaLogFilter(inner);

            filter.Log(LogEventLevel.Error, "IME", null,
                "Error while destroying the context:\nTmds.DBus.Protocol.DBusErrorReplyException: "
                + "org.freedesktop.DBus.Error.UnknownMethod: Method Destroy is not implemented on interface "
                + "org.freedesktop.IBus.Service");
            filter.Log(LogEventLevel.Error, "IME", null,
                "Error while destroying the context:\nSystem.TimeoutException: no reply");
            filter.Log(LogEventLevel.Error, "IME", null, "Unable to create IME input context:\nboom");
            filter.Log(LogEventLevel.Warning, "Binding", null, "Error while destroying the context: {X}", 1);

            Assert.Equal(3, inner.Messages.Count);
            Assert.DoesNotContain(inner.Messages, m => m.Contains("UnknownMethod"));
        }
    }
}
