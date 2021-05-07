// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.IO;
using System.Reflection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.StaticWebAssets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Microsoft.AspNetCore.Builder
{
    internal class WebHostEnvironment : IWebHostEnvironment
    {
        private static readonly NullFileProvider NullFileProvider = new();

        public WebHostEnvironment(Assembly? callingAssembly)
        {
            ContentRootPath = Directory.GetCurrentDirectory();

            ApplicationName = (callingAssembly ?? Assembly.GetEntryAssembly())?.GetName()?.Name ?? string.Empty;
            EnvironmentName = Environments.Production;

            // This feels wrong, but HostingEnvironment also sets WebRoot to "default!".
            WebRootPath = default!;

            // Default to /wwwroot if it exists.
            var wwwroot = Path.Combine(ContentRootPath, "wwwroot");
            if (Directory.Exists(wwwroot))
            {
                WebRootPath = wwwroot;
            }

            ContentRootFileProvider = NullFileProvider;
            WebRootFileProvider = NullFileProvider;

            ResolveFileProviders(new Configuration());
        }

        public void ApplyConfigurationSettings(IConfiguration configuration)
        {
            ApplicationName = configuration[WebHostDefaults.ApplicationKey] ?? ApplicationName;
            ContentRootPath = configuration[WebHostDefaults.ContentRootKey] ?? ContentRootPath;
            EnvironmentName = configuration[WebHostDefaults.EnvironmentKey] ?? EnvironmentName;
            WebRootPath = configuration[WebHostDefaults.ContentRootKey] ?? WebRootPath;

            ResolveFileProviders(configuration);
        }

        public void ApplyEnvironmentSettings(IWebHostBuilder genericWebHostBuilder)
        {
            genericWebHostBuilder.UseSetting(WebHostDefaults.ApplicationKey, ApplicationName);
            genericWebHostBuilder.UseSetting(WebHostDefaults.EnvironmentKey, EnvironmentName);
            genericWebHostBuilder.UseSetting(WebHostDefaults.ContentRootKey, ContentRootPath);
            genericWebHostBuilder.UseSetting(WebHostDefaults.WebRootKey, WebRootPath);
        }

        public void ResolveFileProviders(IConfiguration configuration)
        {
            if (Directory.Exists(ContentRootPath))
            {
                ContentRootFileProvider = new PhysicalFileProvider(ContentRootPath);
            }

            if (Directory.Exists(WebRootPath))
            {
                WebRootFileProvider = new PhysicalFileProvider(Path.Combine(ContentRootPath, WebRootPath));
            }

            if (this.IsDevelopment())
            {
                StaticWebAssetsLoader.UseStaticWebAssets(this, configuration);
            }
        }

        public string ApplicationName { get; set; }
        public string EnvironmentName { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; }
        public string ContentRootPath { get; set; }

        public IFileProvider WebRootFileProvider { get; set; }
        public string WebRootPath { get; set; }
    }
}
