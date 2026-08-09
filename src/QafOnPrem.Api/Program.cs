using QafOnPrem.Api.Configuration;
using QafOnPrem.Api.Services.AppData;
using QafOnPrem.Api.Services.Auth;
using QafOnPrem.Api.Services.Integrations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();

builder.Services.AddProblemDetails();
builder.Services.AddControllers();
builder.Services.AddHealthChecks();
builder.Services.AddOpenApi();
builder.Services.Configure<CorsSettings>(builder.Configuration.GetSection(CorsSettings.SectionName));
builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
builder.Services.Configure<DevelopmentAuthSettings>(builder.Configuration.GetSection(DevelopmentAuthSettings.SectionName));
builder.Services.Configure<ScheduleProcessingSettings>(builder.Configuration.GetSection(ScheduleProcessingSettings.SectionName));
builder.Services.Configure<IntegrationProcessingSettings>(builder.Configuration.GetSection(IntegrationProcessingSettings.SectionName));
builder.Services.Configure<SqlIdentitySettings>(builder.Configuration.GetSection(SqlIdentitySettings.SectionName));
builder.Services.Configure<UploadStorageSettings>(builder.Configuration.GetSection(UploadStorageSettings.SectionName));

var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>() ?? new JwtSettings();
if (!isDevelopment && (string.IsNullOrWhiteSpace(jwtSettings.SigningKey) || string.Equals(jwtSettings.SigningKey, "replace-this-with-a-long-random-key-before-non-dev-use", StringComparison.Ordinal)))
{
    throw new InvalidOperationException("Jwt:SigningKey must be configured with a strong non-placeholder value outside development.");
}

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.SigningKey));

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !isDevelopment;
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidAudience = jwtSettings.Audience,
            IssuerSigningKey = signingKey,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization();
builder.Services.AddScoped<IDevelopmentAuthService, DevelopmentAuthService>();
builder.Services.AddScoped<ISqlIdentityService, SqlIdentityService>();
builder.Services.AddSingleton<ITestSuiteEditSessionService, InMemoryTestSuiteEditSessionService>();
builder.Services.AddScoped<ISqlAppDataService, SqlAppDataService>();
builder.Services.AddScoped<SqlIntegrationJobProcessor>();
builder.Services.AddScoped<IAuthService, CompositeAuthService>();
builder.Services.AddScoped<ISqlIdentityReadinessService, SqlIdentityReadinessService>();
builder.Services.AddHttpClient();
builder.Services.AddHostedService<SqlIdentityStartupValidationService>();
builder.Services.AddHostedService<ExecutionScheduleProcessingService>();
builder.Services.AddHostedService<IntegrationJobProcessingService>();
builder.Services.AddSingleton<ITokenFactory>(_ => new JwtTokenFactory(jwtSettings));

var allowedOrigins = builder.Configuration
    .GetSection(CorsSettings.SectionName)
    .Get<CorsSettings>()?
    .AllowedOrigins ?? [];

builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        if (allowedOrigins.Length == 0)
        {
            if (!isDevelopment)
            {
                throw new InvalidOperationException("Cors:AllowedOrigins must be configured outside development.");
            }

            policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod();
            return;
        }

        policy.WithOrigins(allowedOrigins)
            .AllowAnyHeader()
            .AllowAnyMethod();
    });
});

var app = builder.Build();
var uploadStorageSettings = app.Configuration.GetSection(UploadStorageSettings.SectionName).Get<UploadStorageSettings>() ?? new UploadStorageSettings();
var sharedUploadsPath = string.IsNullOrWhiteSpace(uploadStorageSettings.RootPath)
    ? null
    : Path.GetFullPath(uploadStorageSettings.RootPath);
IFileProvider? uploadsFileProvider = null;
if (!string.IsNullOrWhiteSpace(sharedUploadsPath) && Directory.Exists(sharedUploadsPath))
{
    uploadsFileProvider = new PhysicalFileProvider(sharedUploadsPath);
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseExceptionHandler();
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
if (uploadsFileProvider is not null)
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = uploadsFileProvider,
        RequestPath = "/uploads"
    });
}
app.UseCors("Frontend");
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHealthChecks("/health/live");

app.Run();

public partial class Program;

