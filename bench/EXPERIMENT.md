# Orchestrator concurrency & retry storms (mock provider)

**Question.** Under bursty load, what do bounded concurrency and a queue/worker pool buy over
dispatching everything at once — and can we reproduce, then cure, a retry storm on our own
orchestrator's dispatch shape?

**Setup.** `Aura.Bench` models the worker's real dispatch mechanics — `SemaphoreSlim(N)` per run,
and the production poll → `Take(10)` → `WhenAll(batch)` loop — against a mock provider with
seeded latency (200 ms ± 50%), a small CPU cost per call, transient-failure rate, **ack-loss**
(resource created, caller told "failed") and a **capacity** above which it sheds load. No cloud SDK
is referenced by the project; the multi-cloud teardown path is never touched.
Variants: `unbounded` · `bounded(N)` · `pool(N)` (channel + N workers) · `prod-batch10` (production shape).
Completion latency is measured from enqueue, so queue wait counts. One cell per hour via the
shared dispatcher (28 cells: 4 variants × {10,50,100,500} jobs, plus 12 retry-storm cells).

**First measurements (seed 1, 100 jobs, N=4).**

| variant | wall | P50 | P95 | P99 | peak inflight |
|---|---:|---:|---:|---:|---:|
| unbounded | 0.50 s | 0.31 s | 0.43 s | 0.47 s | 91 |
| bounded(4) | 5.12 s | 2.63 s | 4.83 s | 5.03 s | 4 |
| pool(4) | 5.14 s | 2.63 s | 4.83 s | 5.04 s | 4 |
| prod-batch10 | **6.04 s** | 3.10 s | 5.75 s | 5.94 s | 4 |

- Against an *infinitely patient* provider, unbounded wins outright — bounding only costs latency.
  The case for bounding is the provider's capacity, not ours.
- `prod-batch10` is **~18 % slower than a plain semaphore** at the same N: the `WhenAll` barrier
  makes each batch of 10 wait for its slowest member before the next poll. A streaming pool
  removes the barrier with no other change.

**Retry storm (100 jobs, provider capacity 8, 5 % transient failures, up to 20 attempts).**

| variant | retry | success | provider calls | amplification | P95 |
|---|---|---:|---:|---:|---:|
| unbounded | none | 7 % | 100 | 1.0× | 0.13 s |
| unbounded | naive | 8 % | 1 865 | **18.7×** | 0.44 s |
| unbounded | backoff+jitter | 100 % | 622 | 6.2× | **32 s** |
| bounded(8) | backoff+jitter | 100 % | 103 | **1.03×** | 2.6 s |
| pool(8) | backoff+jitter | 100 % | 103 | 1.03× | 2.6 s |

- Naive immediate retries against a shedding provider are a **spiral**: 18.7× the calls, still 8 %
  success — every retry lands on a provider that is over capacity *because of the retries*.
- Backoff + jitter alone *completes*, but at 6× amplification and a 32 s tail — it survives the
  storm, it doesn't prevent it.
- **The cure is the concurrency cap matched to provider capacity**; backoff is the second line,
  not the first. With both: 1.03× amplification, zero shed, P95 2.6 s.
- **Idempotency:** with 10 % ack-loss, naive retries created **11 duplicate resources per 100 jobs**
  without keys and **0** with keys (`selftest.sh` asserts this on every run). Today the
  orchestrator has no retry on provisioning; adding one without idempotency keys would introduce
  this bug.

**What changes in the product.** (1) Replace the `Take(10)`/`WhenAll` batch with a streaming
pool — same N, no barrier. (2) Any future provisioning retry ships with idempotency keys and sits
*behind* the concurrency cap. (3) Cap N per provider, not globally.

**Caveats.** Mock provider; latencies are relative; CPU cost per call is nominal (2 ms) so the
"knee" here is a capacity/queueing knee, not a CPU one. Multi-seed via the hourly cron.
