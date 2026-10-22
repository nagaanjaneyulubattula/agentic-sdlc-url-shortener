using UrlShortener.Api.Core;
using UrlShortener.Api.Features;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.Services.AddUrlShortenerCore(builder.Configuration);

var features = FeatureRegistry.All.OrderBy(f => f.Order).ToList();
foreach (var feature in features)
{
    feature.ConfigureServices(builder.Services, builder.Configuration);
}

var app = builder.Build();

app.UseMiddleware<ErrorHandlingMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<RequestGateMiddleware>();
app.UseRouting();

foreach (var feature in features)
{
    feature.MapEndpoints(app);
}

app.Run();

public partial class Program { }
