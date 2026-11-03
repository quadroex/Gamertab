using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using Xunit.Abstractions;
using static Microsoft.Playwright.Assertions;

namespace Gamertab.Tests;

public class ConnectivityFactAttribute : FactAttribute
{
    public ConnectivityFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("GAMERTAB_ACTIVITY_BENCHMARK") != "1")
            Skip = "Run dev.ps1 connectivity-test to measure activity transports.";
    }
}

[Trait("Category", "Browser")]
public class ActivityBenchmarkTests(BrowserFixture fixture, ITestOutputHelper output) : IClassFixture<BrowserFixture>
{
    [ConnectivityFact]
    public async Task CompareActivityTransportsWithTheSameIdlePeriodAndEightUpdates()
    {
        var results = new List<object>();
        foreach (var mode in new[] { "websocket", "longpoll", "adaptive", "signalr" })
        {
            await using var owner = await fixture.NewContextAsync();
            var session = (await (await owner.APIRequest.GetAsync("/api/account/session")).JsonAsync())!.Value;
            var headers = new Dictionary<string, string> { ["X-CSRF-TOKEN"] = session.GetProperty("csrfToken").GetString()! };
            Assert.Equal(201, (await owner.APIRequest.PostAsync("/api/account/register", new()
            {
                Headers = headers, DataObject = new { email = $"bench-{Guid.NewGuid():N}@example.test", password = "GamertabTest42", confirmPassword = "GamertabTest42" }
            })).Status);
            session = (await (await owner.APIRequest.GetAsync("/api/account/session")).JsonAsync())!.Value;
            headers["X-CSRF-TOKEN"] = session.GetProperty("csrfToken").GetString()!;
            var created = await owner.APIRequest.PostAsync("/api/library", new() { Headers = headers, DataObject = new { gameId = 1 } });
            var entryId = (await created.JsonAsync())!.Value.GetProperty("id").GetInt32();
            var baseline = (await (await owner.APIRequest.GetAsync("/api/activity")).JsonAsync())!.Value.GetProperty("cursor").GetInt64();
            var page = await owner.NewPageAsync();
            await page.GotoAsync("/Library/Activity");
            await Expect(page.Locator("[data-event-id]")).ToHaveCountAsync(1);
            await page.GetByText("Connection details", new() { Exact = true }).ClickAsync();
            await page.GetByLabel("Update method").SelectOptionAsync(mode == "signalr" ? "websocket" : "signalr");
            await Expect(page.Locator("[data-activity-status]")).ToHaveTextAsync("Live updates connected");
            var sent = new ConcurrentDictionary<string, double>();
            var delays = new ConcurrentDictionary<string, double>();
            var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var clock = new Stopwatch();
            await page.ExposeFunctionAsync("recordActivityArrival", (string id) =>
            {
                if (sent.TryGetValue(id, out var time) && delays.TryAdd(id, clock.Elapsed.TotalMilliseconds - time) && delays.Count == 8)
                    complete.TrySetResult();
            });
            await page.EvaluateAsync("""
                new MutationObserver(records => records.forEach(record => record.addedNodes.forEach(node => {
                    if (node.dataset?.eventId) window.recordActivityArrival(node.dataset.eventId);
                }))).observe(document.querySelector('[data-activity-items]'), { childList: true });
                """);
            var measuring = true;
            var requests = 0;
            var sockets = 0;
            var frames = 0;
            long receivedBytes = 0;
            var pendingBodies = new ConcurrentBag<Task>();
            bool Relevant(string url) => url.Contains("/api/activity") || url.Contains("/hubs/activity");
            page.Request += (_, request) => { if (measuring && Relevant(request.Url)) Interlocked.Increment(ref requests); };
            page.Response += (_, response) =>
            {
                if (!measuring || !Relevant(response.Url)) return;
                pendingBodies.Add(Task.Run(async () =>
                {
                    try { Interlocked.Add(ref receivedBytes, (await response.BodyAsync()).Length); }
                    catch (PlaywrightException) { }
                }));
            };
            page.WebSocket += (_, socket) =>
            {
                if (!Relevant(socket.Url)) return;
                if (measuring) Interlocked.Increment(ref sockets);
                socket.FrameReceived += (_, frame) =>
                {
                    if (!measuring) return;
                    Interlocked.Increment(ref frames);
                    Interlocked.Add(ref receivedBytes, frame.Binary?.Length ?? Encoding.UTF8.GetByteCount(frame.Text ?? ""));
                };
            };
            using var process = Process.GetCurrentProcess();
            var cpuBefore = process.TotalProcessorTime;
            var allocatedBefore = GC.GetTotalAllocatedBytes(true);
            clock.Start();
            await page.GetByLabel("Update method").SelectOptionAsync(mode);
            await Task.Delay(TimeSpan.FromSeconds(12));
            for (var index = 0; index < 8; index++)
            {
                sent[(baseline + index + 1).ToString()] = clock.Elapsed.TotalMilliseconds;
                Assert.Equal(200, (await owner.APIRequest.PutAsync($"/api/library/{entryId}", new()
                {
                    Headers = headers, DataObject = new { gameId = 1, status = index % 2 == 0 ? "Playing" : "Completed" }
                })).Status);
                if (index < 7) await Task.Delay(1000);
            }
            await complete.Task.WaitAsync(TimeSpan.FromSeconds(15));
            clock.Stop();
            var cpuMs = (process.TotalProcessorTime - cpuBefore).TotalMilliseconds;
            var allocatedKiB = (GC.GetTotalAllocatedBytes(true) - allocatedBefore) / 1024;
            measuring = false;
            await Task.WhenAll(pendingBodies.ToArray());
            var result = new { mode, updates = delays.Count, seconds = Math.Round(clock.Elapsed.TotalSeconds, 2), httpRequests = requests,
                webSockets = sockets, receivedFrames = frames, receivedPayloadBytes = receivedBytes,
                averageDelayMs = Math.Round(delays.Values.Average(), 1), maximumDelayMs = Math.Round(delays.Values.Max(), 1),
                serverAndHarnessCpuMs = Math.Round(cpuMs, 1), serverAndHarnessAllocatedKiB = allocatedKiB };
            results.Add(result);
            output.WriteLine(JsonSerializer.Serialize(result));
        }
        var report = new { environment = ".NET 10, local Kestrel with SQLite, headless Edge, server and measuring client share a process",
            scenario = "12 seconds idle, 8 library updates about 1 second apart, receiver connection setup included; payload sizes exclude HTTP/WebSocket framing and TLS", results };
        await File.WriteAllTextAsync(Path.Combine(fixture.ResultsPath, "connectivity.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }
}
