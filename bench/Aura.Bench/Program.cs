// Aura.Bench — orchestrator concurrency + retry-storm harness against a MOCK provider.
// usage: dotnet run -c Release -- '{"id":"...","variant":"bounded","n":4,"jobs":100,"fail":0.0,"ackloss":0.0,"retry":"none","idem":false,"seed":1}'
// Prints one metrics JSON line (last line of stdout). No real cloud SDK is referenced by this project — by construction.
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

var cell = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(args.Length > 0 ? args[0] : "{}")!;
string S(string k, string d) => cell.TryGetValue(k, out var v) ? v.GetString()! : d;
int I(string k, int d) => cell.TryGetValue(k, out var v) ? v.GetInt32() : d;
double D(string k, double d) => cell.TryGetValue(k, out var v) ? v.GetDouble() : d;
bool B(string k, bool d) => cell.TryGetValue(k, out var v) ? v.GetBoolean() : d;

var variant = S("variant", "bounded"); var n = I("n", 4); var jobs = I("jobs", 100); var seed = I("seed", 1);
var retry = S("retry", "none"); var idem = B("idem", false); var maxAttempts = I("maxAttempts", 5);
var provider = new MockProvider(seed, latencyMs: I("latencyMs", 200), cpuMs: D("cpuMs", 2), failRate: D("fail", 0), ackLoss: D("ackloss", 0), capacity: I("capacity", 0));
var runner = new JobRunner(provider, retry, idem, maxAttempts, seed);

var rng = new Random(seed);
var jobIds = Enumerable.Range(0, jobs).Select(i => $"job-{seed}-{i:D4}").ToArray();
var durations = new double[jobs]; var ok = new bool[jobs];
var sw = Stopwatch.StartNew(); long peakRss = 0;
using var rssTimer = new Timer(_ => { var r = Process.GetCurrentProcess().WorkingSet64; if (r > peakRss) peakRss = r; }, null, 0, 50);

// Completion latency is measured from ENQUEUE (t0), not from dispatch — queue wait is what the user feels.
async Task RunOne(int i) { ok[i] = await runner.RunAsync(jobIds[i]); durations[i] = sw.Elapsed.TotalMilliseconds; }

switch (variant)
{
    case "unbounded":           // every queued job dispatched at once
        await Task.WhenAll(Enumerable.Range(0, jobs).Select(RunOne)); break;
    case "bounded":             // SemaphoreSlim(N) around each job — the worker's per-run gate
    {
        var sem = new SemaphoreSlim(n, n);
        await Task.WhenAll(Enumerable.Range(0, jobs).Select(async i => { await sem.WaitAsync(); try { await RunOne(i); } finally { sem.Release(); } })); break;
    }
    case "pool":                // Channel queue + N long-lived workers (streaming; no batch barrier)
    {
        var ch = Channel.CreateUnbounded<int>();
        foreach (var i in Enumerable.Range(0, jobs)) ch.Writer.TryWrite(i);
        ch.Writer.Complete();
        await Task.WhenAll(Enumerable.Range(0, n).Select(async _ => { await foreach (var i in ch.Reader.ReadAllAsync()) await RunOne(i); })); break;
    }
    case "prod-batch10":        // production shape: poll → Take(10) → WhenAll(batch) under SemaphoreSlim(N) → poll again
    {
        var sem = new SemaphoreSlim(n, n); var next = 0;
        while (next < jobs)
        {
            var batch = Enumerable.Range(next, Math.Min(10, jobs - next)).ToArray(); next += batch.Length;
            await Task.WhenAll(batch.Select(async i => { await sem.WaitAsync(); try { await RunOne(i); } finally { sem.Release(); } }));
        }
        break;
    }
    default: throw new ArgumentException($"unknown variant {variant}");
}
sw.Stop(); rssTimer.Dispose();

