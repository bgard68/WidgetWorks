using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using WidgetWorks.Application.Abstractions;
using WidgetWorks.Infrastructure.Email;
using Xunit;

namespace WidgetWorks.UnitTests;

/// <summary>
/// Delivery itself, as opposed to the message shape covered by <see cref="EmailMessageTests"/>.
/// A minimal in-process SMTP listener plays the server, so the real SmtpClient conversation --
/// greeting, EHLO, optional AUTH, DATA -- runs end to end without any network dependency.
/// </summary>
public class EmailDeliveryTests
{
    private static readonly EmailMessage Message =
        new("jane@example.com", "Your order", "<html><body>hi</body></html>", "hi");

    [Fact]
    public async Task Sends_over_a_real_smtp_conversation_without_credentials()
    {
        using var server = new FakeSmtpServer(advertiseAuth: false);
        var sender = new SmtpEmailSender(new EmailOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            UseStartTls = false,
            Username = string.Empty,   // anonymous -> DefaultNetworkCredentials branch
        });

        await sender.SendAsync(Message, CancellationToken.None);

        var transcript = await server.CompletedAsync();
        Assert.Contains("MAIL FROM:<no-reply@widgetworks.demo>", transcript);
        Assert.Contains("RCPT TO:<jane@example.com>", transcript);
        Assert.Contains("Subject:", transcript);
    }

    [Fact]
    public async Task Authenticates_when_a_username_is_configured()
    {
        using var server = new FakeSmtpServer(advertiseAuth: true);
        var sender = new SmtpEmailSender(new EmailOptions
        {
            Host = "127.0.0.1",
            Port = server.Port,
            UseStartTls = false,
            Username = "mailer",
            Password = "hunter2",
        });

        await sender.SendAsync(Message, CancellationToken.None);

        var transcript = await server.CompletedAsync();
        Assert.Contains("AUTH", transcript);
        // LOGIN sends the username base64-encoded on its own line.
        Assert.Contains(Convert.ToBase64String(Encoding.ASCII.GetBytes("mailer")), transcript);
    }

    [Fact]
    public async Task A_dead_server_is_logged_and_rethrown_so_callers_can_decide()
    {
        // Bind-then-close guarantees a port nothing is listening on.
        int deadPort;
        using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            deadPort = ((IPEndPoint)socket.LocalEndPoint!).Port;
        }

        var sender = new SmtpEmailSender(new EmailOptions { Host = "127.0.0.1", Port = deadPort, UseStartTls = false });

        var original = Console.Out;
        var log = new StringWriter();
        Console.SetOut(log);
        try
        {
            await Assert.ThrowsAsync<SmtpException>(() => sender.SendAsync(Message, CancellationToken.None));
        }
        finally
        {
            Console.SetOut(original);
        }

        // The failure trace is the only clue when callers swallow notification errors.
        Assert.Contains("[email] FAILED", log.ToString());
        Assert.Contains("Your order", log.ToString());
    }

    [Fact]
    public async Task The_dev_sender_writes_the_message_to_stdout()
    {
        var original = Console.Out;
        var log = new StringWriter();
        Console.SetOut(log);
        try
        {
            await new DevEmailSender().SendAsync(Message, CancellationToken.None);
        }
        finally
        {
            Console.SetOut(original);
        }

        Assert.Contains("jane@example.com", log.ToString());
        Assert.Contains("Your order", log.ToString());
        Assert.Contains("hi", log.ToString());
    }

    /// <summary>
    /// Just enough SMTP to satisfy System.Net.Mail.SmtpClient over plaintext: greeting, EHLO
    /// capabilities (AUTH LOGIN when asked to), the MAIL/RCPT/DATA exchange, and QUIT.
    /// </summary>
    private sealed class FakeSmtpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly TaskCompletionSource<string> _transcript = new();
        private readonly bool _advertiseAuth;

        public FakeSmtpServer(bool advertiseAuth)
        {
            _advertiseAuth = advertiseAuth;
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _ = ServeAsync();
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public async Task<string> CompletedAsync()
            => await _transcript.Task.WaitAsync(TimeSpan.FromSeconds(10));

        public void Dispose() => _listener.Stop();

        private async Task ServeAsync()
        {
            try
            {
                using var client = await _listener.AcceptTcpClientAsync();
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII);
                using var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\r\n" };
                var received = new StringBuilder();

                await writer.WriteLineAsync("220 fake.local ready");
                string? line;
                while ((line = await reader.ReadLineAsync()) is not null)
                {
                    received.AppendLine(line);
                    var verb = line.Split(' ')[0].ToUpperInvariant();
                    switch (verb)
                    {
                        case "EHLO":
                        case "HELO":
                            if (_advertiseAuth)
                            {
                                await writer.WriteLineAsync("250-fake.local");
                                await writer.WriteLineAsync("250 AUTH LOGIN");
                            }
                            else
                            {
                                await writer.WriteLineAsync("250 fake.local");
                            }

                            break;
                        case "AUTH":
                            // "AUTH LOGIN <base64-user>" (initial response) or bare "AUTH LOGIN".
                            if (line.Split(' ').Length < 3)
                            {
                                await writer.WriteLineAsync("334 VXNlcm5hbWU6");   // "Username:"
                                received.AppendLine(await reader.ReadLineAsync());
                            }

                            await writer.WriteLineAsync("334 UGFzc3dvcmQ6");   // "Password:"
                            received.AppendLine(await reader.ReadLineAsync());
                            await writer.WriteLineAsync("235 ok");
                            break;
                        case "DATA":
                            await writer.WriteLineAsync("354 go ahead");
                            while ((line = await reader.ReadLineAsync()) is not null && line != ".")
                            {
                                received.AppendLine(line);
                            }

                            await writer.WriteLineAsync("250 queued");
                            break;
                        case "QUIT":
                            await writer.WriteLineAsync("221 bye");
                            _transcript.TrySetResult(received.ToString());
                            return;
                        default:
                            await writer.WriteLineAsync("250 ok");
                            break;
                    }
                }

                _transcript.TrySetResult(received.ToString());
            }
            catch (Exception ex)
            {
                _transcript.TrySetException(ex);
            }
        }
    }
}
