using System.Net;

namespace ReceiptCapture.Tests;

/// <summary>An HTTP handler that answers from code, so the update checks can be tested with no network.</summary>
public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _answer;
    public List<Uri> Requests { get; } = new();

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> answer) => _answer = answer;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_answer(request));
    }

    public static HttpResponseMessage Ok(byte[] body) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    public static HttpResponseMessage Ok(string text) => Ok(System.Text.Encoding.UTF8.GetBytes(text));
    public static HttpResponseMessage Status(HttpStatusCode code) => new(code);
}
