using Azure.Identity;
using Azure.Messaging.ServiceBus;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;
using OpenTelemetry.Exporter;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

// Define attributes for your application
var resourceBuilder = ResourceBuilder.CreateDefault()
    // Add attributes for the name and version of the service
    .AddService("MyCompany.AsyncApp.Sender", serviceVersion: "1.0.0")
    // Add attributes for the OpenTelemetry SDK version
    .AddTelemetrySdk();

// Configure tracing
builder.Services.AddOpenTelemetry()
    .WithTracing(tracerProviderBuilder =>
    {
        tracerProviderBuilder
            // Define the resource
            .SetResourceBuilder(resourceBuilder)
            // Receive traces from our own custom sources
            .AddSource("Sender")
            // Ensures that all spans are recorded and sent to exporter
            .SetSampler(new AlwaysOnSampler())
            // Stream traces to the SpanExporter
            .AddProcessor(
                new BatchActivityExportProcessor(new JaegerExporter(new()
                {
                    Protocol = JaegerExportProtocol.HttpBinaryThrift,
                })));
    });

var app = builder.Build();

await using var client = new ServiceBusClient("asyncservice.servicebus.windows.net", new DefaultAzureCredential());
await using var sender = client.CreateSender("messages");

app.MapPost("/send", async (string message, TracerProvider tracerProvider) =>
{
    // Creates the root span
    var tracer = tracerProvider.GetTracer("Sender");
    await SendMessageAsync(message, tracer, sender);
    return Results.Accepted();
});

await app.RunAsync();

static async Task SendMessageAsync(string message, Tracer tracer, ServiceBusSender sender)
{
    using var span = tracer.StartActiveSpan("Send message", SpanKind.Producer);

    // Set contextual information which can be read by the receiver
    Baggage.SetBaggage("Sent by", "AsyncApp.Sender");

    var qMessage = new ServiceBusMessage();

    // Use the Propagator to add trace context to message properties
    Propagators.DefaultTextMapPropagator.Inject(new(span.Context, Baggage.Current), qMessage.ApplicationProperties,
        (qProps, key, value) =>
        {
            qProps ??= new Dictionary<string, object>();
            qProps[key] = value;
        });

    qMessage.Body = BinaryData.FromString(message);
    await sender.SendMessageAsync(qMessage);

    // Add an event to the span
    span.AddEvent($"Message \"{message}\" sent to queue");
}