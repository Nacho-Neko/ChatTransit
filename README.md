# ChatTransit

ChatTransit is a .NET 10 library for translating chat completion requests and responses between the wire formats used by OpenAI Chat Completions, OpenAI Responses, Anthropic Messages, and Google Gemini.

The library decodes an inbound provider-specific request into a canonical `TransitRequest`, then encodes it into the target provider protocol. It also contains response collectors, SSE encoders, and protocol-native error encoders for returning results to the original caller.

## Project Layout

- `Src/ChatTransit.csproj` contains the library source.
- `Test/ChatTransit.Tests.csproj` contains xUnit v3 tests, fixtures, decoder coverage, and cross-protocol round-trip tests.
- `ChatTransit.slnx` is the standalone solution for this project.

## Supported Protocols

- `openai.chat`: OpenAI Chat Completions API.
- `openai.responses`: OpenAI Responses API.
- `anthropic`: Anthropic Messages API.
- `gemini`: Google Gemini `generateContent` API.

## Main Components

- `ChatTransitRegistry` resolves request decoder and encoder pairs for caller-format to native-format conversion.
- `IRequestDecoder` implementations parse provider request JSON into `TransitRequest`.
- `IRequestEncoder` implementations serialize `TransitRequest` into backend-native JSON.
- `ResponseEncoderRegistry` resolves streaming SSE encoders and non-streaming response collectors.
- `ErrorEncoderRegistry` resolves protocol-native error body and SSE error encoders.
- `AddChatTransit()` registers all decoders, encoders, collectors, error encoders, and registries with dependency injection.

## Usage

Register ChatTransit services:

```csharp
using Gateway.Shared.ChatTransit;

services.AddChatTransit();
```

Resolve a conversion path:

```csharp
var registry = serviceProvider.GetRequiredService<ChatTransitRegistry>();
var route = registry.Resolve("openai.chat", "anthropic");

if (route is { Decoder: not null, Encoder: not null })
{
    var transitRequest = route.Value.Decoder.Decode(requestBodyBytes, cancellationToken);
    var providerBody = route.Value.Encoder.Encode(transitRequest);
}
```

For same-protocol requests, `Resolve` returns `null` so callers can keep the original payload as a passthrough.

## Build and Test

From this directory:

```powershell
dotnet restore .\ChatTransit.slnx
dotnet build .\ChatTransit.slnx
dotnet test .\Test\ChatTransit.Tests.csproj
```

The source project currently references sibling workspace projects for shared gateway DTOs and raw provider SDK projects. If this folder is moved outside `src/Shared`, update the `ProjectReference` paths in `Src/ChatTransit.csproj`.

## License

This project is licensed under the PolyForm Noncommercial License 1.0.0. Commercial use is not permitted without a separate commercial license.
