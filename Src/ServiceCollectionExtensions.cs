using Gateway.Shared.ChatTransit.Abstractions;
using Gateway.Shared.ChatTransit.Errors;
using Gateway.Shared.ChatTransit.Inbound;
using Gateway.Shared.ChatTransit.Outbound;
using Gateway.Shared.ChatTransit.Responses;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gateway.Shared.ChatTransit;

/// <summary>
/// Extension methods to register ChatTransit services into the DI container.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers all ChatTransit decoders, encoders, and the <see cref="ChatTransitRegistry"/>
    /// as singletons.
    /// </summary>
    public static IServiceCollection AddChatTransit(this IServiceCollection services)
    {
        // Inbound decoders (caller protocol → TransitRequest)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRequestDecoder, OpenAiChatInboundDecoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRequestDecoder, OpenAiResponsesInboundDecoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRequestDecoder, AnthropicInboundDecoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRequestDecoder, GeminiInboundDecoder>());

        // Outbound encoders (TransitRequest → native protocol)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRequestEncoder, OpenAiChatOutboundEncoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRequestEncoder, OpenAiResponsesOutboundEncoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRequestEncoder, AnthropicOutboundEncoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IRequestEncoder, GeminiOutboundEncoder>());

        // Response SSE encoders (StreamingChunkDto → SSE strings)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IResponseSseEncoder, OpenAiChatResponseEncoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IResponseSseEncoder, OpenAiResponsesResponseEncoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IResponseSseEncoder, AnthropicResponseEncoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IResponseSseEncoder, GeminiResponseEncoder>());

        // Response collectors (List<StreamingChunkDto> → non-streaming object)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IResponseCollector, OpenAiChatResponseEncoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IResponseCollector, OpenAiResponsesResponseEncoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IResponseCollector, AnthropicResponseEncoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IResponseCollector, GeminiResponseEncoder>());

        // Error encoders (TransitError → native error body / SSE)
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IErrorEncoder, OpenAiErrorEncoder>(
            _ => new OpenAiErrorEncoder(ChatTransitProtocol.OpenAiChat)));
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IErrorEncoder, OpenAiResponsesErrorEncoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IErrorEncoder, AnthropicErrorEncoder>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IErrorEncoder, GeminiErrorEncoder>());

        // Registries as singletons
        services.TryAddSingleton<ChatTransitRegistry>();
        services.TryAddSingleton<ResponseEncoderRegistry>();
        services.TryAddSingleton<ErrorEncoderRegistry>();

        return services;
    }
}
