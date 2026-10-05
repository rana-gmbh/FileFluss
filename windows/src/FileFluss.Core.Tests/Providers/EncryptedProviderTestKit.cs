// Copyright (C) 2026 Rana GmbH — GPLv3. See LICENSE at the repository root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace FileFluss.Core.Tests.Providers;

/// <summary>Helpers shared by the Mega, Filen and Internxt tests.</summary>
internal static class EncryptedProviderTestKit
{
    public static byte[] Hex(string hex) => Convert.FromHexString(hex);

    public static string ToHex(byte[] bytes) => Convert.ToHexStringLower(bytes);

    /// <summary>Deterministic pseudo-random bytes, so a failing test is reproducible.</summary>
    public static byte[] Bytes(int length, int seed)
    {
        var data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    public static HttpResponseMessage Status(HttpStatusCode status, string body = "")
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Binary(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    /// <summary>A temporary directory removed on dispose.</summary>
    public sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ff-e2ee-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string name, byte[] content)
        {
            var path = System.IO.Path.Combine(Path, name);
            System.IO.File.WriteAllBytes(path, content);
            return path;
        }

        public string Combine(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// Sits in front of <see cref="Support.FakeHttpHandler"/> and keeps every request body as
    /// raw bytes (the fake records bodies as UTF-8 text, which mangles ciphertext). Reading the
    /// body here also runs progress-reporting content exactly once, as the network would.
    /// </summary>
    public sealed class RawCaptureHandler : DelegatingHandler
    {
        public RawCaptureHandler(HttpMessageHandler inner)
            : base(inner)
        {
        }

        public ConcurrentQueue<Captured> Requests { get; } = new();

        public sealed record Captured(HttpMethod Method, Uri Uri, byte[] Body, IReadOnlyDictionary<string, string> Headers);

        public IEnumerable<Captured> Matching(HttpMethod method, string urlFragment)
            => Requests.Where(r => r.Method == method && r.Uri.ToString().Contains(urlFragment, StringComparison.Ordinal));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            byte[] body = [];
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsByteArrayAsync(cancellationToken);
                var replacement = new ByteArrayContent(body);
                foreach (var header in request.Content.Headers)
                {
                    if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                    {
                        replacement.Headers.TryAddWithoutValidation(header.Key, header.Value);
                    }
                }

                request.Content = replacement;
            }

            var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
                .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
            Requests.Enqueue(new Captured(request.Method, request.RequestUri!, body, headers));
            return await base.SendAsync(request, cancellationToken);
        }
    }
}
