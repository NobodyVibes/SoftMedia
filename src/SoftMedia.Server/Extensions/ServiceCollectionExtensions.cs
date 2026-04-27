using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using System.Threading.RateLimiting;
using SoftMedia.Server.Services.Identity;
using SoftMedia.Server.Services.Infrastructure;
using SoftMedia.Server.Services.Media;
using SoftMedia.Server.Services.Scanning;
using SoftMedia.Server.Services.Transcoding;
using SoftMedia.Server.Services.Metadata;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Background;
using SoftMedia.Server.Services.Media.Strategies;
using SoftMedia.Server.Hubs;
using SoftMedia.Server.Helpers;

namespace SoftMedia.Server.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddIdentityServices(this IServiceCollection services, IConfiguration config)
    {
        services.AddScoped<IPasswordHasher, PasswordHasher>();
        services.AddScoped<ITokenService, TokenService>();
        services.AddScoped<IRefreshTokenService, RefreshTokenService>();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
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
                        var path = context.HttpContext.Request.Path;
                        if (path.StartsWithSegments("/api/transcode") ||
                            path.StartsWithSegments("/api/v1/stream") ||
                            path.StartsWithSegments("/api/v1/audio") ||
                            path.StartsWithSegments("/api/v1/books") ||
                            path.StartsWithSegments("/api/v1/image") ||
                            path.StartsWithSegments("/api/v1/music") ||
                            path.StartsWithSegments("/api/media") ||
                            path.StartsWithSegments("/hubs/media"))
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
                    }
                };
            });

        return services;
    }

    public static IServiceCollection AddMediaServices(this IServiceCollection services)
    {
        services.AddHttpClient();

        // Scanners
        services.AddScoped<IScannerOrchestrator, ScannerOrchestrator>();
        services.AddScoped<IMediaScanner, MusicScanner>();
        services.AddScoped<IMediaScanner, TvScanner>();
        services.AddScoped<IMediaScanner, MovieScanner>();
        services.AddScoped<IMediaScanner, GameScanner>();
        services.AddScoped<IMediaScanner, BookScanner>();
        services.AddSingleton<IBookMetadataExtractor, BookMetadataExtractor>();

        services.AddTransient<SoftMediaUserAgentHandler>();
        
        // Metadata Providers
        services.AddHttpClient<WikidataProvider>().AddHttpMessageHandler<SoftMediaUserAgentHandler>();
        services.AddHttpClient<TVMazeProvider>().AddHttpMessageHandler<SoftMediaUserAgentHandler>();
        services.AddHttpClient<OMDbProvider>().AddHttpMessageHandler<SoftMediaUserAgentHandler>();
        services.AddHttpClient<MusicBrainzProvider>().AddHttpMessageHandler<SoftMediaUserAgentHandler>();
        services.AddHttpClient<OpenLibraryProvider>(c => c.Timeout = TimeSpan.FromSeconds(15))
                .AddHttpMessageHandler<SoftMediaUserAgentHandler>();
        services.AddHttpClient<GameMetadataProvider>().AddHttpMessageHandler<SoftMediaUserAgentHandler>();
        services.AddHttpClient<ComicWikidataProvider>().AddHttpMessageHandler<SoftMediaUserAgentHandler>();

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
        
        services.AddScoped<EmbeddedMusicProvider>(); 
        services.AddScoped<ITvMetadataEnricher, TvMetadataEnricher>();
        services.AddScoped<IMetadataAggregator, MetadataAggregator>();
        services.AddScoped<IMetadataRouter, MetadataRouter>();

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
        services.AddSingleton<IFileSystem, FileSystem>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        
        services.AddScoped<ISubtitleService, SubtitleService>();
        services.AddScoped<ITranscodeProfileBuilder, TranscodeProfileBuilder>();
        services.AddScoped<IHlsManifestService, HlsManifestService>();
        services.AddScoped<IRecommendationService, RecommendationService>();
        services.AddScoped<IMusicImageService, MusicImageService>();
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

        return services;
    }

    public const string AuthRateLimitPolicy = "auth";
    public const string ImageProxyRateLimitPolicy = "image-proxy";

    // Policy "auth": per-IP sliding window on /auth/login and /auth/signup to defeat
    // credential stuffing. 15 attempts per minute is comfortable headroom for users
    // who fat-finger their password a few times while still capping automated attacks.
    // Sliding window (vs fixed) prevents the "5 fails at 12:00:59 then 5 more at
    // 12:01:00" burst that fixed-window allows.
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
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown-ip",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 30,
                        Window = TimeSpan.FromMinutes(1),
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
        });
        return services;
    }

    public static IServiceCollection AddBackgroundServices(this IServiceCollection services)
    {
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

        return services;
    }
}
