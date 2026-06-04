using System.Collections.Concurrent;
using System.Net;

namespace UKBatch.Worker.Tests.Common;

/// <summary>
/// Test double for the heartbeat client's primary handler. Records every POST (URI + raw JSON body)
/// and replies with a scripted status (or throws an <see cref="HttpRequestException"/>) so the
/// <c>WorkerHeartbeatService</c> loop can be exercised deterministically with a
/// <c>FakeTimeProvider</c>. Mirrors the <c>ScriptedHandler</c> pattern in
/// <c>UKBatch.Transport.Http.Tests</c> but oriented for capture-then-assert rather than retry counting.
/// </summary>
internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<int, (HttpStatusCode? Status, bool Throw)> _responder;

    /// <summary>Captured beats in arrival order. Thread-safe (the loop may run on a pool thread).</summary>
    public ConcurrentQueue<CapturedBeat> Beats { get; } = new();

    /// <summary>Total <see cref="SendAsync"/> invocations.</summary>
    public int CallCount => _callCount;

    private int _callCount;

    /// <summary>Always replies 202 Accepted.</summary>
    public RecordingHttpMessageHandler()
        : this(_ => (HttpStatusCode.Accepted, false))
    {
    }

    /// <summary>
    /// Replies per the supplied responder. The argument is the 1-based call index, so a test can
    /// script "first call 500, then 202" or "always throw".
    /// </summary>
    public RecordingHttpMessageHandler(Func<int, (HttpStatusCode? Status, bool Throw)> responder)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var n = Interlocked.Increment(ref _callCount);

        // Buffer the body BEFORE deciding to throw so a captured beat is recorded even on a
        // scripted failure (the production code builds + serializes the payload regardless).
        string body = string.Empty;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }

        Beats.Enqueue(new CapturedBeat(request.RequestUri, body, request.Method.Method));

        var (status, shouldThrow) = _responder(n);
        if (shouldThrow)
        {
            throw new HttpRequestException("scripted transient heartbeat failure");
        }

        return new HttpResponseMessage(status ?? HttpStatusCode.Accepted);
    }

    internal sealed record CapturedBeat(Uri? RequestUri, string Body, string Method);
}
