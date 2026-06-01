using CityMapApp.Components;
using CityMapApp.Data;
using CityMapApp.Models;
using CityMapApp.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Shared.DTOs;
using Shared.Requests;
using Shared.Responses;

var builder = WebApplication.CreateBuilder(args);
const string userTokenCookieName = "citymap-user-token";

builder.AddServiceDefaults();

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddScoped(sp =>
{
    var navigationManager = sp.GetRequiredService<NavigationManager>();
    return new HttpClient { BaseAddress = new Uri(navigationManager.BaseUri) };
});

builder.Services.AddDbContext<CityMapDbContext>(options =>
    options.UseSqlite(
        builder.Configuration.GetConnectionString("CityMapDb") ?? "Data Source=citymap.db"
    )
);

builder.Services.AddHttpClient<IGeocodingService, NominatimGeocodingService>(client =>
{
    client.BaseAddress = new Uri("https://nominatim.openstreetmap.org/");
    client.DefaultRequestHeaders.UserAgent.ParseAdd("CityMapApp/1.0");
});

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter(
        "submission",
        limiterOptions =>
        {
            limiterOptions.PermitLimit = 5;
            limiterOptions.Window = TimeSpan.FromHours(1);
            limiterOptions.QueueLimit = 0;
        }
    );

    options.AddFixedWindowLimiter(
        "map",
        limiterOptions =>
        {
            limiterOptions.PermitLimit = 60;
            limiterOptions.Window = TimeSpan.FromMinutes(1);
            limiterOptions.QueueLimit = 0;
        }
    );
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<CityMapDbContext>();
    dbContext.Database.EnsureCreated();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
}
else
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapPost(
        "/api/submissions",
        async (
            SubmissionRequest request,
            HttpContext httpContext,
            CityMapDbContext dbContext,
            IGeocodingService geocodingService,
            CancellationToken cancellationToken
        ) =>
        {
            if (!IsValidSubmission(request))
            {
                return Results.BadRequest(new SubmissionResponse(false, "Invalid city or state"));
            }

            var existingToken = httpContext.Request.Cookies[userTokenCookieName];

            if (!string.IsNullOrWhiteSpace(existingToken))
            {
                var alreadySubmitted = await dbContext
                    .Submissions.AsNoTracking()
                    .AnyAsync(
                        submission => submission.UserToken == existingToken,
                        cancellationToken
                    );

                if (alreadySubmitted)
                {
                    return Results.Ok(new SubmissionResponse(false, "Already submitted"));
                }
            }

            var geocodeResult = await geocodingService.GeocodeAsync(
                request.City,
                request.State,
                cancellationToken
            );

            if (geocodeResult is null)
            {
                return Results.BadRequest(
                    new SubmissionResponse(false, "Unable to locate city/state")
                );
            }

            var token = string.IsNullOrWhiteSpace(existingToken)
                ? Guid.NewGuid().ToString("N")
                : existingToken;

            var submission = new Submission
            {
                City = request.City.Trim(),
                State = request.State.Trim(),
                Latitude = geocodeResult.Latitude,
                Longitude = geocodeResult.Longitude,
                CreatedUtc = DateTime.UtcNow,
                UserToken = token,
            };

            dbContext.Submissions.Add(submission);
            await dbContext.SaveChangesAsync(cancellationToken);

            httpContext.Response.Cookies.Append(
                userTokenCookieName,
                token,
                new CookieOptions
                {
                    HttpOnly = true,
                    Secure = true,
                    SameSite = SameSiteMode.Lax,
                    Expires = DateTimeOffset.UtcNow.AddYears(5),
                }
            );

            return Results.Ok(new SubmissionResponse(true, null));
        }
    )
    .RequireRateLimiting("submission");

app.MapGet(
    "/api/submissions/me",
    async (
        HttpContext httpContext,
        CityMapDbContext dbContext,
        CancellationToken cancellationToken
    ) =>
    {
        var existingToken = httpContext.Request.Cookies[userTokenCookieName];
        if (string.IsNullOrWhiteSpace(existingToken))
        {
            return Results.Ok(new SubmissionStatusResponse(false));
        }

        var hasSubmitted = await dbContext
            .Submissions.AsNoTracking()
            .AnyAsync(submission => submission.UserToken == existingToken, cancellationToken);

        return Results.Ok(new SubmissionStatusResponse(hasSubmitted));
    }
);

app.MapGet(
        "/api/map",
        async (CityMapDbContext dbContext, CancellationToken cancellationToken) =>
        {
            var groupedPins = await dbContext
                .Submissions.AsNoTracking()
                .GroupBy(submission => new
                {
                    submission.City,
                    submission.State,
                    submission.Latitude,
                    submission.Longitude,
                })
                .Select(group => new
                {
                    group.Key.City,
                    group.Key.State,
                    group.Key.Latitude,
                    group.Key.Longitude,
                    Count = group.Count(),
                })
                .ToListAsync(cancellationToken);

            var pins = groupedPins
                .Select(pin => new MapPinDto(
                    pin.City,
                    pin.State,
                    pin.Latitude,
                    pin.Longitude,
                    pin.Count
                ))
                .OrderByDescending(pin => pin.Count)
                .ToList();

            return Results.Ok(pins);
        }
    )
    .RequireRateLimiting("map");

app.MapDefaultEndpoints();

app.Run();

static bool IsValidSubmission(SubmissionRequest request)
{
    if (string.IsNullOrWhiteSpace(request.City) || string.IsNullOrWhiteSpace(request.State))
    {
        return false;
    }

    return request.City.Trim().Length <= 100 && request.State.Trim().Length <= 50;
}
