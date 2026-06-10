using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Background;
using SoftMedia.Server.Services.Media.Strategies;
using SoftMedia.Server.Services.Security;
using SoftMedia.Server.Services.Security.ContentRating;
using SoftMedia.Server.Services.Security.LibraryAccess;
using SoftMedia.Server.Hubs;
using SoftMedia.Server.Helpers;

namespace SoftMedia.Server.Extensions;

public static class ServiceCollectionExtensions
{
    /// Name of the policy scheme that routes each request to either JwtBearer or
    /// the ApiToken scheme based on the Authorization header.
    public const string SmartAuthScheme = "SmartAuth";

    /// <summary>
    /// The media/streaming route prefixes where (a) a JWT may travel in the <c>?token=</c> /
    /// <c>?access_token=</c> query string (browsers can't set Authorization on &lt;img&gt;/&lt;video&gt;),
    /// and (b) a reduced-privilege "media" token is accepted. Single source of truth shared by
    /// <c>OnMessageReceived</c> (query-token lift) and <c>OnTokenValidated</c> (media-token scope)
    /// so the two can never drift apart.
    /// </summary>
    internal static bool IsMediaRoute(Microsoft.AspNetCore.Http.PathString path) =>
        path.StartsWithSegments("/api/transcode") ||
        path.StartsWithSegments("/api/v1/stream") ||
        path.StartsWithSegments("/api/v1/audio") ||
        path.StartsWithSegments("/api/v1/books") ||
        path.StartsWithSegments("/api/v1/image") ||
        path.StartsWithSegments("/api/v1/music") ||
        path.StartsWithSegments("/api/v1/trickplay") ||
        path.StartsWithSegments("/api/media") ||
        path.StartsWithSegments("/hubs/media");

