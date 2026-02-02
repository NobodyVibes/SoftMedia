using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
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
        
        // Metadata Providers
        services.AddHttpClient<WikidataProvider>();
        services.AddHttpClient<TVMazeProvider>();
        services.AddHttpClient<OMDbProvider>();
        services.AddHttpClient<MusicBrainzProvider>();
        services.AddHttpClient<OpenLibraryProvider>();
        services.AddHttpClient<GameMetadataProvider>();
        
        services.AddScoped<IMetadataProvider, WikidataProvider>();
        services.AddScoped<IMetadataProvider, TVMazeProvider>();
        services.AddScoped<IMetadataProvider, OMDbProvider>();
        services.AddScoped<IMetadataProvider, MusicBrainzProvider>();
        services.AddScoped<IMetadataProvider, EmbeddedMusicProvider>();
        services.AddScoped<IMetadataProvider, OpenLibraryProvider>();
        services.AddScoped<IMetadataProvider, GameMetadataProvider>();
        services.AddScoped<IMetadataProvider, ExifMetadataProvider>();
        
        services.AddScoped<EmbeddedMusicProvider>(); 
        services.AddScoped<MetadataAggregator>();
        services.AddScoped<IMetadataRouter, MetadataRouter>();

        // Media Analysis Strategies (Strategy Pattern for type-specific analysis)
        services.AddScoped<IMediaAnalysisStrategy, VideoAnalysisStrategy>();
        services.AddScoped<IMediaAnalysisStrategy, AudioAnalysisStrategy>();
        services.AddScoped<IMediaAnalysisService, MediaAnalysisService>();

        // Media & Transcoding Services
        services.AddScoped<IFFmpegService, FFmpegService>();
        services.AddScoped<IStreamPlanService, StreamPlanService>();
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
        services.AddScoped<IMediaRetrievalService, MediaRetrievalService>();
        services.AddScoped<IUserMediaInteractionService, UserMediaInteractionService>();
        
        // System / Infrastructure
        services.AddScoped<ISettingsService, SettingsService>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<IUserPreferencesService, UserPreferencesService>();
        services.AddSingleton<RateLimiterFactory>();
        
        services.AddSingleton<IMediaNotificationService, MediaNotificationService>();

        // Image Cache Client
        services.AddHttpClient<ImageCacheService>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SoftMedia/1.0 (https://github.com/NobodyVibes/SoftMedia)");
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

        // Image Cache
        services.AddSingleton<BackgroundImageCacheService>();
        services.AddSingleton<IBackgroundImageCacheService>(sp => sp.GetRequiredService<BackgroundImageCacheService>());
        services.AddHostedService(sp => sp.GetRequiredService<BackgroundImageCacheService>());

        // Other Background Services
        services.AddHostedService<ThrottleMonitorService>();
        services.AddSingleton<MetadataRefreshService>();
        services.AddHostedService(sp => sp.GetRequiredService<MetadataRefreshService>());
        services.AddHostedService<HeroCacheWorker>();

        return services;
    }
}
