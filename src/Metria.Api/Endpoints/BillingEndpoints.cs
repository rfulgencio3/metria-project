using Metria.Api.Auth;
using Metria.Api.Billing;
using Metria.Api.Contracts;
using Metria.Api.Data;
using Metria.Api.Models;
using Metria.Api.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Stripe;
using System.Security.Claims;
using System.Text.Json;
using BillingPortalSessionCreateOptions = Stripe.BillingPortal.SessionCreateOptions;
using BillingPortalSessionService = Stripe.BillingPortal.SessionService;
using CheckoutLineItemOptions = Stripe.Checkout.SessionLineItemOptions;
using CheckoutSession = Stripe.Checkout.Session;
using CheckoutSessionCreateOptions = Stripe.Checkout.SessionCreateOptions;
using CheckoutSessionService = Stripe.Checkout.SessionService;
using DbSubscription = Metria.Api.Models.Subscription;
using StripeSubscription = Stripe.Subscription;
using StripeSubscriptionService = Stripe.SubscriptionService;

namespace Metria.Api.Endpoints;

public static class BillingEndpoints
{
    public static WebApplication MapBillingEndpoints(this WebApplication app)
    {
        const string Tag = "Billing";
        var billing = app.MapGroup("/api/billing").WithTags(Tag);

        // Billing: subscription status (used by frontend paywall)
        billing.MapGet("/subscription", async (ClaimsPrincipal user, [FromServices] AppDbContext db, [FromServices] ISubscriptionService svc, [FromServices] IConfiguration cfg, [FromServices] ILogger<Program> log, [FromServices] IMemoryCache cache) =>
        {
            var email = user.GetEmail();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();
        
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (u is null) return Results.Unauthorized();
        
            var (active, plan, renewsAtUtc) = await svc.GetStatusAsync(u.Id);
            if (!active)
            {
                try
                {
                    log.LogInformation("No active sub in DB for {Email}. Attempting on-demand Stripe sync...", email);
                    var throttleKey = $"sub_sync_recent_{u.Id}";
                    if (cache.TryGetValue(throttleKey, out _))
                    {
                        log.LogInformation("On-demand Stripe sync throttled for user {UserId}", u.Id);
                        goto SkipSync;
                    }
        
                    var custService = new Stripe.CustomerService();
                    var subService = new StripeSubscriptionService();
        
                    var custs = await custService.ListAsync(new Stripe.CustomerListOptions { Email = email, Limit = 10 });
                    var now = DateTime.UtcNow;
                    StripeSubscription? found = null;
                    foreach (var cust in custs.Data?.OrderByDescending(c => c.Created) ?? Enumerable.Empty<Customer>())
                    {
                        var list = await subService.ListAsync(new Stripe.SubscriptionListOptions { Customer = cust.Id, Limit = 10, Status = "all" });
                    var candidate = list.Data?
                            .Where(stripeSubscription => stripeSubscription.Status == "active" || stripeSubscription.Status == "trialing")
                            .OrderByDescending(stripeSubscription => StripeSubscriptionMapping.GetStripeDate(stripeSubscription, "CurrentPeriodEnd") ?? DateTime.MinValue)
                            .FirstOrDefault();
                        if (candidate != null)
                        {
                            found = candidate;
                            break;
                        }
                    }
        
                    if (found == null)
                    {
                        // Fallback: search recent subscriptions (last 24h) and match by customer email
                        try
                        {
                            var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
                            var recent = await subService.ListAsync(new Stripe.SubscriptionListOptions
                            {
                                Created = new DateRangeOptions { GreaterThanOrEqual = cutoff.DateTime },
                                Status = "all",
                                Limit = 50
                            });
                            foreach (var stripeSubscription in recent.Data?.Where(x => x.Status == "active" || x.Status == "trialing") ?? Enumerable.Empty<StripeSubscription>())
                            {
                                if (!string.IsNullOrWhiteSpace(stripeSubscription.CustomerId))
                                {
                                    try
                                    {
                                        var customer = await custService.GetAsync(stripeSubscription.CustomerId);
                                        if (string.Equals(customer.Email, email, StringComparison.OrdinalIgnoreCase))
                                        {
                                            found = stripeSubscription;
                                            break;
                                        }
                                    }
                                    catch { /* ignore */ }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            log.LogWarning(ex, "Failed recent subscriptions search for {Email}", email);
                        }
                    }
        
                    if (found != null)
                    {
                        var price = found.Items?.Data?.FirstOrDefault()?.Price;
                        var priceId = price?.Id;
                        var interval = price?.Recurring?.Interval;
                        var mappedPlan = StripeSubscriptionMapping.MapPlan(cfg, priceId, interval);
                        var mappedStatus = StripeSubscriptionMapping.MapStatus(found.Status);
                        var start = StripeSubscriptionMapping.GetStripeDate(found, "CurrentPeriodStart");
                        var end = StripeSubscriptionMapping.GetStripeDate(found, "CurrentPeriodEnd");
                        if (start != null && end != null)
                        {
                            await svc.UpsertAsync(u.Id, mappedPlan, mappedStatus, start.Value, end.Value, found.CustomerId, found.Id, priceId);
                            (active, plan, renewsAtUtc) = await svc.GetStatusAsync(u.Id);
                            log.LogInformation("On-demand Stripe sync succeeded for {Email}. active={Active}, plan={Plan}", email, active, plan);
                        }
                        else
                        {
                            log.LogWarning("Found Stripe subscription but missing period dates for {Email}", email);
                        }
                    }
                    else
                    {
                        log.LogInformation("No Stripe subscription found by email for {Email}", email);
                    }
        
                    // Throttle subsequent attempts for a short period
                    cache.Set(throttleKey, true, TimeSpan.FromSeconds(20));
                }
                catch (Exception ex)
                {
                    log.LogWarning(ex, "On-demand Stripe sync failed for {Email}", email);
                }
                SkipSync:;
            }
        
            log.LogInformation("GET /api/billing/subscription -> user {Email} ({UserId}) active={Active} plan={Plan} renewsAt={Renews}", email, u.Id, active, plan?.ToString(), renewsAtUtc);
            return Results.Ok(new { active, plan = plan?.ToString().ToLowerInvariant(), renewsAtUtc });
        }).RequireAuthorization();
        
        // Billing: subscriptions history
        billing.MapGet("/subscriptions/history", async (ClaimsPrincipal user, [FromServices] AppDbContext db, [FromServices] ILogger<Program> log) =>
        {
            var email = user.GetEmail();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();
        
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (u is null) return Results.Unauthorized();
        
            var list = await db.Subscriptions.AsNoTracking()
                .Where(s => s.UserId == u.Id)
                .OrderByDescending(s => s.CreatedAtUtc)
                .Select(s => new {
                    provider = s.Provider,
                    plan = s.Plan.ToString().ToLowerInvariant(),
                    status = s.Status.ToString().ToLowerInvariant(),
                    startedAtUtc = s.StartedAtUtc,
                    currentPeriodStartUtc = s.CurrentPeriodStartUtc,
                    currentPeriodEndUtc = s.CurrentPeriodEndUtc,
                    canceledAtUtc = s.CanceledAtUtc,
                    createdAtUtc = s.CreatedAtUtc,
                    updatedAtUtc = s.UpdatedAtUtc
                })
                .ToListAsync();
        
            log.LogInformation("GET /api/billing/subscriptions/history -> user {Email} ({UserId}) items={Count}", email, u.Id, list.Count);
            return Results.Ok(list);
        }).RequireAuthorization();
        
        // Billing: create Checkout Session (Stripe)
        billing.MapPost("/checkout", async (ClaimsPrincipal user, CheckoutReq req, [FromServices] AppDbContext db, [FromServices] IConfiguration cfg, [FromServices] ILogger<Program> log) =>
        {
            var email = user.GetEmail();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();
        
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (u is null) return Results.Unauthorized();
        
            if (string.IsNullOrWhiteSpace(req?.PriceId)) return Results.BadRequest("priceId obrigatório");
        
            var successUrl = string.IsNullOrWhiteSpace(req.SuccessUrl)
                ? ($"{(cfg["FrontendOrigin"] ?? "http://localhost:5173").TrimEnd('/')}/dashboard?checkout=success")
                : req.SuccessUrl;
            var cancelUrl = string.IsNullOrWhiteSpace(req.CancelUrl)
                ? ($"{(cfg["FrontendOrigin"] ?? "http://localhost:5173").TrimEnd('/')}/dashboard?checkout=cancel")
                : req.CancelUrl;
        
            // Tenta vincular a sessão a um Customer do Stripe com o e-mail do usuário
            string? customerId = null;
            try
            {
                customerId = await db.Subscriptions.AsNoTracking()
                    .Where(s => s.UserId == u.Id && s.Provider == "stripe" && s.ProviderCustomerId != null)
                    .OrderByDescending(s => s.UpdatedAtUtc)
                    .Select(s => s.ProviderCustomerId)
                    .FirstOrDefaultAsync();
        
                if (string.IsNullOrWhiteSpace(customerId))
                {
                    var custService = new Stripe.CustomerService();
                    var list = await custService.ListAsync(new Stripe.CustomerListOptions { Email = u.Email, Limit = 1 });
                    var existing = list.Data?.FirstOrDefault();
                    if (existing != null)
                    {
                        customerId = existing.Id;
                    }
                    else
                    {
                        var created = await custService.CreateAsync(new Stripe.CustomerCreateOptions { Email = u.Email });
                        customerId = created?.Id;
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "Failed to resolve/create Stripe customer for {Email}", u.Email);
            }
        
            var options = new CheckoutSessionCreateOptions
            {
                Mode = "subscription",
                SuccessUrl = successUrl,
                CancelUrl = cancelUrl,
                ClientReferenceId = u.Id.ToString(),
                LineItems = new List<CheckoutLineItemOptions>
                {
                    new CheckoutLineItemOptions { Price = req.PriceId, Quantity = 1 }
                }
            };
            if (!string.IsNullOrWhiteSpace(customerId))
            {
                options.Customer = customerId;
            }
            else
            {
                options.CustomerEmail = u.Email;
            }
        
            var service = new CheckoutSessionService();
            log.LogInformation("POST /api/billing/checkout -> user {Email} ({UserId}) price={PriceId} success={Success} cancel={Cancel}", email, u.Id, req.PriceId, successUrl, cancelUrl);
            var session = await service.CreateAsync(options);
            log.LogInformation("Checkout Session created: id={SessionId} url={Url}", session.Id, session.Url);
            return Results.Ok(new { url = session.Url });
        }).RequireAuthorization();
        
        // Billing: Customer Portal
        billing.MapPost("/portal", async (ClaimsPrincipal user, PortalReq req, [FromServices] AppDbContext db, [FromServices] IConfiguration cfg, [FromServices] ILogger<Program> log) =>
        {
            var email = user.GetEmail();
            if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();
        
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (u is null) return Results.Unauthorized();
        
            var sub = await db.Subscriptions.AsNoTracking()
                .Where(s => s.UserId == u.Id)
                .OrderByDescending(s => s.UpdatedAtUtc)
                .FirstOrDefaultAsync();
            var customerId = sub?.ProviderCustomerId;
            if (string.IsNullOrWhiteSpace(customerId)) {
                log.LogWarning("/api/billing/portal -> user {Email} ({UserId}) sem ProviderCustomerId", email, u.Id);
                return Results.BadRequest("Cliente Stripe não encontrado.");
            }
        
            var billingPortal = new BillingPortalSessionService();
            var portalSession = await billingPortal.CreateAsync(new BillingPortalSessionCreateOptions
            {
                Customer = customerId,
                ReturnUrl = string.IsNullOrWhiteSpace(req?.ReturnUrl)
                    ? ($"{(cfg["FrontendOrigin"] ?? "http://localhost:5173").TrimEnd('/')}/dashboard")
                    : req.ReturnUrl
            });
            log.LogInformation("Billing portal created for user {Email} ({UserId}) customer={CustomerId} url={Url}", email, u.Id, customerId, portalSession.Url);
            return Results.Ok(new { url = portalSession.Url });
        }).RequireAuthorization();
        
        // Billing: Stripe Webhook - Enhanced with better error handling and logging
        billing.MapPost("/webhook", async (HttpRequest http, [FromServices] AppDbContext db, [FromServices] IConfiguration cfg, [FromServices] ILogger<Program> log, [FromServices] IMemoryCache cache) =>
        {
            using var reader = new StreamReader(http.Body);
            var json = await reader.ReadToEndAsync();
            var hasSignatureHeader = http.Headers.ContainsKey("Stripe-Signature");
            var signature = http.Headers["Stripe-Signature"].ToString();
            var webhookSecret = Environment.GetEnvironmentVariable("STRIPE_WEBHOOK_SECRET") ?? cfg["Stripe:WebhookSecret"];
            var webhookSecretLength = webhookSecret?.Length ?? 0;
            string? rawEventId = null;
            try
            {
                using var eventDoc = JsonDocument.Parse(json);
                if (eventDoc.RootElement.TryGetProperty("id", out var eventIdEl))
                {
                    rawEventId = eventIdEl.GetString();
                }
            }
            catch
            {
                // Best-effort parse only for observability in signature failures.
            }
            
            if (string.IsNullOrWhiteSpace(webhookSecret)) 
            {
                log.LogError(
                    "Webhook secret not configured. EventId={EventId} SecretLen={SecretLen} HasStripeSignatureHeader={HasStripeSignatureHeader}",
                    rawEventId ?? "unknown",
                    webhookSecretLength,
                    hasSignatureHeader);
                return Results.BadRequest("Webhook secret não configurado");
            }
        
            Event stripeEvent;
            try 
            {
                stripeEvent = EventUtility.ConstructEvent(json, signature, webhookSecret);
            } 
            catch (Exception ex) 
            {
                log.LogError(
                    ex,
                    "Stripe webhook signature validation failed. EventId={EventId} SecretLen={SecretLen} HasStripeSignatureHeader={HasStripeSignatureHeader} HasSignatureValue={HasSignatureValue} PayloadLen={PayloadLen}",
                    rawEventId ?? "unknown",
                    webhookSecretLength,
                    hasSignatureHeader,
                    !string.IsNullOrWhiteSpace(signature),
                    json?.Length ?? 0);
                return Results.BadRequest("Invalid webhook signature");
            }
        
            log.LogInformation("Stripe webhook event received: type={Type} id={Id}", stripeEvent.Type, stripeEvent.Id);
        
            // Simple idempotency check using cache
            var cacheKey = $"webhook_processed_{stripeEvent.Id}";
            if (cache.TryGetValue(cacheKey, out _))
            {
                log.LogInformation("Webhook event {EventId} already processed, skipping", stripeEvent.Id);
                return Results.Ok(new { processed = true, eventId = stripeEvent.Id, cached = true });
            }
        
            try
            {
                // Process webhook based on type
                var success = await ProcessWebhookEvent(stripeEvent, db, cfg, log);
                
                if (success)
                {
                    // Cache successful processing for 24 hours
                    cache.Set(cacheKey, DateTime.UtcNow, TimeSpan.FromHours(24));
                    log.LogInformation("Webhook event {EventId} processed successfully", stripeEvent.Id);
                    return Results.Ok(new { processed = true, eventId = stripeEvent.Id });
                }
                else
                {
                    log.LogWarning("Webhook event {EventId} processing failed", stripeEvent.Id);
                    return Results.StatusCode(500);
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Unexpected error processing webhook event {EventId}", stripeEvent.Id);
                return Results.StatusCode(500);
            }
        });
        
        // Helper method to process webhook events
        async Task<bool> ProcessWebhookEvent(Event stripeEvent, AppDbContext db, IConfiguration cfg, ILogger<Program> log)
        {
            try
            {
                switch (stripeEvent.Type)
                {
                    case "checkout.session.completed":
                        {
                            var session = stripeEvent.Data.Object as CheckoutSession;
                            if (session == null) return false;
        
                            log.LogInformation("Processing checkout.session.completed: sessionId={SessionId} customer={Customer} subscription={Subscription} clientRef={ClientRef}",
                                session.Id, session.Customer, session.Subscription, session.ClientReferenceId);
        
                            // Resolve user
                            Guid userId = Guid.Empty;
                            var clientRef = session.ClientReferenceId;
                            if (!string.IsNullOrWhiteSpace(clientRef))
                            {
                                if (Guid.TryParse(clientRef, out var uid))
                                {
                                    userId = uid;
                                }
                                else
                                {
                                    var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == clientRef);
                                    if (u != null) userId = u.Id;
                                }
                            }
                            if (userId == Guid.Empty)
                            {
                                var email = session.CustomerDetails?.Email ?? session.CustomerEmail;
                                if (!string.IsNullOrWhiteSpace(email))
                                {
                                    var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
                                    if (u != null) userId = u.Id;
                                }
                            }
        
                            if (userId == Guid.Empty)
                            {
                                log.LogWarning("Could not resolve user from checkout session {SessionId}", session.Id);
                                return false;
                            }
        
                            // Process subscription if present
                            if (!string.IsNullOrWhiteSpace(session.SubscriptionId))
                            {
                                var subsService = new StripeSubscriptionService();
                                var sub = await subsService.GetAsync(session.SubscriptionId);
                                await UpsertSubscription(sub, userId, db, cfg, log);
                            }
        
                            return true;
                        }
        
                    case "customer.subscription.created":
                    case "customer.subscription.updated":
                    case "customer.subscription.deleted":
                        {
                            var sub = stripeEvent.Data.Object as StripeSubscription;
                            if (sub == null) return false;
        
                            log.LogInformation("Processing {EventType}: subId={SubId} customer={CustomerId} status={Status}",
                                stripeEvent.Type, sub.Id, sub.CustomerId, sub.Status);
        
                            // Resolve user
                            Guid userId = Guid.Empty;
                            var existingBySub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.ProviderSubscriptionId == sub.Id);
                            if (existingBySub != null) userId = existingBySub.UserId;
        
                            if (userId == Guid.Empty && !string.IsNullOrWhiteSpace(sub.CustomerId))
                            {
                                var existingByCustomer = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.ProviderCustomerId == sub.CustomerId);
                                if (existingByCustomer != null) userId = existingByCustomer.UserId;
                            }
        
                            if (userId == Guid.Empty && !string.IsNullOrWhiteSpace(sub.CustomerId))
                            {
                                try
                                {
                                    var cs = new CustomerService();
                                    var cust = await cs.GetAsync(sub.CustomerId);
                                    var email = cust?.Email;
                                    if (!string.IsNullOrWhiteSpace(email))
                                    {
                                        var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
                                        if (u != null) userId = u.Id;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    log.LogWarning(ex, "Failed to fetch customer {CustomerId} from Stripe", sub.CustomerId);
                                }
                            }
        
                            if (userId == Guid.Empty)
                            {
                                log.LogWarning("Could not resolve user from subscription {SubscriptionId}", sub.Id);
                                return false;
                            }
        
                            await UpsertSubscription(sub, userId, db, cfg, log);
                            return true;
                        }
        
                    default:
                        log.LogInformation("Unhandled webhook event type: {Type}", stripeEvent.Type);
                        return true; // Not an error, just unhandled
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Error processing webhook event {EventId} type {Type}", stripeEvent.Id, stripeEvent.Type);
                return false;
            }
        }
        
        // Helper method to upsert subscription
        async Task UpsertSubscription(StripeSubscription stripeSubscription, Guid userId, AppDbContext db, 
            IConfiguration cfg, ILogger<Program> log)
        {
            var price = stripeSubscription.Items?.Data?.FirstOrDefault()?.Price;
            var priceId = price?.Id;
            var interval = price?.Recurring?.Interval;
            var plan = StripeSubscriptionMapping.MapPlan(cfg, priceId, interval);
            var status = StripeSubscriptionMapping.MapStatus(stripeSubscription.Status);
        
            var start = StripeSubscriptionMapping.GetStripeDate(stripeSubscription, "CurrentPeriodStart");
            var end = StripeSubscriptionMapping.GetStripeDate(stripeSubscription, "CurrentPeriodEnd");
        
            if (start == null || end == null)
            {
                log.LogWarning("Subscription {SubscriptionId} missing current period dates", stripeSubscription.Id);
                return;
            }
        
            // Cancel other active subscriptions for this user
            var activeSubscriptions = await db.Subscriptions
                .Where(s => s.UserId == userId &&
                           (s.Status == SubscriptionStatus.Active || s.Status == SubscriptionStatus.Trialing) &&
                           s.ProviderSubscriptionId != stripeSubscription.Id)
                .ToListAsync();
        
            foreach (var activeSub in activeSubscriptions)
            {
                activeSub.Status = SubscriptionStatus.Canceled;
                activeSub.CanceledAtUtc = DateTime.UtcNow;
                activeSub.UpdatedAtUtc = DateTime.UtcNow;
            }
        
            // Upsert current subscription
            var subscription = await db.Subscriptions
                .FirstOrDefaultAsync(s => s.ProviderSubscriptionId == stripeSubscription.Id);
        
            if (subscription == null)
            {
                subscription = new DbSubscription
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Provider = "stripe",
                    ProviderCustomerId = stripeSubscription.CustomerId,
                    ProviderSubscriptionId = stripeSubscription.Id,
                    ProviderPriceId = priceId,
                    Plan = plan,
                    Status = status,
                    StartedAtUtc = (status == SubscriptionStatus.Active || status == SubscriptionStatus.Trialing) ? DateTime.UtcNow : null,
                    CurrentPeriodStartUtc = start.Value,
                    CurrentPeriodEndUtc = end.Value,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                };
                db.Subscriptions.Add(subscription);
            }
            else
            {
                subscription.UserId = userId;
                subscription.ProviderCustomerId = stripeSubscription.CustomerId;
                subscription.ProviderPriceId = priceId ?? subscription.ProviderPriceId;
                subscription.Plan = plan;
                subscription.Status = status;
                subscription.CurrentPeriodStartUtc = start.Value;
                subscription.CurrentPeriodEndUtc = end.Value;
                subscription.CanceledAtUtc = StripeSubscriptionMapping.GetStripeDate(stripeSubscription, "CanceledAt");
                subscription.UpdatedAtUtc = DateTime.UtcNow;
            }
        
            await db.SaveChangesAsync();
            log.LogInformation("Upserted subscription {SubscriptionId} for user {UserId}", stripeSubscription.Id, userId);
        }
        
        // Billing: Sync manual (reconciliação) — tenta buscar no Stripe e fazer upsert
        billing.MapPost("/sync", async (ClaimsPrincipal user, SyncReq req, [FromServices] AppDbContext db, [FromServices] IConfiguration cfg, [FromServices] ILogger<Program> log) =>
        {
            var emailFromToken = user.GetEmail();
            if (string.IsNullOrWhiteSpace(emailFromToken)) return Results.Unauthorized();
        
            log.LogInformation("/api/billing/sync called by user {Email} with payload: checkoutSessionId={SessionId}, subscriptionId={SubId}, customerId={CustId}, email={Email}",
                emailFromToken, req.CheckoutSessionId, req.SubscriptionId, req.CustomerId, req.Email);
        
            var subService = new StripeSubscriptionService();
            var custService = new Stripe.CustomerService();
            StripeSubscription? sub = null;
            string? emailHintFromCheckout = null;
        
            try
            {
                log.LogInformation("Attempting to find subscription in Stripe...");

                if (!string.IsNullOrWhiteSpace(req.CheckoutSessionId))
                {
                    log.LogInformation("Searching by checkout session ID: {SessionId}", req.CheckoutSessionId);
                    try
                    {
                        var checkoutService = new CheckoutSessionService();
                        var checkoutSession = await checkoutService.GetAsync(req.CheckoutSessionId);

                        emailHintFromCheckout = checkoutSession.CustomerDetails?.Email ?? checkoutSession.CustomerEmail;

                        if (!string.IsNullOrWhiteSpace(checkoutSession.SubscriptionId))
                        {
                            log.LogInformation("Checkout session resolved subscription ID: {SubId}", checkoutSession.SubscriptionId);
                            sub = await subService.GetAsync(checkoutSession.SubscriptionId);
                        }
                        else if (!string.IsNullOrWhiteSpace(checkoutSession.CustomerId))
                        {
                            log.LogInformation("Checkout session has customer ID {CustId} but no subscription ID yet. Searching customer subscriptions...", checkoutSession.CustomerId);
                            var byCustomer = await subService.ListAsync(new Stripe.SubscriptionListOptions { Customer = checkoutSession.CustomerId, Limit = 10, Status = "all" });
                            sub = byCustomer.Data?.FirstOrDefault(stripeSubscription => stripeSubscription.Status == "active" || stripeSubscription.Status == "trialing")
                                ?? byCustomer.Data?.FirstOrDefault();
                        }
                    }
                    catch (Exception ex)
                    {
                        log.LogWarning(ex, "Failed to resolve subscription via checkout session {SessionId}", req.CheckoutSessionId);
                    }
                }

                if (sub == null && !string.IsNullOrWhiteSpace(req.SubscriptionId))
                {
                    log.LogInformation("Searching by subscription ID: {SubId}", req.SubscriptionId);
                    sub = await subService.GetAsync(req.SubscriptionId);
                }

                if (sub == null && !string.IsNullOrWhiteSpace(req.CustomerId))
                {
                    log.LogInformation("Searching by customer ID: {CustId}", req.CustomerId);
                    var list = await subService.ListAsync(new Stripe.SubscriptionListOptions { Customer = req.CustomerId, Limit = 10 });
                    sub = list.Data?.FirstOrDefault(stripeSubscription => stripeSubscription.Status == "active" || stripeSubscription.Status == "trialing") ?? list.Data?.FirstOrDefault();
                    log.LogInformation("Found {Count} subscriptions for customer, selected: {SubId}", list.Data?.Count ?? 0, sub?.Id);
                }

                if (sub == null)
                {
                    var targetEmail = !string.IsNullOrWhiteSpace(req.Email)
                        ? req.Email!.Trim().ToLowerInvariant()
                        : (!string.IsNullOrWhiteSpace(emailHintFromCheckout)
                            ? emailHintFromCheckout.Trim().ToLowerInvariant()
                            : emailFromToken);
                    log.LogInformation("Searching by email: {Email}", targetEmail);
                    
                    // Estratégia 1: Buscar customers por email
                    var custs = await custService.ListAsync(new Stripe.CustomerListOptions { Email = targetEmail, Limit = 10 });
                    log.LogInformation("Found {Count} customers for email", custs.Data?.Count ?? 0);
                    
                    if (custs.Data?.Any() == true)
                    {
                        // Tenta todos os customers encontrados, priorizando o mais recente
                        foreach (var cust in custs.Data.OrderByDescending(c => c.Created))
                        {
                            log.LogInformation("Checking customer: {CustId} (created: {Created})", cust.Id, cust.Created);
                            var list = await subService.ListAsync(new Stripe.SubscriptionListOptions { Customer = cust.Id, Limit = 10 });
                            var candidateSub = list.Data?.FirstOrDefault(stripeSubscription => stripeSubscription.Status == "active" || stripeSubscription.Status == "trialing") ?? list.Data?.FirstOrDefault();
                            
                            if (candidateSub != null)
                            {
                                log.LogInformation("Found subscription {SubId} for customer {CustId}", candidateSub.Id, cust.Id);
                                sub = candidateSub;
                                break;
                            }
                        }
                    }
                    
                    // Estratégia 2: Se não encontrou, buscar subscriptions recentes (últimas 24h) que podem estar sem customer linkado
                    if (sub == null)
                    {
                        log.LogInformation("No subscription found by email, searching recent subscriptions...");
                        var recentCutoff = DateTimeOffset.UtcNow.AddHours(-24);
                        
                        try
                        {
                            var recentSubs = await subService.ListAsync(new Stripe.SubscriptionListOptions 
                            { 
                                Created = new DateRangeOptions { GreaterThanOrEqual = recentCutoff.DateTime },
                                Status = "all",
                                Limit = 50
                            });
                            
                            log.LogInformation("Found {Count} recent subscriptions", recentSubs.Data?.Count ?? 0);
                            
                            // Para cada subscription recente, verifica se o customer tem o email correto
                            foreach (var recentSub in recentSubs.Data?.Where(stripeSubscription => stripeSubscription.Status == "active" || stripeSubscription.Status == "trialing") ?? Enumerable.Empty<StripeSubscription>())
                            {
                                if (!string.IsNullOrWhiteSpace(recentSub.CustomerId))
                                {
                                    try
                                    {
                                        var customer = await custService.GetAsync(recentSub.CustomerId);
                                        if (string.Equals(customer.Email, targetEmail, StringComparison.OrdinalIgnoreCase))
                                        {
                                            log.LogInformation("Found matching subscription {SubId} via recent search for customer {CustId}", recentSub.Id, customer.Id);
                                            sub = recentSub;
                                            break;
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        log.LogWarning(ex, "Failed to get customer {CustId} for recent subscription {SubId}", recentSub.CustomerId, recentSub.Id);
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            log.LogWarning(ex, "Failed to search recent subscriptions");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                log.LogError(ex, "/api/billing/sync failed to fetch subscription from Stripe");
                return Results.BadRequest($"Falha ao consultar Stripe: {ex.Message}");
            }
        
            if (sub == null) 
            {
                log.LogWarning("No subscription found in Stripe for user {Email}", emailFromToken);
                return Results.NotFound("Assinatura não encontrada no Stripe");
            }
        
            log.LogInformation("Found subscription in Stripe: {SubId}, status: {Status}, customer: {CustId}", sub.Id, sub.Status, sub.CustomerId);
        
            // Resolve usuário
            var email = emailFromToken;
            if (!string.IsNullOrWhiteSpace(sub.CustomerId))
            {
                try {
                    var cust = await custService.GetAsync(sub.CustomerId);
                    if (!string.IsNullOrWhiteSpace(cust?.Email)) email = cust.Email;
                    log.LogInformation("Resolved email from customer: {Email}", email);
                } catch (Exception ex) {
                    log.LogWarning(ex, "Failed to get customer details from Stripe");
                }
            }
            var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Email == email);
            if (u is null) 
            {
                log.LogError("User not found in database: {Email}", email);
                return Results.Unauthorized();
            }
        
            log.LogInformation("Resolved user: {UserId} ({Email})", u.Id, u.Email);
        
            var price = sub.Items?.Data?.FirstOrDefault()?.Price;
            var priceId = price?.Id;
            var interval = price?.Recurring?.Interval;
            var plan = StripeSubscriptionMapping.MapPlan(cfg, priceId, interval);
            var status = StripeSubscriptionMapping.MapStatus(sub.Status);
            DateTime? start = StripeSubscriptionMapping.GetStripeDate(sub, "CurrentPeriodStart");
            DateTime? end = StripeSubscriptionMapping.GetStripeDate(sub, "CurrentPeriodEnd");
            
            log.LogInformation("Subscription details: priceId={PriceId}, interval={Interval}, plan={Plan}, status={Status}, period={Start} to {End}", 
                priceId, interval, plan, status, start, end);
            
            if (start == null || end == null) 
            {
                log.LogError("Subscription missing period dates: start={Start}, end={End}", start, end);
                return Results.BadRequest("Assinatura sem período atual");
            }
        
            await UpsertSubscription(sub, u.Id, db, cfg, log);
            log.LogInformation("/api/billing/sync SUCCESS: upserted subId={SubId} userId={UserId} status={Status} plan={Plan}", sub.Id, u.Id, status, plan);
            return Results.Ok(new { ok = true, subId = sub.Id, status = status.ToString().ToLowerInvariant(), plan = plan.ToString().ToLowerInvariant() });
        }).RequireAuthorization();
        
        // Debug endpoint to test Stripe connectivity
        if (app.Environment.IsDevelopment())
        {
            billing.MapGet("/debug", async (ClaimsPrincipal user, [FromServices] ILogger<Program> log) =>
            {
                var email = user.GetEmail();
                if (string.IsNullOrWhiteSpace(email)) return Results.Unauthorized();
        
                try
                {
                    log.LogInformation("Debug: Testing Stripe connectivity for user {Email}", email);
                    
                    var custService = new Stripe.CustomerService();
                    var custs = await custService.ListAsync(new Stripe.CustomerListOptions { Email = email, Limit = 5 });
                    
                    var result = new {
                        email = email,
                        stripeConnected = true,
                        customersFound = custs.Data?.Count ?? 0,
                        customers = custs.Data?.Select(c => new {
                            id = c.Id,
                            email = c.Email,
                            created = c.Created
                        }).ToList()
                    };
                    
                    log.LogInformation("Debug result: {Result}", System.Text.Json.JsonSerializer.Serialize(result));
                    return Results.Ok(result);
                }
                catch (Exception ex)
                {
                    log.LogError(ex, "Debug: Stripe connectivity test failed");
                    return Results.Ok(new {
                        email = email,
                        stripeConnected = false,
                        error = ex.Message
                    });
                }
            }).RequireAuthorization();
        }
        

        return app;
    }
}





