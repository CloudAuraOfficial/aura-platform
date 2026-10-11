# AI essence generation — end-to-end verification

Scope: WI-6 (issue #7). Flow under test:

Essences page "AI Generate" modal → `POST /api/v1/essences/generate` → `EssencesController.Generate`
→ `AiEssenceBuilderService.GenerateAsync` → per-user provider key (`UserAiKeyService`, set in Settings)
→ `ILlmProvider` (`OpenAiCompatibleLlmProvider` / `AnthropicLlmProvider`) → output parsing and validation
→ `AiGenerationLog` row (usage) → essence JSON returned to the modal → saved by the normal Create path.

The generate endpoint itself does not persist an essence. Only the usage log is written. The
essence is saved when the user clicks Create in the editor.

## Automatic verification

Run with `dotnet test tests/Aura.Tests` (`EssenceGenerationE2ETests`, plus the existing
`AiEssenceBuilderServiceTests`, `OpenAiCompatibleLlmProviderTests` and `AnthropicLlmProviderTests`).

No test calls a real provider and no real key is used. The provider is the real
`OpenAiCompatibleLlmProvider` over a scripted `HttpMessageHandler`. The target URL is
`localhost` and is never contacted.

| Case | Test(s) | What is asserted |
|---|---|---|
| (a) valid prompt → valid, runnable essence | `Valid_prompt_yields_essence_that_passes_project_validation`, `Fenced_markdown_output_is_unwrapped_and_accepted`, `Bad_first_output_is_retried_and_second_valid_output_wins`, `Generation_does_not_persist_an_essence_itself`, `Controller_valid_prompt_returns_200_with_essence` | Output passes the project's own validator (`DeploymentOrchestrationService.ParseAndSortLayers`, via `ValidateEssenceJson`) with the expected layer order. Tokens and iterations are summed. Usage is logged as success. |
| (b) no provider key | `Missing_key_fails_clearly_without_calling_the_provider`, `Controller_missing_key_returns_422_standard_shape`, `Blank_prompt_is_rejected_before_any_provider_call` | Clear message. Provider not called. No usage row. Body is `{error, message, statusCode}`, with no stack trace or exception type name. |
| (c) malformed or invalid output | `Invalid_model_output_is_retried_then_rejected_and_nothing_is_persisted` (9 variants), `Usage_from_earlier_attempts_is_kept_when_a_later_provider_call_fails`, `Controller_malformed_output_returns_422_and_persists_no_essence` | Retried up to 3 times, then rejected. Usage from every attempt is logged with `Success=false`. No `Essence` row is created. Variants: not JSON, non-object JSON, missing `layers`, `layers` as array, no enabled layers, dependency cycle, unknown `runPolicy`. |
| (d) timeout / HTTP error | `Provider_timeout_fails_gracefully_without_retry`, `Provider_http_error_fails_gracefully_with_status_in_message`, `Controller_provider_timeout_returns_422_standard_shape` | Mapped to a failed result, not a crash or 500. Not retried. Provider status surfaces in the message. |
| Controller mapping | `Controller_unknown_provider_returns_400`, `Controller_unknown_cloud_account_returns_400_before_any_provider_call` | 400 with the standard shape. |
| Request validation | `Model_validation_failure_is_reported_in_standard_error_shape`, `Model_validation_failure_without_message_falls_back_to_generic_text` | Framework validation errors use the standard shape rather than `ProblemDetails`. |

Also run: the full unit suite (296 pass, excluding the Docker-dependent Postgres, AWS and GCP
integration suites), `dotnet build` (0 errors), and a syntax check of the inline script in
`Essences.cshtml`.

## Findings and fixes

| # | Area | Finding | Fix |
|---|---|---|---|
| 1 | Service | Validation only checked for a `layers` property. A cyclic, zero-layer, or bad-`runPolicy` essence was returned as valid and only failed later, at run creation. | `ValidateEssenceJson` runs the project's own parser (`ParseAndSortLayers`). Rejected output goes back to the model as a retry hint. |
| 2 | Service | A top-level non-object JSON value (e.g. `[1,2,3]`) threw `InvalidOperationException` from `TryGetProperty`. It escaped the retry loop, skipped the usage log (regression of #21), and surfaced a .NET message to the client. | Object check before property access. Covered by a regression test. |
| 3 | Service | A provider failure threw immediately. Tokens from earlier attempts were never logged. | The loop breaks on provider failure. Usage is logged, then the error is thrown. Covered by a regression test. |
| 4 | Service | A whitespace-only prompt passed `[Required]` and was sent to the model. | Blank-prompt check, with no param name so the message stays clean. |
| 5 | HTTP | `[ApiController]` validation failures returned `ProblemDetails` (no `error`/`message`). The modal treated that as success and showed "Essence generated!" with no JSON. | `InvalidModelStateResponse` gives the standard `{error, message, statusCode}` shape. It is registered globally. |
| 6 | UI | `api()` throws on a non-JSON body (e.g. a gateway error page), and `fetch` rejects on network failure. Neither was caught, so the button stayed on "Generating..." forever. | `try/catch/finally` around the request. The button always re-enables. Shows a clear message. |
| 7 | UI | A success response with no `essenceJson` was shown as success. | Treated as a failure with a message. |
| 8 | UI | No cloud account, or no provider, produced a confusing server-side 400. | Client-side guard with a clear message. |
| 9 | Config | The Anthropic endpoint was a hardcoded constant, unlike OpenRouter. | `ANTHROPIC_BASE_URL` and `OPENAI_BASE_URL` read from env, with the public defaults. Documented in `.env.example`. |

Known gaps, not changed here:

- `operationType` values are not checked against the operation registry at generation time. An
  unknown operation passes generation and fails at run time. The prompt lists the valid types,
  so this is a quality gap, not a crash.
- The UI lists all supported providers, not only those with a configured key. A missing key is
  reported clearly on submit, so this was left as is.
- The HTTP client timeout is 120 seconds, so a hung upstream keeps the modal in "Generating..."
  for up to two minutes before the timeout error appears.

## Manual checklist: live provider key (maintainer, on your own server)

Do this with a real key on your own deployment. Do not paste the key into issues, PRs or logs.

1. [ ] Settings → add a provider key (OpenAI, Anthropic or OpenRouter). Save. Reload and confirm it is stored (masked).
2. [ ] Essences → **AI Generate**. Choose a cloud account and the provider. Enter a prompt such as
       "Deploy a small Ubuntu VM in East US with a resource group and a health check".
   - [ ] Status shows "Generating...", then "Essence generated! Opening editor...".
   - [ ] The editor opens with a name prefixed `AI:`, the JSON, and the stats line (tokens, iterations, model).
3. [ ] Click **Create**. The essence appears in the list.
4. [ ] Open the essence and check the layers match the prompt. Dependencies are ordered correctly.
5. [ ] Create a deployment from the essence and start a run. Confirm the layers are created (no
       "cycle" or "unknown executor" error at run creation). Cancel the run if you do not want to
       deploy anything.
6. [ ] Negative, no key: delete the key in Settings, then generate. The modal shows
       "No API key configured for provider '…'. Add one in Account Settings." and the button is usable again.
7. [ ] Negative, bad key: enter an invalid key, then generate. The modal shows the provider's
       401 message, with no stack trace.
8. [ ] Negative, empty prompt: submit a blank prompt. The modal shows the validation message. No
       provider call is made (no usage row).
9. [ ] Network failure: in browser dev tools, set the network to offline, then generate. The
       modal shows "Could not complete generation…" and the button is re-enabled.
10. [ ] Check usage: the `AiGenerationLog` table has one row per attempt, with `Success` matching
        the outcome and tokens populated.
11. [ ] No secrets in logs: search the API logs for the key value. It must not appear.

Pass criteria: steps 2–5 succeed with a real key. Steps 6–9 show a clear, non-crashing message
and leave the modal usable.
