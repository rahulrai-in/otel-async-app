using Azure.Identity;
using Azure.Messaging.ServiceBus;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// Define attributes for your application
var resourceBuilder = ResourceBuilder.CreateDefault()
    // Add attributes for the name and version of the service
    .AddService("MyCompany.AsyncApp.Receiver", serviceVersion: "1.0.0")
    // Add attributes for the OpenTelemetry SDK version
    .AddTelemetrySdk();

// Configure tracing
using var tracerProvider = Sdk.CreateTracerProviderBuilder()
    .SetResourceBuilder(resourceBuilder)
    // Receive traces from our own custom sources
    .AddSource("Receiver")
    // Ensures that all spans are recorded and sent to exporter
    .SetSampler(new AlwaysOnSampler())
    // Stream traces to the SpanExporter
    .AddProcessor(
        new BatchActivityExportProcessor(
            new JaegerExporter(new() { Protocol = JaegerExportProtocol.HttpBinaryThrift })))
    .Build();

var tracer = TracerProvider.Default.GetTracer("Receiver");

await using var client = new ServiceBusClient("asyncservice.servicebus.windows.net", new DefaultAzureCredential());
await using var processor = client.CreateProcessor("messages");

processor.ProcessMessageAsync += ProcessMessageAsync;
processor.ProcessErrorAsync += ProcessErrorAsync;

await processor.StartProcessingAsync();

Console.WriteLine("Press any key to end the processing");
Console.ReadKey();

Console.WriteLine("\nStopping the receiver...");
await processor.StopProcessingAsync();
Console.WriteLine("Stopped receiving messages");

async Task ProcessMessageAsync(ProcessMessageEventArgs args)
{
    // Use the Propagator to extract the context from message properties
    var parentContext = Propagators.DefaultTextMapPropagator.Extract(default, args.Message.ApplicationProperties,
        (qProps, key) =>
        {
            if (!qProps.TryGetValue(key, out var value) || value?.ToString() is null)
            {
                return Enumerable.Empty<string>();
            }

            return new[] { value.ToString() };
        });

    // Create a new span as a child of the propagated parent span
    using var span = tracer.StartActiveSpan("Receive message", SpanKind.Consumer,
        new SpanContext(parentContext.ActivityContext));

    // Copy the parent span baggage to the current span baggage
    Baggage.Current = parentContext.Baggage;

    // Write the baggage properties as span attributes so that they can be recorded by Jaeger
    foreach (var (key, value) in Baggage.Current)
    {
        span.SetAttribute(key, value);
    }

    var body = args.Message.Body.ToString();
    Console.WriteLine($"Received: {body}");
    await args.CompleteMessageAsync(args.Message);

    // Record an event on the span
    span.AddEvent($"Message \"{body}\" received from queue");
}

static Task ProcessErrorAsync(ProcessErrorEventArgs args)
{
    Console.WriteLine(args.Exception.ToString());
    return Task.CompletedTask;
}