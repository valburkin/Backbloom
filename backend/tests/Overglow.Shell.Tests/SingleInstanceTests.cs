namespace Overglow.Shell.Tests;

public sealed class SingleInstanceTests : IDisposable
{
    private readonly string _dir = TestHost.NewDirectory();

    public void Dispose() => TestHost.DeleteDirectory(_dir);

    [Fact]
    public void OneInstancePerDataDirectory()
    {
        using (var first = SingleInstance.TryAcquire(_dir))
        {
            Assert.NotNull(first);
            Assert.Null(SingleInstance.TryAcquire(_dir));

            var other = TestHost.NewDirectory();
            try
            {
                using var second = SingleInstance.TryAcquire(other);
                Assert.NotNull(second);
            }
            finally
            {
                TestHost.DeleteDirectory(other);
            }
        }

        using var again = SingleInstance.TryAcquire(_dir);
        Assert.NotNull(again);
        Assert.True(File.Exists(Path.Combine(_dir, SingleInstance.LockFileName)));
    }

    [Fact]
    public void PipeNames_DependOnTheDirectoryOnly()
    {
        Assert.Equal(SingleInstance.PipeNameFor(_dir), SingleInstance.PipeNameFor(_dir + Path.DirectorySeparatorChar));
        Assert.NotEqual(SingleInstance.PipeNameFor(_dir), SingleInstance.PipeNameFor(_dir + "x"));
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(SingleInstance.PipeNameFor(_dir), SingleInstance.PipeNameFor(_dir.ToUpperInvariant()));
        }
    }

    [Fact]
    public async Task ALaterLaunch_HandsItsRequestToTheRunningInstance()
    {
        using var running = SingleInstance.TryAcquire(_dir)!;
        var requests = new List<InstanceRequest>();
        running.Listen(request =>
        {
            lock (requests)
            {
                requests.Add(request);
            }

            return request == InstanceRequest.Activate ? new InstanceReply(true, new Uri("http://127.0.0.1:18890/")) : new InstanceReply(true);
        });

        var activate = await SingleInstance.SendAsync(_dir, InstanceRequest.Activate, TimeSpan.FromSeconds(5));
        Assert.Equal(new InstanceReply(true, new Uri("http://127.0.0.1:18890/")), activate);
        var quit = await SingleInstance.SendAsync(_dir, InstanceRequest.Quit, TimeSpan.FromSeconds(5));
        Assert.Equal(new InstanceReply(true), quit);
        Assert.Equal([InstanceRequest.Activate, InstanceRequest.Quit], requests);
    }

    [Fact]
    public async Task NoRunningInstance_NoAnswer()
    {
        var reply = await SingleInstance.SendAsync(_dir, InstanceRequest.Activate, TimeSpan.FromMilliseconds(300));
        Assert.Null(reply);
    }

    [Theory]
    [InlineData("ok", true, null)]
    [InlineData("refused", false, null)]
    [InlineData("ok http://127.0.0.1:18890/", true, "http://127.0.0.1:18890/")]
    public void Replies_RoundTrip(string line, bool accepted, string? url)
    {
        var reply = new InstanceReply(accepted, url is null ? null : new Uri(url));
        Assert.Equal(line, SingleInstance.FormatReply(reply));
        Assert.Equal(reply, SingleInstance.ParseReply(line));
        Assert.Null(SingleInstance.ParseReply("garbage"));
    }
}
