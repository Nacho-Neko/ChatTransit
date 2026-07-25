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
using ChatTransit;

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

ChatTransit is a pure protocol translator with **zero project references** — not to `Gateway.Shared` (NATS/Consul/Redis/SemanticKernel dispatch plumbing), not to `OneApi.Common`, not to any raw provider SDK project. It only depends on NuGet packages (`MessagePack`, `Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`), so it never knows anything about the platform it happens to be embedded in — this is also why it can be vendored wholesale into a completely separate repository (see `Meeko.Demux/Common/ChatTransit`) with no source changes.

`StreamingChunkDto` / `StreamingContentType` (`Src/StreamingChunkDto.cs`) and the usage-key constants response decoders/encoders share (`Src/Mapping/ChatTransitUsageKeys.cs`) are ChatTransit's own types, not borrowed from a sibling project. Callers that need to cross into a platform-specific representation (e.g. a NATS dispatch-transport chunk, or a different `StreamingChunkDto` used elsewhere in the host platform) own a small field-by-field mapping at their integration boundary — see `CompatWorker.ToTransitChunk` in OneApi's `CompatProvider`, or `TransitChunkMapper` in `Meeko.Demux`'s `Demux.Gateway`. That mapping is platform plumbing, not protocol translation, so it does not belong in this library.

## License

This project is licensed under the PolyForm Noncommercial License 1.0.0. Commercial use is not permitted without a separate commercial license.
