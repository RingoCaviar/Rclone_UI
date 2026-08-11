using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace RcloneUI.Rclone;

public sealed record RcloneDaemonConfiguration(Uri Address, string UserName, string Password)
{
    public static RcloneDaemonConfiguration Create(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        return new(new($"http://127.0.0.1:{port}/"), "host", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    }

    public IReadOnlyList<string> BuildArguments() =>
    [
        "rcd",
        $"--rc-addr={Address.Host}:{Address.Port}",
        $"--rc-user={UserName}",
        $"--rc-pass={Password}",
        "--rc-enable-metrics=false",
        "--log-format=date,time,microseconds,UTC",
    ];

    public HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        if (!Address.IsLoopback) throw new InvalidOperationException("rclone RC must use a loopback address.");
        var client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        client.BaseAddress = Address;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{UserName}:{Password}")));
        return client;
    }
}
