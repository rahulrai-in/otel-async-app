using Azure.Identity;
using Azure.Messaging.ServiceBus;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Define attributes for your application
var resourceBuilder = ResourceBuilder.CreateDefault()
    // add attributes for the name and version of the service
    .AddService("MyCompany.AsyncApp.Receiver", serviceVersion: "1.0.0")
    // add attributes for the OpenTelemetry SDK version
    .AddTelemetrySdk();

// Configure tracing
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resourceBuilder)
    // receive traces from our own custom sources
    .AddSource("Receiver")
    // ensures that all spans are recorded and sent to exporter
    .SetSampler(new AlwaysOnSampler())
    // stream traces to the SpanExporter
    .AddProcessor(
        new BatchActivityExportProcessor(
            new JaegerExporter(new() { Protocol = JaegerExportProtocol.HttpBinaryThrift })))
    .Build();

var tracer = TracerProvider.Default.GetTracer("Receiver");

await using var client = new ServiceBusClient("<namespace-name>.servicebus.windows.net", new DefaultAzureCredential());
await using var processor = client.CreateProcessor("<queue-name>");

processor.ProcessMessageAsync += ProcessMessageAsync;
processor.ProcessErrorAsync += ProcessErrorAsync;

await processor.StartProcessingAsync().ConfigureAwait(false);

Console.WriteLine("Press any key to end the processing");
Console.ReadKey();

Console.WriteLine("\nStopping the receiver...");
await processor.StopProcessingAsync().ConfigureAwait(false);
Console.WriteLine("Stopped receiving messages");

async Task ProcessMessageAsync(ProcessMessageEventArgs args)
{
    var parentContext = Propagators.DefaultTextMapPropagator.Extract(default, args.Message.ApplicationProperties,
        (qProps, key) =>
        {
            if (!qProps.TryGetValue(key, out var value) || value?.ToString() is null)
            {
                return Enumerable.Empty<string>();
            }

            return new[] { value.ToString() };
        });

    using var span = tracer.StartActiveSpan("Receive message", SpanKind.Consumer,
        new SpanContext(parentContext.ActivityContext));

    Baggage.Current = parentContext.Baggage;

    foreach (var (key, value) in Baggage.Current)
    {
        span.SetAttribute(key, value);
    }

    var body = args.Message.Body.ToString();
    Console.WriteLine($"Received: {body}");
    await args.CompleteMessageAsync(args.Message).ConfigureAwait(false);

    span.AddEvent($"Message \"{body}\" received from queue");
}

static Task ProcessErrorAsync(ProcessErrorEventArgs args)
{
    Console.WriteLine(args.Exception.ToString());
    return Task.CompletedTask;
}