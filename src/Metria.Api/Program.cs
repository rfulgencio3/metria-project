using Metria.Api.Data;
using Metria.Api.Endpoints;
using Metria.Api.Repositories;
using Metria.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using System.Text;
using System.Text.Json.Serialization;

// Load .env file
DotNetEnv.Env.Load();

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Stripe API Key (from env STRIPE_SECRET_KEY or config Stripe:SecretKey)
var stripeSecret = Environment.GetEnvironmentVariable("STRIPE_SECRET_KEY") ?? config["Stripe:SecretKey"];
if (!string.IsNullOrWhiteSpace(stripeSecret))
{
    Stripe.StripeConfiguration.ApiKey = stripeSecret;
}

var corsOrigins = builder.Environment.IsDevelopment()
    ? new[] { "http://localhost:3000", "http://localhost:5173" }
    : new[] { Environment.GetEnvironmentVariable("FRONTEND_ORIGIN") ?? "https://seu-dominio-frontend" };

builder.Services.AddCors(o =>
{
    o.AddPolicy("frontend", p =>
        p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials());
});

var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(opts =>
    {
        opts.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = config["Jwt:Issuer"],
            ValidAudience = config["Jwt:Audience"],
            IssuerSigningKey = key,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

var conn = Environment.GetEnvironmentVariable("POSTGRES_CONNECTION")
           ?? Environment.GetEnvironmentVariable("DATABASE_URL")
           ?? config.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(conn))
{
    throw new InvalidOperationException("Missing Postgres connection string. Set POSTGRES_CONNECTION, DATABASE_URL or ConnectionStrings:Postgres");
}

conn = NormalizePostgresConnectionString(conn);

builder.Services.AddDbContext<AppDbContext>(opt =>
    opt.UseNpgsql(conn, o => o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery)));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ISubscriptionRepository, SubscriptionRepository>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

var app = builder.Build();
var enableSwagger = app.Environment.IsDevelopment() ||
                   string.Equals(Environment.GetEnvironmentVariable("ENABLE_SWAGGER"), "true", StringComparison.OrdinalIgnoreCase);

app.UseCors("frontend");
app.UseAuthentication();
app.UseAuthorization();

if (enableSwagger)
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.RoutePrefix = "";
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Metria.Api v1");
    });
}

app.MapGet("/health-check", () => Results.Ok(new { ok = true, timeUtc = DateTime.UtcNow }))
   .AllowAnonymous();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try { db.Database.Migrate(); } catch { }
}

app.MapAuthEndpoints(config, key);
app.MapUserEndpoints();
app.MapBillingEndpoints();
app.MapAssessmentEndpoints();
app.MapGoalsEndpoints();

app.Run();

static string NormalizePostgresConnectionString(string raw)
{
    if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
        !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
    {
        return raw;
    }

    var uri = new Uri(raw);
    var userInfo = uri.UserInfo.Split(':', 2);
    if (userInfo.Length != 2)
    {
        throw new InvalidOperationException("Invalid DATABASE_URL format: missing username/password.");
    }

    var builder = new NpgsqlConnectionStringBuilder
    {
        Host = uri.Host,
        Port = uri.Port > 0 ? uri.Port : 5432,
        Database = uri.AbsolutePath.Trim('/'),
        Username = Uri.UnescapeDataString(userInfo[0]),
        Password = Uri.UnescapeDataString(userInfo[1]),
        SslMode = SslMode.Require
    };

    if (!string.IsNullOrWhiteSpace(uri.Query))
    {
        var query = uri.Query.TrimStart('?');
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;

            var key = Uri.UnescapeDataString(kv[0]);
            var value = Uri.UnescapeDataString(kv[1]);

            if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase))
            {
                if (Enum.TryParse<SslMode>(value, true, out var mode))
                {
                    builder.SslMode = mode;
                }
            }
            else if (key.Equals("ssl", StringComparison.OrdinalIgnoreCase))
            {
                if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    builder.SslMode = SslMode.Require;
                }
                else if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
                {
                    builder.SslMode = SslMode.Disable;
                }
            }
        }
    }

    return builder.ConnectionString;
}