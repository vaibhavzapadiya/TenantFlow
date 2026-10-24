using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;

namespace TenantFlow.Api.Tests.Support;

/// <summary>
/// Stands in for Kestrel's request body limit, which <c>TestServer</c> does not enforce. Every request starts with the
/// server-wide limit; routing may then raise it for an endpoint (request size limit metadata), as it does on Kestrel; a body
/// read past the limit throws the same 413 <see cref="BadHttpRequestException"/> Kestrel throws.
/// </summary>
internal static class KestrelBodyLimit
{
    public static void Add(IServiceCollection services, long serverLimit) =>
        services.AddSingleton<IStartupFilter>(new Filter(serverLimit));

    private sealed class Filter(long serverLimit) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                var feature = new SizeFeature { MaxRequestBodySize = serverLimit };
                context.Features.Set<IHttpMaxRequestBodySizeFeature>(feature);
                context.Request.Body = new LimitedStream(context.Request.Body, feature);
                return nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class SizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;

        public long? MaxRequestBodySize { get; set; }
    }

    private sealed class LimitedStream(Stream inner, SizeFeature feature) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Count(await inner.ReadAsync(buffer, cancellationToken));

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Count(int read)
        {
            _read += read;
            return feature.MaxRequestBodySize is { } max && _read > max
                ? throw new BadHttpRequestException("Request body too large.", StatusCodes.Status413PayloadTooLarge)
                : read;
        }
    }
}
