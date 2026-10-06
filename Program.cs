using dotenv.net;
using Microsoft.EntityFrameworkCore;
using Buffet_Restaurant_Managment_System_API.Data;
using CloudinaryDotNet;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;
using Microsoft.OpenApi.Models;
using Buffet_Restaurant_Managment_System_API.Hubs;
using Buffet_Restaurant_Managment_System_API.Services;

var builder = WebApplication.CreateBuilder(args);

// --- JWT Configuration ---
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrEmpty(jwtKey))
{
    Console.WriteLine("WARNING: JWT Key is missing!");
}

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(options =>
{
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"] ?? "DefaultFallbackSecretKey1234567890"))
    };
});

// --- CORS Configuration ---
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAngular", policy =>
    {
        policy.WithOrigins(
                    "http://localhost:4200",
                    "https://localhost:4200",
                    "https://buffet-restaurant-management-system.vercel.app",
                    "http://localhost:3000"
                )
                // 🟢 ใช้ SetIsOriginAllowed ในรูปแบบที่ตรวจสอบเฉพาะ Domain/Subdomain ของ Vercel และ Localhost
                .SetIsOriginAllowed(origin => 
                    string.IsNullOrEmpty(origin) ||
                    origin.StartsWith("http://localhost") ||
                    origin.StartsWith("https://localhost") ||
                    origin.EndsWith(".vercel.app")
                )
                .AllowAnyMethod()
                .AllowAnyHeader()
                .AllowCredentials(); // 🟢 จำเป็นต้องใช้คู่กับ SignalR (WebSockets/Negotiate)
    });
});

builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// --- Swagger Configuration ---
builder.Services.AddSwaggerGen(option =>
{
    option.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Buffet Restaurant Management System API",
        Version = "v1"
    });
    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "JWT Authentication",
        Description = "Enter JWT Bearer token **_only_**",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Reference = new OpenApiReference
        {
            Id = JwtBearerDefaults.AuthenticationScheme,
            Type = ReferenceType.SecurityScheme
        }
    };
    option.AddSecurityDefinition(securityScheme.Reference.Id, securityScheme);
    option.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { securityScheme, new string[] { } }
    });
});

// --- Cloudinary Configuration ---
DotEnv.Load();
var cloudName = Environment.GetEnvironmentVariable("CLOUDINARY_NAME");
var apiKey = Environment.GetEnvironmentVariable("CLOUDINARY_API_KEY");
var apiSecret = Environment.GetEnvironmentVariable("CLOUDINARY_API_SECRET");
var cloudinaryAccount = new Account(cloudName, apiKey, apiSecret);
var cloudinary = new Cloudinary(cloudinaryAccount);
builder.Services.AddSingleton(cloudinary);

var Apikey_payment = Environment.GetEnvironmentVariable("API_KEY_PAYMENT");

// --- Database Connection ---
var connectionTemplate = builder.Configuration.GetConnectionString("DefaultConnection");
var connectionString = connectionTemplate?
    .Replace("{DB_HOST}", Environment.GetEnvironmentVariable("DB_HOST"))
    .Replace("{DB_PORT}", Environment.GetEnvironmentVariable("DB_PORT"))
    .Replace("{DB_DATABASE}", Environment.GetEnvironmentVariable("DB_DATABASE"))
    .Replace("{DB_USERNAME}", Environment.GetEnvironmentVariable("DB_USERNAME"))
    .Replace("{DB_PASSWORD}", Environment.GetEnvironmentVariable("DB_PASSWORD"));


if (!string.IsNullOrEmpty(connectionString) && !connectionString.Contains("Guid Format", StringComparison.OrdinalIgnoreCase))
{
    connectionString += (connectionString.TrimEnd().EndsWith(";") ? "" : ";") + "Guid Format=None;";
}

builder.Services.AddDbContext<restaurantDbContext>(options =>
    options.UseMySql(connectionString, ServerVersion.AutoDetect(connectionString)));

builder.Services.AddHttpClient<PromptPayService>();
builder.Services.AddMemoryCache();

var app = builder.Build();

// --- Pipeline Order (สำคัญมากสำหรับ CORS) ---
app.UseSwagger();
app.UseSwaggerUI();

app.UseRouting(); // 🟢 1. ต้องเรียก UseRouting() ก่อน UseCors()

app.UseCors("AllowAngular"); // 🟢 2. เรียก UseCors() หลัง UseRouting() และก่อน Authentication

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<tableStatusHub>("/tableStatusHub");

app.Run();