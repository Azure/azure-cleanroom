// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace CgsUI;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Add services to the container.
        builder.Services.AddControllersWithViews();

        var app = builder.Build();

        // Configure the HTTP request pipeline.
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Home/Error");
        }

        app.UseStaticFiles();

        // Add defense-in-depth security headers, including a restrictive Content Security
        // Policy, to reduce the exploitability of any future markup-injection defects.
        app.Use(async (context, next) =>
        {
            context.Response.Headers.Append(
                "Content-Security-Policy",
                "default-src 'self'; " +
                "script-src 'self' 'unsafe-inline'; " +
                "style-src 'self' 'unsafe-inline' https://cdnjs.cloudflare.com " +
                "https://fonts.googleapis.com; " +
                "font-src 'self' https://cdnjs.cloudflare.com https://fonts.gstatic.com; " +
                "img-src 'self' data:; " +
                "connect-src 'self'; " +
                "object-src 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self'; " +
                "frame-ancestors 'none';");
            context.Response.Headers.Append("X-Content-Type-Options", "nosniff");
            context.Response.Headers.Append("X-Frame-Options", "DENY");
            context.Response.Headers.Append("Referrer-Policy", "no-referrer");
            await next();
        });

        app.UseRouting();

        app.UseAuthorization();

        app.MapControllerRoute(
            name: "default",
            pattern: "{controller=Home}/{action=Index}/{id?}");

        app.Run();
    }
}
