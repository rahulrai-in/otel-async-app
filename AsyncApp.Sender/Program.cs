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
    // add attributes for the name and version of the service
    .AddService("MyCompany.AsyncApp.Sender", serviceVersion: "1.0.0")
    // add attributes for the OpenTelemetry SDK version
    .AddTelemetrySdk();

// Configure tracing
builder.Services.AddOpenTelemetry()
    .WithTracing(tracerProviderBuilder =>
    {
        tracerProviderBuilder
            // define the resource
            .SetResourceBuilder(resourceBuilder)
            // receive traces from our own custom sources
            .AddSource("Sender")
            // ensures that all spans are recorded and sent to exporter
            .SetSampler(new AlwaysOnSampler())
            // stream traces to the SpanExporter
            .AddProcessor(
                new BatchActivityExportProcessor(new JaegerExporter(new()
                {
                    Protocol = JaegerExportProtocol.HttpBinaryThrift,
                })));
    });

var app = builder.Build();

await using ServiceBusClient client = new("<namespace-name>.servicebus.windows.net", new DefaultAzureCredential());
await using var sender = client.CreateSender("<queue-name>");

app.MapPost("/send", async (string message, TracerProvider tracerProvider) =>
{
    var tracer = tracerProvider.GetTracer("Sender");
    await SendMessageAsync(message, tracer, sender).ConfigureAwait(false);
    return Results.Accepted();
});

await app.RunAsync().ConfigureAwait(false);

static async Task SendMessageAsync(string message, Tracer tracer, ServiceBusSender sender)
{
    using var span = tracer.StartActiveSpan("Send message", SpanKind.Producer);

    // set contextual information which can be read by the receiver
    Baggage.SetBaggage("Sent by", "AsyncApp.Sender");

    var qMessage = new ServiceBusMessage();

    // add trace context to message properties
    Propagators.DefaultTextMapPropagator.Inject(new(span.Context, Baggage.Current), qMessage.ApplicationProperties,
        (qProps, key, value) =>
        {
            qProps ??= new Dictionary<string, object>();
            qProps[key] = value;
        });

    qMessage.Body = BinaryData.FromString(message);
    await sender.SendMessageAsync(qMessage).ConfigureAwait(false);

    span.AddEvent($"Message \"{message}\" sent to queue");
}