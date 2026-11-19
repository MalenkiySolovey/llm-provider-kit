using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Llm.ProviderKit.Catalog;
using Llm.ProviderKit.Models;

namespace Llm.ProviderKit.Inspection;

public interface ISafeProviderHttpTransport
{
    Task<ProbeHttpResponse> SendAsync(ProviderProbePlan plan, ProviderDescriptor descriptor, ProviderCredential? credential, CancellationToken cancellationToken = default);
}

/// <summary>Bounded GET/HEAD transport with exact-host redirect checks and credential redaction.</summary>
public sealed class SafeProviderHttpTransport : ISafeProviderHttpTransport, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public SafeProviderHttpTransport(HttpClient? httpClient = null)
    {
        if (httpClient is not null)
        {
            _httpClient = httpClient;
            _ownsClient = false;
            return;
        }

        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        };
        _httpClient = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Llm.ProviderKit/0.1.0");
        _ownsClient = true;
    }

    public async Task<ProbeHttpResponse> SendAsync(
        ProviderProbePlan plan,
        ProviderDescriptor descriptor,
        ProviderCredential? credential,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (plan.Method is not ("GET" or "HEAD")) return Failure(0, "Only GET and HEAD requests are allowed.");
        if (!ProviderMatcher.IsHostTrusted(descriptor, plan.TargetUri)) return Failure(0, "Probe target is outside the trusted host allowlist.");

        var options = plan.TransportOptions ?? new ProviderTransportOptions();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.EffectiveTimeout);
        var current = plan.TargetUri;
        for (var redirect = 0; ; redirect++)
        {
            using var request = new HttpRequestMessage(new HttpMethod(plan.Method), current);
            var secret = credential?.RevealForRequest();
            if (!string.IsNullOrWhiteSpace(secret))
            {
                if (plan.AuthScheme.Equals("bearer", StringComparison.OrdinalIgnoreCase))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret.Trim());
                else if (plan.AuthScheme.Equals("x-api-key", StringComparison.OrdinalIgnoreCase))
                    request.Headers.TryAddWithoutValidation("x-api-key", secret.Trim());
            }

            if (plan.Headers is not null)
            {
                foreach (var (name, value) in plan.Headers) request.Headers.TryAddWithoutValidation(name, value);
            }

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Failure(0, "Provider probe timed out.");
            }
            catch (HttpRequestException)
            {
                return Failure(0, "Provider probe connection failed.");
            }
            catch (Exception)
            {
                return Failure(0, "Provider probe failed before receiving a response.");
            }

            using (response)
            {
                if (IsRedirect(response.StatusCode))
                {
                    if (redirect >= options.EffectiveMaxRedirects) return Failure((int)response.StatusCode, "Provider redirected too many times.");
                    var location = response.Headers.Location;
                    if (location is null) return Failure((int)response.StatusCode, "Redirect response is missing its location.");
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    if (!ProviderMatcher.IsHostTrusted(descriptor, next))
                        return Failure((int)response.StatusCode, "Redirect to a host outside the exact trusted host allowlist was blocked.");
                    if (next.Scheme != Uri.UriSchemeHttps && !(next.Scheme == Uri.UriSchemeHttp && next.IsLoopback))
                        return Failure((int)response.StatusCode, "Redirect to an insecure endpoint was blocked.");
                    current = next;
                    continue;
                }

                if (!response.IsSuccessStatusCode) return Failure((int)response.StatusCode, $"Provider returned HTTP {(int)response.StatusCode}.");
                if (plan.Method == "HEAD") return new ProbeHttpResponse(true, null, (int)response.StatusCode, null);
                if (response.Content.Headers.ContentLength > options.EffectiveMaxResponseBytes)
                    return Failure((int)response.StatusCode, "Provider response exceeded the configured size limit.");

                try
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                    using var buffer = new MemoryStream();
                    var chunk = new byte[8192];
                    while (true)
                    {
                        var count = await stream.ReadAsync(chunk, timeout.Token).ConfigureAwait(false);
                        if (count == 0) break;
                        if (buffer.Length + count > options.EffectiveMaxResponseBytes)
                            return Failure((int)response.StatusCode, "Provider response exceeded the configured size limit.");
                        buffer.Write(chunk, 0, count);
                    }

                    using var document = JsonDocument.Parse(buffer.ToArray(), new JsonDocumentOptions { MaxDepth = 64 });
                    return new ProbeHttpResponse(true, document.RootElement.Clone(), (int)response.StatusCode, null);
                }
                catch (JsonException)
                {
                    return Failure((int)response.StatusCode, "Provider returned malformed JSON.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    return Failure((int)response.StatusCode, "Provider probe timed out while reading its response.");
                }
                catch (Exception)
                {
                    return Failure((int)response.StatusCode, "Provider response could not be read.");
                }
            }
        }
    }

    private static bool IsRedirect(HttpStatusCode status) => status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static ProbeHttpResponse Failure(int status, string message) => new(false, null, status, message);

    public void Dispose()
    {
        if (_ownsClient) _httpClient.Dispose();
    }
}
