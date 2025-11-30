using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
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

// Library Management Services
builder.Services.AddSingleton<IFileSystem, FileSystem>();
builder.Services.AddSingleton<IProcessRunner, ProcessRunner>();
builder.Services.AddScoped<IFileScannerService, FileScannerService>();
builder.Services.AddHostedService<LibraryWatcher>();
builder.Services.AddHttpClient<WikidataProvider>();
builder.Services.AddHttpClient<TVMazeProvider>();
builder.Services.AddScoped<IMetadataProvider, WikidataProvider>();
builder.Services.AddScoped<IMetadataProvider, TVMazeProvider>();
builder.Services.AddScoped<IMetadataRouter, MetadataRouter>();
builder.Services.AddScoped<IFFmpegService, FFmpegService>();

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
    });

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
// builder.Services.AddOpenApi(); // Requires .NET 9

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    // app.MapOpenApi(); // Requires .NET 9
}

// app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// Seed the database
using (var scope = app.Services.CreateScope())
{
    await DbInitializer.InitializeAsync(scope.ServiceProvider);
}

app.Run();
