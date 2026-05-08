using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace autocad_final.Agent
{
    public sealed class OpenRouterClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _model;
        // ResponseSerializer only — request is now serialized via JsonSupport.Serialize
        // (which uses UseSimpleDictionaryFormat=true) so Dictionary<string,T> comes out as
        // a JSON object rather than the DCJS default key/value array format.
        private static readonly DataContractJsonSerializer ResponseSerializer =
            new DataContractJsonSerializer(typeof(OpenRouterResponse));
        private bool _disposed;

        public OpenRouterClient(string apiKey, string model = "anthropic/claude-sonnet-4-5", string referer = "autocad-final")
        {
            if (string.IsNullOrWhiteSpace(apiKey))
                throw new ArgumentException("OpenRouter API key is required.", nameof(apiKey));

            _model = string.IsNullOrWhiteSpace(model) ? "anthropic/claude-sonnet-4-5" : model;
            // Must exceed per-attempt limit below so long responses (slow free models) are not cut off mid-read.
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            _http.DefaultRequestHeaders.Add("Authorization", "Bearer " + apiKey);
            _http.DefaultRequestHeaders.Add("HTTP-Referer", string.IsNullOrWhiteSpace(referer) ? "autocad-final" : referer);
            _http.DefaultRequestHeaders.Add("X-Title", "autocad-final");
        }

        public async Task<OpenRouterResponse> CompleteAsync(OpenRouterRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            request.Model = _model;

            // Use JsonSupport.Serialize (UseSimpleDictionaryFormat=true) so that
            // Dictionary<string, JsonSchemaObject> serializes as a JSON object, not an array.
            string payload = JsonSupport.Serialize(request);
            const string url = "https://openrouter.ai/api/v1/chat/completions";

            AgentLog.Write("OpenRouter", "POST model=" + _model + " payload=" + payload.Length + " bytes — first 800: " +
                (payload.Length > 800 ? payload.Substring(0, 800) : payload));
            Exception lastError = null;
            // Was 30s — free-tier models often exceed that; treat timeouts as retryable, not user cancel.
            // Per-attempt limit must stay below HttpClient.Timeout.
            const int attemptTimeoutSeconds = 100;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using (var attemptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(attemptTimeoutSeconds)))
                    using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attemptTimeout.Token))
                    using (var content = new StringContent(payload, Encoding.UTF8, "application/json"))
                    using (var response = await _http.PostAsync(url, content, linked.Token).ConfigureAwait(false))
                    {
                        int statusCode = (int)response.StatusCode;
                        var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        AgentLog.Write("OpenRouter", "attempt=" + attempt + " status=" + statusCode +
                            " body=" + (responseBody.Length > 500 ? responseBody.Substring(0, 500) : responseBody));

                        if (statusCode == 429 || statusCode >= 500)
                        {
                            lastError = new InvalidOperationException("OpenRouter returned " + statusCode + ": " + responseBody);
                            await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken).ConfigureAwait(false);
                            continue;
                        }

                        if (statusCode < 200 || statusCode >= 300)
                        {
                            // Non-retryable client error (4xx) — fail immediately with full details
                            throw new InvalidOperationException("OpenRouter HTTP " + statusCode + ": " + responseBody);
                        }

                        return DeserializeResponse(responseBody);
                    }
                }
                catch (OperationCanceledException ex)
                {
                    // User hit Stop → agent token is cancelled. Otherwise this is HTTP/attempt timeout.
                    if (cancellationToken.IsCancellationRequested)
                        throw;

                    AgentLog.Write("OpenRouter",
                        "attempt=" + attempt + " timed out after " + attemptTimeoutSeconds +
                        " s (model may be slow or overloaded). Retrying if attempts remain.");
                    lastError = new InvalidOperationException(
                        "OpenRouter request timed out after " + attemptTimeoutSeconds +
                        " seconds. Free-tier models are often slow; try again or switch to a faster model in Properties.config (OpenRouterModel).",
                        ex);
                    if (attempt < 2)
                        await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    throw; // already has full context, don't retry 4xx
                }
                catch (Exception ex)
                {
                    AgentLog.Write("OpenRouter", "attempt=" + attempt + " exception=" + ex.GetType().Name + ": " + ex.Message);
                    lastError = ex;
                    if (attempt < 2)
                        await Task.Delay(TimeSpan.FromSeconds(attempt + 1), cancellationToken).ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException("OpenRouter call failed after retries.", lastError);
        }

        private static OpenRouterResponse DeserializeResponse(string body)
        {
            if (string.IsNullOrWhiteSpace(body))
                return new OpenRouterResponse { Choices = new List<OpenRouterChoice>() };

            try
            {
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(body)))
                {
                    var parsed = ResponseSerializer.ReadObject(ms) as OpenRouterResponse;
                    if (parsed != null)
                        return parsed;
                }
            }
            catch
            {
                // Fall through to empty response
            }

            return new OpenRouterResponse { Choices = new List<OpenRouterChoice>() };
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _http.Dispose();
        }
    }
}
