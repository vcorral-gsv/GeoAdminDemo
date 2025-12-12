using GeoAdminDemo.Data;
using GeoAdminDemo.Middlewares;
using GeoAdminDemo.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// EF Core + SQL Server
builder.Services.AddDbContext<AppDbContext>(options =>
{
    var cs = builder.Configuration.GetConnectionString("DefaultConnection")
             ?? throw new InvalidOperationException("Falta la cadena 'DefaultConnection'.");
    options.UseSqlServer(cs, sql =>
    {
        sql.UseNetTopologySuite();
    });
});

builder.Services.AddHttpContextAccessor();

// HttpClients
builder.Services.AddHttpClient("esri-admin", c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient("arcgis-token", c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient("arcgis-geocode", c => c.Timeout = TimeSpan.FromMinutes(10));

// Servicios
builder.Services.AddScoped<IArcgisTokenProvider, ArcgisTokenProvider>();
builder.Services.AddScoped<ArcgisGeocodingService>();
builder.Services.AddScoped<EsriAdminImportService>();
builder.Services.AddScoped<GeoResolveService>();

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// Middleware de excepciones (antes de todo)
app.UseMiddleware<ExceptionHandlingMiddleware>();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.Run();