Array.Sort(durations);
double P(double q) => durations[Math.Min(durations.Length - 1, (int)Math.Floor(q * (durations.Length - 1)))];
var succeeded = ok.Count(x => x);
var metrics = new Dictionary<string, object?>
{
    ["variant"] = variant, ["n"] = n, ["jobs"] = jobs, ["retry"] = retry, ["idem"] = idem,
    ["wall_ms"] = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
    ["throughput_jps"] = Math.Round(jobs / sw.Elapsed.TotalSeconds, 2),
    ["p50_ms"] = Math.Round(P(0.50), 1), ["p95_ms"] = Math.Round(P(0.95), 1), ["p99_ms"] = Math.Round(P(0.99), 1),
    ["success_rate"] = Math.Round((double)succeeded / jobs, 4),
    ["provider_calls"] = provider.Calls, ["amplification"] = Math.Round((double)provider.Calls / jobs, 3),
    ["double_provisions"] = provider.DoubleProvisions, ["shed"] = provider.Shed, ["peak_rss_mb"] = Math.Round(peakRss / 1e6, 1),
    ["peak_inflight"] = provider.PeakInflight,
};
Console.WriteLine(JsonSerializer.Serialize(metrics));

// ---------------------------------------------------------------- mock provider
sealed class MockProvider
{
    readonly Random _rng; readonly int _latencyMs, _capacity; readonly double _cpuMs, _failRate, _ackLoss;
    public int Shed;
    readonly HashSet<string> _keys = new(); readonly List<string> _created = new(); readonly object _lock = new();
    int _inflight; public int Calls, PeakInflight;
    /// Resources that exist beyond one-per-job — the cost of retrying without idempotency.
    public int DoubleProvisions { get { lock (_lock) return _created.Count - _created.Distinct().Count(); } }
    public MockProvider(int seed, int latencyMs, double cpuMs, double failRate, double ackLoss, int capacity)
    { _rng = new Random(seed * 7919); _latencyMs = latencyMs; _cpuMs = cpuMs; _failRate = failRate; _ackLoss = ackLoss; _capacity = capacity; }

    /// Provision one resource. Throws TransientException on a transient failure. With ackLoss, the
    /// resource IS created but the caller sees a failure (lost acknowledgement) — the classic double-provision trap.
    public async Task ProvisionAsync(string jobId, string? idempotencyKey)
    {
        double fail, ack, jitter; bool shed;
        lock (_lock)
        {
            Calls++; _inflight++; if (_inflight > PeakInflight) PeakInflight = _inflight;
            fail = _rng.NextDouble(); ack = _rng.NextDouble(); jitter = _rng.NextDouble();
            shed = _capacity > 0 && _inflight > _capacity; if (shed) Shed++;   // load-dependent failure: above capacity, fail fast
        }
        try
        {
            if (shed) { await Task.Delay(20); throw new TransientException("capacity exceeded"); }
            var spin = Stopwatch.StartNew(); while (spin.Elapsed.TotalMilliseconds < _cpuMs) { } // serialization/crypto cost, CPU-bound
            await Task.Delay((int)(_latencyMs * (0.5 + jitter)));
            if (fail < _failRate) throw new TransientException("provider busy");
            lock (_lock)
            {
                // With a key, a replay is a no-op. Without one, every successful call creates a resource.
                if (idempotencyKey is null || _keys.Add(idempotencyKey)) _created.Add(jobId);
            }
            if (ack < _ackLoss) throw new TransientException("ack lost");  // created, but caller doesn't know
        }
        finally { lock (_lock) _inflight--; }
    }
}
sealed class TransientException(string m) : Exception(m);

// ---------------------------------------------------------------- retry policies
sealed class JobRunner(MockProvider provider, string retry, bool idem, int maxAttempts, int seed)
{
    readonly Random _rng = new(seed * 104729);
    public async Task<bool> RunAsync(string jobId)
    {
        var key = idem ? jobId : null;
        for (var attempt = 1; ; attempt++)
        {
            try { await provider.ProvisionAsync(jobId, key); return true; }
            catch (TransientException)
            {
                if (retry == "none" || attempt >= maxAttempts) return false;
                if (retry == "backoff")
                {
                    int jitter; lock (_rng) jitter = _rng.Next(0, 100);
                    await Task.Delay(Math.Min(5000, 200 * (1 << (attempt - 1))) + jitter);  // exp backoff, cap 5 s, full jitter slice
                }
                // "naive": immediate retry, no delay
            }
        }
    }
}
