using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Configlue.Testing;
using Microsoft.Extensions.Logging;

namespace Configlue.Tests;

public sealed partial class RuntimeDiagnosticTests
{
    [Test]
    public async Task Tracing_NestsSourceReads_AndCorrelatesValueFreeEvents()
    {
        using var root = new Activity("diagnostic-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        var completed = new ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == ConfiglueTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.TraceId == root.TraceId)
                    completed.Enqueue(activity);
            },
        };
        ActivitySource.AddActivityListener(listener);
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { Label = "private-value" }
        );
        await using var runtime = CreateRuntime([
            new StateSource<AppSettings.Fragment>(
                "store",
                store,
                new StateSourceOptions<AppSettings.Fragment>
                {
                    PhysicalOrigin = "private-path",
                    FixedResourceId = new ResourceId("private-resource"),
                }
            ),
        ]);
        await runtime.GetValueAsync();
        var spans = completed.ToArray();
        spans.Length.ShouldBe(2);
        var read = spans.Single(static span => span.OperationName == "configlue.source.read");
        var resolve = spans.Single(static span => span.OperationName == "configlue.resolve");
        read.ParentSpanId.ShouldBe(resolve.SpanId);
        resolve.ParentSpanId.ShouldBe(root.SpanId);
        read.GetTagItem("configlue.source.id").ShouldBe("store");
        read.GetTagItem("configlue.read.status").ShouldBe("Success");
        // Correlation flows through Activity.Current; value-free events carry no trace identifiers.
        runtime.GetRuntimeSnapshot().LastResolution.ShouldNotBeNull();
        var tags = string.Join("\n", spans.SelectMany(static span => span.TagObjects));
        tags.ShouldNotContain("private-value");
        tags.ShouldNotContain("private-path");
        tags.ShouldNotContain("private-resource");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Metrics_EmitDurations_WithOnlyBoundedLabels(bool sourceReadOnly)
    {
        using var root = new Activity("metrics-test").SetIdFormat(ActivityIdFormat.W3C).Start();
        var measurements =
            new ConcurrentQueue<(
                string Name,
                double Value,
                KeyValuePair<string, object?>[] Tags
            )>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, owner) =>
            {
                if (
                    instrument.Meter.Name == ConfiglueTelemetry.MeterName
                    && (!sourceReadOnly || instrument.Name == "configlue.source.read.duration")
                )
                    owner.EnableMeasurementEvents(instrument);
            },
        };
        listener.SetMeasurementEventCallback<double>(
            (instrument, value, tags, _) =>
            {
                if (Activity.Current?.TraceId == root.TraceId)
                    measurements.Enqueue((instrument.Name, value, tags.ToArray()));
            }
        );
        listener.Start();
        var store = new InMemoryStateSource<AppSettings.Fragment>(
            new AppSettings.Fragment { RetryCount = 3 }
        );
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "unbounded-source-id",
                    store,
                    new StateSourceOptions<AppSettings.Fragment>()
                ),
            ]),
            diagnostics: ConfiglueRuntimeDiagnosticOptions.Disabled
        );
        await runtime.GetValueAsync();
        var observed = measurements.ToArray();
        observed
            .Select(static item => item.Name)
            .ShouldBe(
                sourceReadOnly
                    ? new[] { "configlue.source.read.duration" }
                    : new[] { "configlue.source.read.duration", "configlue.resolve.duration" }
            );
        foreach (var measurement in observed)
        {
            measurement.Value.ShouldBeGreaterThanOrEqualTo(0);
            measurement.Tags.ShouldAllBe(tag =>
                tag.Key == "configlue.result" || tag.Key == "configlue.source.kind"
            );
            measurement
                .Tags.Single(static tag => tag.Key == "configlue.result")
                .Value.ShouldBe("success");
            string.Join("\n", measurement.Tags).ShouldNotContain("unbounded-source-id");
        }
    }

    [Test]
    public async Task Logging_RetainsErrorCategory_WithoutExceptionMessagesOrLocators()
    {
        var logger = new DiagnosticLogger();
        var exception = new InvalidOperationException("private-exception-message");
        var reader = new ThrowingReader(exception);
        await using var runtime = new ConfiglueRuntime<AppSettings, AppSettings.Fragment>(
            new StateSourceSet<AppSettings.Fragment>([
                new StateSource<AppSettings.Fragment>(
                    "remote",
                    reader,
                    new StateSourceOptions<AppSettings.Fragment>
                    {
                        PhysicalOrigin = "private-path",
                        FixedResourceId = new ResourceId("private-resource"),
                    }
                ),
            ]),
            logger: logger,
            diagnostics: ConfiglueRuntimeDiagnosticOptions.Disabled
        );
        var thrown = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await runtime.GetValueAsync()
        );
        thrown.ShouldBeSameAs(exception);
        logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Error
            && entry.Fields.Any(field =>
                field.Key == "ErrorCategory"
                && Equals(field.Value, typeof(InvalidOperationException).FullName)
            )
        );
        logger.Entries.ShouldAllBe(entry => entry.Exception == null);
        var text = string.Join("\n", logger.Entries.SelectMany(static entry => entry.Fields));
        text.ShouldNotContain("private-exception-message");
        text.ShouldNotContain("private-path");
        text.ShouldNotContain("private-resource");
    }

    private sealed class DiagnosticLogger : ILogger
    {
        internal List<(
            LogLevel Level,
            Exception? Exception,
            KeyValuePair<string, object?>[] Fields
        )> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) =>
            Entries.Add(
                (
                    logLevel,
                    exception,
                    ((IEnumerable<KeyValuePair<string, object?>>)state!).ToArray()
                )
            );
    }
}
