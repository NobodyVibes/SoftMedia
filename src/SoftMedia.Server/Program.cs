using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Microsoft.AspNetCore.RateLimiting;
using System.Threading.RateLimiting;
using SoftMedia.Server.Data;
using SoftMedia.Server.Services;
using SoftMedia.Server.Services.Abstractions;
using SoftMedia.Server.Services.Metadata;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddScoped<IPasswordHasher, PasswordHasher>();
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddHttpClient();

// Library Management Services
builder.Services.AddSingleton<IFileSystem, FileSystem>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddScoped<IFileScannerService, FileScannerService>();
builder.Services.AddSingleton<LibraryScanQueueService>();
builder.Services.AddSingleton<ILibraryScanQueueService>(sp => sp.GetRequiredService<LibraryScanQueueService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<LibraryScanQueueService>());
builder.Services.AddSingleton<LibraryWatcher>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<LibraryWatcher>());
builder.Services.AddHttpClient<WikidataProvider>();
builder.Services.AddHttpClient<TVMazeProvider>();
builder.Services.AddScoped<IMetadataProvider, WikidataProvider>();
builder.Services.AddScoped<IMetadataProvider, TVMazeProvider>();
builder.Services.AddHttpClient<MusicBrainzProvider>();
builder.Services.AddScoped<EmbeddedMusicProvider>(); // No HttpClient needed
builder.Services.AddHttpClient<OpenLibraryProvider>();
builder.Services.AddHttpClient<GameMetadataProvider>();
builder.Services.AddScoped<IMetadataProvider, MusicBrainzProvider>();
builder.Services.AddScoped<IMetadataProvider, EmbeddedMusicProvider>();
builder.Services.AddScoped<IMetadataProvider, OpenLibraryProvider>();
builder.Services.AddScoped<IMetadataProvider, GameMetadataProvider>();
builder.Services.AddScoped<IMetadataProvider, ExifMetadataProvider>();
builder.Services.AddScoped<IMetadataRouter, MetadataRouter>();
builder.Services.AddScoped<MetadataAggregator>();
builder.Services.AddScoped<IFFmpegService, FFmpegService>();
builder.Services.AddScoped<IStreamPlanService, StreamPlanService>();
builder.Services.AddSingleton<IProcessController, ProcessController>(); // Cross-platform process suspend/resume
builder.Services.AddSingleton<TranscodeService>(); // Singleton to maintain process tracking across requests
builder.Services.AddHostedService<ThrottleMonitorService>(); // Background service for throttling
builder.Services.AddScoped<ISettingsService, SettingsService>();


builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        var allowAnyOriginForLAN = builder.Configuration.GetValue<bool>("Cors:AllowAnyOriginForLAN");
        if (allowAnyOriginForLAN)
        {
            // Allow any origin for LAN access - required since we can't know all possible local IPs
            policy.SetIsOriginAllowed(_ => true)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
        else
        {
            var origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
            policy.WithOrigins(origins)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        }
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddFixedWindowLimiter("fixed", limiterOptions =>
    {
        limiterOptions.PermitLimit = 100;
        limiterOptions.Window = TimeSpan.FromSeconds(10);
        limiterOptions.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        limiterOptions.QueueLimit = 5;
    });
});

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["JwtSettings:Issuer"],
            ValidAudience = builder.Configuration["JwtSettings:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["JwtSettings:Secret"]!))
        };
        
        // Support JWT from query parameter for streaming endpoints
        // This is required because browser media elements can't set Authorization headers
        options.Events = new Microsoft.AspNetCore.Authentication.JwtBearer.JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var path = context.HttpContext.Request.Path;
                // Only extract from query for streaming/transcode/media endpoints
                // This is required because browser media elements can't set Authorization headers
                if (path.StartsWithSegments("/api/transcode") || 
                    path.StartsWithSegments("/api/v1/stream") ||
                    path.StartsWithSegments("/api/media"))
                {
                    var token = context.Request.Query["token"];
                    if (!string.IsNullOrEmpty(token))
                    {
                        context.Token = token;
                    }
                }
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "SoftMedia API", Version = "v1" });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization header using the Bearer scheme. Example: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// app.UseHttpsRedirection();

app.UseCors("AllowFrontend");
app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Seed the database
using (var scope = app.Services.CreateScope())
{
    await DbInitializer.InitializeAsync(scope.ServiceProvider);
}

app.Run();
