using System.Net;
using System.Text;
using Kintsugi.Application.Common.Exceptions;
using Kintsugi.Infrastructure.OutlookSignatures;

namespace Kintsugi.Tests.Infrastructure;

/// <summary>The ID token goes only to the configured https address, and every failure is an
/// <see cref="ExternalServiceException"/> that never carries the token.</summary>
public class OutlookSignatureSourceClientTests
{
    private const string Url = "https://signatures.example.com/bulk";
    private const string Token = "id-token-for-tests";

    private sealed class Tokens : IGoogleIdTokenProvider
    {
        public List<string> Audiences { get; } = new();

        public Task<string> GetAsync(string audience, CancellationToken cancellationToken)
        {
            Audiences.Add(audience);
            return Task.FromResult(Token);
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

        public Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) => _respond = respond;

        public List<HttpRequestMessage> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_respond(request));
        }
    }

    private static (OutlookSignatureSourceClient Client, Handler Handler, Tokens Tokens) Make(HttpStatusCode status, string body, Action<HttpResponseMessage>? tweak = null)
    {
        var handler = new Handler(_ =>
        {
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            tweak?.Invoke(response);
            return response;
        });
        var tokens = new Tokens();
        return (new OutlookSignatureSourceClient(new HttpClient(handler), tokens), handler, tokens);
    }

    [Fact]
    public async Task AnOkBody_IsParsed_AndTheTokenIsSentAsABearer()
    {
        var (client, handler, tokens) = Make(HttpStatusCode.OK,
            """{"status":"ok","signatures":[{"email":"ana@example.com","html":"<p>A</p>","text":"A","renderedAt":"2026-11-18T01:00:00Z"}]}""");

        var response = await client.FetchAsync(Url, CancellationToken.None);

        Assert.Equal("ok", response.Status);
        Assert.Equal("ana@example.com", Assert.Single(response.Signatures!)!.Email);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(new Uri(Url), request.RequestUri);
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
        // The token is minted for the address it is sent to, and nothing else.
        Assert.Equal(new[] { Url }, tokens.Audiences);
    }

    [Fact]
    public async Task ADisabledBody_IsParsed()
    {
        var (client, _, _) = Make(HttpStatusCode.OK, """{"status":"disabled"}""");

        Assert.Equal("disabled", (await client.FetchAsync(Url, CancellationToken.None)).Status);
    }

    /// <summary>No token is minted, and no request made, for an address that is not https.</summary>
    [Theory]
    [InlineData("http://signatures.example.com/bulk")]
    [InlineData("not a url")]
    public async Task ANonHttpsAddress_GetsNoToken(string url)
    {
        var (client, handler, tokens) = Make(HttpStatusCode.OK, "{}");

        await Assert.ThrowsAsync<ExternalServiceException>(() => client.FetchAsync(url, CancellationToken.None));

        Assert.Empty(tokens.Audiences);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "401")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    [InlineData(HttpStatusCode.InternalServerError, "500")]
    [InlineData(HttpStatusCode.Found, "redirect")]
    public async Task ANonSuccess_IsAnErrorNamingTheStatus_WithoutTheToken(HttpStatusCode status, string expected)
    {
        var (client, handler, _) = Make(status, "{}", r => r.Headers.Location = new Uri("https://elsewhere.example.net/"));

        var error = await Assert.ThrowsAsync<ExternalServiceException>(() => client.FetchAsync(Url, CancellationToken.None));

        Assert.Contains(expected, error.Message);
        Assert.DoesNotContain(Token, error.Message);
        // A redirect is reported, never followed.
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("not json at all <p>ana@example.com</p>")]
    [InlineData("""{"status":"ok","signatures":"ana@example.com"}""")]
    public async Task AMalformedBody_IsAnError_ThatDoesNotQuoteIt(string body)
    {
        var (client, _, _) = Make(HttpStatusCode.OK, body);

        var error = await Assert.ThrowsAsync<ExternalServiceException>(() => client.FetchAsync(Url, CancellationToken.None));

        Assert.DoesNotContain("@", error.Message);
    }

    /// <summary>Headers arrive, then the body never finishes: the pull must end as an error, not hang.</summary>
    [Fact]
    public async Task ABodyThatStalls_IsAnError_WithinTheBodyTimeout()
    {
        var handler = new Handler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) });
        var client = new OutlookSignatureSourceClient(new HttpClient(handler), new Tokens()) { BodyTimeout = TimeSpan.FromMilliseconds(200) };

        var fetch = client.FetchAsync(Url, CancellationToken.None);
        var finished = await Task.WhenAny(fetch, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.Same(fetch, finished);
        var error = await Assert.ThrowsAsync<ExternalServiceException>(() => fetch);
        Assert.Contains("did not finish", error.Message);
    }

    /// <summary>A body that never ends: every read waits until it is cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }

    [Fact]
    public async Task ABodyOverTheCap_IsRefused()
    {
        var (client, _, _) = Make(HttpStatusCode.OK, "{}", r => r.Content.Headers.ContentLength = OutlookSignatureSourceClient.MaxResponseBytes + 1);

        var error = await Assert.ThrowsAsync<ExternalServiceException>(() => client.FetchAsync(Url, CancellationToken.None));

        Assert.Contains("MiB", error.Message);
    }
}