    public static IServiceCollection AddIdentityServices(this IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();
        services.AddScoped<IApiTokenService, ApiTokenService>();
        services.AddScoped<ITrustedDeviceService, TrustedDeviceService>();
        services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, ScopeAuthorizationHandler>();
        // Singleton: holds the in-memory pending-2FA-challenge store (P2-WI-005).
        services.AddSingleton<ITotpService, TotpService>();

        services.AddAuthentication(SmartAuthScheme)
            // Policy scheme: opaque "sm_*" bearer tokens go to the ApiToken handler;
            // everything else (JWTs, query-string tokens) goes to JwtBearer. This keeps
            // an opaque API token away from the JWT validator, which would reject it.
            .AddPolicyScheme(SmartAuthScheme, SmartAuthScheme, options =>
            {
                options.ForwardDefaultSelector = ctx =>
                {
                    var auth = ctx.Request.Headers.Authorization.ToString();
                    if (auth.StartsWith("Bearer " + IApiTokenService.Prefix, StringComparison.Ordinal))
                        return ApiTokenAuthenticationHandler.SchemeName;
                    return JwtBearerDefaults.AuthenticationScheme;
                };
            })
            .AddScheme<Microsoft.AspNetCore.Authentication.AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(
                ApiTokenAuthenticationHandler.SchemeName, _ => { })
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = config["JwtSettings:Issuer"],
                    ValidAudience = config["JwtSettings:Audience"],
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["JwtSettings:Secret"]!))
                };
                
                options.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (IsMediaRoute(context.HttpContext.Request.Path))
                        {
                            var token = context.Request.Query["token"];
                            var accessToken = context.Request.Query["access_token"];
                            if (!string.IsNullOrEmpty(token))
                            {
                                context.Token = token;
                            }
                            else if (!string.IsNullOrEmpty(accessToken))
                            {
                                context.Token = accessToken;
                            }
                        }
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        var principal = context.Principal;
                        var tokenUse = principal?.FindFirst(CastTokenClaims.TokenUse)?.Value;

                        // A media token (token_use=media) is a reduced-privilege token the SPA
                        // places in media URLs (audit H3). Accept it ONLY on the media/streaming
                        // routes so a leaked media URL cannot reach other APIs. It already omits
                        // the role claim, so it can never act as admin regardless.
                        if (tokenUse == CastTokenClaims.MediaUse)
                        {
                            if (!IsMediaRoute(context.HttpContext.Request.Path))
                                context.Fail("Media token is restricted to media/streaming routes.");
                            return;
                        }

                        // A cast token (token_use=cast) is a long-lived token the Chromecast
                        // carries in the stream URL. It is hard-scoped to ONE media item's
                        // stream routes: reject it on every other path so a leaked cast URL
                        // can never act as the user elsewhere — even if the user is an admin.
                        if (tokenUse != CastTokenClaims.CastUse)
                            return; // normal access tokens are unaffected

                        var mediaId = principal.FindFirst(CastTokenClaims.CastMedia)?.Value;
                        var path = context.HttpContext.Request.Path;
                        var inScope = !string.IsNullOrEmpty(mediaId) &&
                            (path.StartsWithSegments($"/api/transcode/{mediaId}", StringComparison.OrdinalIgnoreCase) ||
                             path.StartsWithSegments($"/api/v1/stream/{mediaId}", StringComparison.OrdinalIgnoreCase));
                        if (!inScope)
                        {
                            context.Fail("Cast token is scoped to a single media item's stream routes.");
                            return;
                        }

                        // Re-check user state every request so a ban / soft-delete / un-approve
                        // (or admin revocation) takes effect within the token's lifetime — a
                        // stateless JWT is otherwise unrevocable. Mirrors the ApiToken scheme.
                        var sub = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
                                  ?? principal.FindFirst("sub")?.Value;
                        if (!Guid.TryParse(sub, out var userId))
                        {
                            context.Fail("Cast token has no valid subject.");
                            return;
                        }
                        var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                        var ok = await db.Users.AsNoTracking()
                            .AnyAsync(u => u.Id == userId && !u.IsBanned && !u.IsDeleted && u.IsApproved);
                        if (!ok)
                            context.Fail("Cast token user is no longer eligible.");
                    }
                };
            });

        // Authorization: the default policy must accept BOTH schemes (JwtBearer and
        // ApiToken) so existing [Authorize] endpoints work for API tokens too. Scope
        // policies layer on top for endpoints that gate on a specific token scope.
        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(
                    JwtBearerDefaults.AuthenticationScheme, ApiTokenAuthenticationHandler.SchemeName)
                .RequireAuthenticatedUser()
                .Build();
            options.AddScopePolicies();
        });

        return services;
    }

    // Security-layer services. Tiny on purpose — kept separate from AddMediaServices
    // so that the path-jail check has an obvious home (and so the call site in
    // Program.cs reads as "AddSecurityServices()" rather than burying it inside
    // a media DI block).
    public static IServiceCollection AddSecurityServices(this IServiceCollection services)
    {
        services.AddScoped<IStreamSecurityService, StreamSecurityService>();

        // Parental-control filter — IUserContentRatingProvider needs the
        // current HttpContext to read the JWT principal and look the user
        // row up. Repositories inject the provider and call it before
        // building the IQueryable.
        services.AddHttpContextAccessor();
        services.AddScoped<IUserContentRatingProvider, UserContentRatingProvider>();

        // Per-library ACL (Wave C). Mirrors the rating-provider pattern —
        // HttpContext-cached, admin-bypass, fail-open on malformed claim.
        // Repositories inject this and call it before building the IQueryable.
        services.AddScoped<IUserLibraryAccessProvider, UserLibraryAccessProvider>();
        return services;
    }

    public static IServiceCollection AddMediaServices(this IServiceCollection services)
    {
        services.AddHttpClient();

        // Repositories + core media surface (previously registered loose in Program.cs).
        services.AddScoped<ILibraryRepository, LibraryRepository>();
        services.AddScoped<IMediaRepository, MediaRepository>();
        services.AddScoped<IUserMediaInteractionRepository, UserMediaInteractionRepository>();
        services.AddScoped<ILibraryService, LibraryService>();
        services.AddScoped<IMediaService, MediaService>();
        services.AddScoped<ITranscodeDebugService, TranscodeDebugService>();
        services.AddScoped<IVideoPreviewService, VideoPreviewService>();

        // DLNA / UPnP media server (P4-004). Opt-in + LAN-only; see DlnaController.
        services.AddSingleton<Services.Dlna.DlnaServerInfo>();
        services.AddScoped<Services.Dlna.IDlnaContentDirectory, Services.Dlna.DlnaContentDirectory>();
        services.AddHostedService<Services.Dlna.SsdpDiscoveryService>();

        // Scanners
        services.AddScoped<IScannerOrchestrator, ScannerOrchestrator>();
        services.AddScoped<IMediaScanner, MusicScanner>();
        services.AddScoped<IMediaScanner, TvScanner>();
        services.AddScoped<IMediaScanner, MovieScanner>();
        services.AddScoped<IMediaScanner, GameScanner>();
        services.AddScoped<IMediaScanner, BookScanner>();
        services.AddSingleton<IBookMetadataExtractor, BookMetadataExtractor>();

        services.AddTransient<SoftMediaUserAgentHandler>();

        // Named HttpClient used by ImageController's outbound proxy. Carries
        // SoftMediaUserAgentHandler so requests to upstream CDNs (Wikidata,
        // MusicBrainz, Open Library, TVMaze etc.) carry the SDD §4.3-mandated
        // User-Agent. See SDD §6.2 — image-proxy compliance.
        // SECURITY (SSRF): AllowAutoRedirect=false — ImageController allowlists the
        // request host, but a 3xx from an allowlisted host could otherwise be chased to
        // an internal address. The controller follows redirects manually, re-checking the
        // allowlist on each hop. Same guard as the ImageCacheService client below.
        services.AddHttpClient("ImageProxy", c => c.Timeout = TimeSpan.FromSeconds(15))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
                .AddHttpMessageHandler<SoftMediaUserAgentHandler>();

        // Outbound webhook deliveries (P2-WI-004). No SoftMediaUserAgentHandler — the
        // worker sets its own "SoftMedia-Webhooks/1.0" UA per delivery.
        //
        // SECURITY: AllowAutoRedirect=false is load-bearing for the SSRF guard. The
        // worker validates the target's resolved IPs BEFORE sending; if HttpClient
        // transparently followed a 3xx, a benign public URL could redirect to an
        // internal address (169.254.169.254 metadata, 127.0.0.1, RFC1918) and bypass
        // that check. With redirects disabled the worker sees the 3xx and treats it as
        // a blocked delivery rather than chasing the Location.
        services.AddHttpClient("Webhooks", c => c.Timeout = TimeSpan.FromSeconds(15))
                .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
                {
                    AllowAutoRedirect = false,
                    // SECURITY (SSRF, audit M6): connect to the exact IP the worker already
                    // SSRF-validated (carried in the request options), NOT a fresh DNS lookup.
                    // This closes the DNS-rebinding TOCTOU where a hostname validated as public
                    // re-resolves to an internal address at send time. TLS SNI + certificate
                    // validation still use the original hostname, so HTTPS is unaffected.
                    ConnectCallback = async (ctx, ct) =>
                    {
                        var target = ctx.InitialRequestMessage.Options.TryGetValue(
                                Services.Infrastructure.WebhookSecurity.PinnedIpOption, out var pinned)
                            ? pinned
                            : (await Dns.GetHostAddressesAsync(ctx.DnsEndPoint.Host, ct)).FirstOrDefault()
                              ?? throw new InvalidOperationException("No address resolved for webhook host.");

                        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        try
                        {
                            await socket.ConnectAsync(new IPEndPoint(target, ctx.DnsEndPoint.Port), ct);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch
                        {
                            socket.Dispose();
                            throw;
                        }
                    }
                });

        // Metadata Providers
        services.AddHttpClient<WikidataProvider>().AddHttpMessageHandler<SoftMediaUserAgentHandler>();
        services.AddHttpClient<TVMazeProvider>().AddHttpMessageHandler<SoftMediaUserAgentHandler>();
        services.AddHttpClient<OMDbProvider>().AddHttpMessageHandler<SoftMediaUserAgentHandler>();
        services.AddHttpClient<MusicBrainzProvider>().AddHttpMessageHandler<SoftMediaUserAgentHandler>();
        services.AddHttpClient<OpenLibraryProvider>(c => c.Timeout = TimeSpan.FromSeconds(15))
                .AddHttpMessageHandler<SoftMediaUserAgentHandler>();
        services.AddHttpClient<GameMetadataProvider>().AddHttpMessageHandler<SoftMediaUserAgentHandler>();
        services.AddHttpClient<ComicWikidataProvider>().AddHttpMessageHandler<SoftMediaUserAgentHandler>();

        // Wave E2 — OMDb→Wikidata collection bridge. Shares the SDD §4.3
        // User-Agent handler and the existing Wikidata rate-limiter slot so
        // it counts against the same budget as the rest of our Wikidata calls.
        services.AddHttpClient<Services.Metadata.Collections.WikidataCollectionResolver>()
                .AddHttpMessageHandler<SoftMediaUserAgentHandler>();

        services.AddScoped<IMetadataProvider, WikidataProvider>();
        services.AddScoped<IMetadataProvider, TVMazeProvider>();
        services.AddScoped<IMetadataProvider, OMDbProvider>();
        services.AddScoped<IMetadataProvider, MusicBrainzProvider>();
        services.AddScoped<IMetadataProvider, EmbeddedMusicProvider>();
        services.AddScoped<IMetadataProvider, OpenLibraryProvider>();
        services.AddScoped<IMetadataProvider, GameMetadataProvider>();
        services.AddScoped<IMetadataProvider, ExifMetadataProvider>();
        services.AddScoped<IMetadataProvider, ComicInfoXmlProvider>();
        services.AddScoped<IMetadataProvider, ComicWikidataProvider>();

        // Wave D — Kodi/XBMC .nfo sidecar readers (network-free, fallback by default).
        services.AddScoped<IMetadataProvider, Services.Metadata.Nfo.NfoMovieProvider>();
        services.AddScoped<IMetadataProvider, Services.Metadata.Nfo.NfoTvProvider>();
        
        services.AddScoped<EmbeddedMusicProvider>(); 
        services.AddScoped<ITvMetadataEnricher, TvMetadataEnricher>();
        services.AddScoped<IMetadataAggregator, MetadataAggregator>();
        services.AddScoped<IMetadataRouter, MetadataRouter>();

        // Wave E2 — collection enrichment (OMDb→Wikidata bridge).
        services.AddScoped<Services.Metadata.Collections.ICollectionEnrichmentService,
            Services.Metadata.Collections.CollectionEnrichmentService>();

        // Media Analysis Strategies (Strategy Pattern for type-specific analysis)
        services.AddScoped<IMediaAnalysisStrategy, VideoAnalysisStrategy>();
        services.AddScoped<IMediaAnalysisStrategy, AudioAnalysisStrategy>();
        services.AddScoped<IMediaAnalysisService, MediaAnalysisService>();

        // Media & Transcoding Services
        services.AddScoped<IFFmpegService, FFmpegService>();
        services.AddScoped<IStreamPlanService, StreamPlanService>();
        services.AddScoped<IAudioStreamPlanService, AudioStreamPlanService>();
        services.AddSingleton<IProcessController, ProcessController>();
        services.AddSingleton<ITranscodeSessionManager, TranscodeSessionManager>();
        services.AddSingleton<IHlsService, HlsService>();
        services.AddSingleton<TranscodeService>();
        services.AddSingleton<ITranscodeService>(sp => sp.GetRequiredService<TranscodeService>());
        services.AddScoped<ITranscodeSessionService, TranscodeSessionService>();
        services.AddScoped<IStreamResultService, StreamResultService>();
        services.AddSingleton<IBinaryLocationService, BinaryLocationService>();
        services.AddSingleton<IMediaProbeService, MediaProbeService>();

        // Intro / credits cross-episode detection.
        // Extractor and matcher are stateless and reusable as singletons.
        // The orchestrating service is scoped because it consumes AppDbContext.
        services.AddSingleton<Services.Media.Detection.IFingerprintExtractor, Services.Media.Detection.ChromaprintFingerprintExtractor>();
        services.AddSingleton<Services.Media.Detection.ISegmentMatcher, Services.Media.Detection.LongestCommonSegmentMatcher>();
        services.AddScoped<Services.Media.Detection.IIntroCreditsDetectionService, Services.Media.Detection.IntroCreditsDetectionService>();
        services.AddSingleton<IFileSystem, FileSystem>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        
        services.AddScoped<ISubtitleService, SubtitleService>();
        services.AddScoped<ITranscodeProfileBuilder, TranscodeProfileBuilder>();
        services.AddScoped<IHlsManifestService, HlsManifestService>();
        services.AddScoped<IRecommendationService, RecommendationService>();
        services.AddScoped<IMusicImageService, MusicImageService>();

        // Database backup / restore (P1-WI-001).
        services.AddScoped<IBackupService, BackupService>();

        // Trickplay sprite sheets (P2-WI-001).
        services.AddScoped<ITrickplayService, TrickplayService>();
        services.AddSingleton<IThumbnailService, ThumbnailService>();
        services.AddScoped<IMediaRetrievalService, MediaRetrievalService>();
        services.AddScoped<IUserMediaInteractionService, UserMediaInteractionService>();
        services.AddSingleton<IComicArchiveService, ComicArchiveService>();
        services.AddSingleton<IComicPageThumbnailService, ComicPageThumbnailService>();
        services.AddSingleton<IDictionaryService, DictionaryService>();
        
        // System / Infrastructure
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IUserPreferencesService, UserPreferencesService>();
        services.AddSingleton<RateLimiterFactory>();
        
        services.AddSingleton<IMediaNotificationService, MediaNotificationService>();

        // Image Cache Client
        services.AddHttpClient<IImageCacheService, ImageCacheService>((sp, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        // SECURITY (SSRF): like the Webhooks client above, disable transparent redirect
        // following. ImageCacheService allowlists the request host, but a 3xx from an
        // allowlisted host could otherwise be chased to an internal address (cloud
        // metadata, 127.0.0.1, RFC1918). The service follows redirects manually and
        // re-runs the host allowlist on every hop.
        .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
        .AddHttpMessageHandler<SoftMediaUserAgentHandler>()
        .AddHttpMessageHandler(sp => 
        {
            var factory = sp.GetRequiredService<RateLimiterFactory>();
            // Use TVMaze limiter (18/10s) as it's the primary constraint. 
            // Other hosts (MusicBrainz, etc.) also are covered by this conservative limit.
            // Ideally we'd switch limiter based on host, but for now a single shared limiter for the client is safer.
            // Actually, wait: ImageCacheService downloads from MANY hosts.
            // If we use "TVMaze" limiter for *everything* (Wikidata, FanArt, etc.), we might throttle unnecessary requests.
            // However, the "default" limiter in factory is 10/10s, which is even stricter.
            // TVMaze is 18/10s.
            // Getting a limiter based on request URL inside the handler is better, but DelegatingHandler is constructed once per client chain usually?
            // No, AddHttpMessageHandler factory is called when the pipeline is built.
            // But the *instance* of the handler processes multiple requests.
            // The handler needs to be smart enough to pick the limiter, OR we just use a safe global limit.
            // Given the complexity, and that TVMaze is the bulk of traffic, using the TVMaze limiter for *all* image downloads 
            // is a safe starting point (1.8 req/sec is plenty for images if we only have 2 concurrent downloads).
            return new RateLimitingDelegatingHandler(factory.GetLimiter("TVMaze"), sp.GetRequiredService<ILogger<RateLimitingDelegatingHandler>>());
        });

        // Image URL extraction (delegates to IImageDownloadQueue)
        services.AddScoped<IImageUrlExtractorService, ImageUrlExtractorService>();

        // Post-restore artwork repair: re-fetches art whose cache files a DB-only
        // backup didn't include. Scoped (uses AppDbContext); driven by the admin
        // endpoint and the ArtworkRepairOnRestoreService background worker.
        services.AddScoped<IArtworkRepairService, Services.Media.ArtworkRepairService>();

        return services;
    }

    public const string AuthRateLimitPolicy = "auth";
    public const string ImageProxyRateLimitPolicy = "image-proxy";
    public const string TwoFactorRateLimitPolicy = "2fa";

    // Policy "auth": per-IP sliding window on /auth/login and /auth/signup to defeat
    // credential stuffing. 15 attempts per minute is comfortable headroom for users
    // who fat-finger their password a few times while still capping automated attacks.
    // Sliding window (vs fixed) prevents the "near-window-edge double burst": a fixed
    // window of N permits allows 2N attempts in 2 seconds when straddling the boundary.
    //
    // /auth/change-password is intentionally NOT covered by this policy: it requires
    // a valid Bearer token and is one explicit user action — credential stuffing is
    // not the threat model.
    //
    // Policy "image-proxy": per-user fixed window on the image proxy endpoint — limits
    // cache-pollution and outbound-fetch DoS from an authenticated attacker. 120 requests
    // per minute per user is comfortable headroom for page loads (~20 images + retries)
    // while capping pathological abuse. Falls back to IP when the user claim is missing.
    //
    // No global limiter by design — would produce false positives on legitimate
    // range-request streaming traffic.
    public const int AuthPermitLimit = 15;

    public static IServiceCollection AddRateLimitingPolicies(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            // Diagnostic: log every rejection with the partition key and remaining
            // window so we can see WHY a 429 fires. Counts toward catching the
            // "first request 429" bug that surfaced during dev.
            options.OnRejected = (context, _) =>
            {
                var loggerFactory = context.HttpContext.RequestServices
                    .GetRequiredService<ILoggerFactory>();
                var logger = loggerFactory.CreateLogger("RateLimit");
                var ip = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip";
                var path = context.HttpContext.Request.Path;
                var method = context.HttpContext.Request.Method;
                logger.LogWarning(
                    "Rate limit rejected {Method} {Path} from {Ip}. Lease={Lease}",
                    method, path, ip, context.Lease.MetadataNames);
                return ValueTask.CompletedTask;
            };

            options.AddPolicy(AuthRateLimitPolicy, httpContext =>
                RateLimitPartition.GetSlidingWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip",
                    factory: _ => new SlidingWindowRateLimiterOptions
                    {
                        PermitLimit = AuthPermitLimit,
                        Window = TimeSpan.FromMinutes(1),
                        SegmentsPerWindow = 6,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    }));

            options.AddPolicy(ImageProxyRateLimitPolicy, httpContext =>
            {
                var userKey = httpContext.User?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value
                             ?? httpContext.Connection.RemoteIpAddress?.ToString()
                             ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: userKey,
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 120,
                        Window = TimeSpan.FromMinutes(1),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });

            // Policy "2fa" (P2-WI-005): tight cap on TOTP-challenge code submissions to
            // defeat brute-forcing a 6-digit code. Partitioned per challenge id (from the
            // ?challengeId= query) so one user's lockout doesn't affect others; falls back
            // to client IP when absent. 6 attempts / 5 min ≈ negligible odds against 10^6.
            options.AddPolicy(TwoFactorRateLimitPolicy, httpContext =>
            {
                var key = httpContext.Request.Query["challengeId"].ToString();
                if (string.IsNullOrEmpty(key))
                    key = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: "2fa:" + key,
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 6,
                        Window = TimeSpan.FromMinutes(5),
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0,
                        AutoReplenishment = true
                    });
            });
        });
        return services;
    }

    public static IServiceCollection AddBackgroundServices(this IServiceCollection services)
    {
        // Scheduled-task telemetry registry (P1-WI-005). Singleton so it survives
        // across requests; services report their last-run status into it.
        services.AddSingleton<IScheduledTaskRegistry, ScheduledTaskRegistry>();

        // Persists that telemetry to disk so it survives a backend reboot (loaded back
        // in Program.cs on startup).
        services.AddHostedService<TaskStatusPersistenceService>();

        // Library Scan Queue
        services.AddSingleton<LibraryScanQueueService>();
        services.AddSingleton<ILibraryScanQueueService>(sp => sp.GetRequiredService<LibraryScanQueueService>());
        services.AddHostedService(sp => sp.GetRequiredService<LibraryScanQueueService>());

        // Library Watcher
        services.AddSingleton<LibraryWatcher>();
        services.AddHostedService(sp => sp.GetRequiredService<LibraryWatcher>());

        // Other Background Services
        services.AddHostedService<ThrottleMonitorService>();
        services.AddHostedService<RefreshTokenCleanupService>();
        services.AddHostedService<TranscodeSegmentCleanupService>();
        services.AddSingleton<MetadataRefreshService>();
        services.AddHostedService(sp => sp.GetRequiredService<MetadataRefreshService>());
        services.AddHostedService<HeroCacheWorker>();

        // Metadata Queue
        services.AddSingleton<MetadataQueueService>();
        services.AddSingleton<IMetadataQueue>(sp => sp.GetRequiredService<MetadataQueueService>());
        services.AddHostedService(sp => sp.GetRequiredService<MetadataQueueService>());

        // Metadata Retry Service
        services.AddSingleton<MetadataRetryService>();
        services.AddSingleton<IMetadataRetryService>(sp => sp.GetRequiredService<MetadataRetryService>());
        services.AddHostedService(sp => sp.GetRequiredService<MetadataRetryService>());

        // Image Download Queue
        services.AddSingleton<ImageDownloadQueueService>();
        services.AddSingleton<IImageDownloadQueue>(sp => sp.GetRequiredService<ImageDownloadQueueService>());
        services.AddHostedService(sp => sp.GetRequiredService<ImageDownloadQueueService>());

        // Daily database backup + rotation (P1-WI-001). Resolves a scope per cycle
        // because IBackupService / AppDbContext / ISettingsService are scoped.
        services.AddHostedService<BackupRotationService>();

        // Trickplay sprite-sheet backfill sweep (P2-WI-001).
        services.AddHostedService<TrickplayWorker>();

        // One-shot artwork repair after a database restore. Consumes the marker
        // PendingRestore drops on boot and re-fetches art that the (image-cache-excluding)
        // backup couldn't restore.
        services.AddHostedService<ArtworkRepairOnRestoreService>();

        // Outbound webhooks (P2-WI-004): singleton in-memory queue + drain worker.
        services.AddSingleton<IWebhookDispatcher, WebhookDispatcher>();
        services.AddHostedService<WebhookDispatchWorker>();

        return services;
    }
}
